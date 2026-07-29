using System.Net;
using System.Net.Sockets;
using OpenAudioLink.Core.Audio;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Core.Protocol;
using OpenAudioLink.Hub;
using OpenAudioLink.Hub.Configuration;
using OpenAudioLink.Hub.Services;

// Anchor the content root to the executable so appsettings.json and wwwroot
// are found regardless of the launch directory (double-click, shortcut,
// Windows service).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Runs as a plain console app during development and as a Windows service
// when installed; UseWindowsService is a no-op on other platforms.
builder.Host.UseWindowsService(options => options.ServiceName = "OpenAudioLink Hub");

var dataDirectory = builder.Configuration["Hub:DataDirectory"];
if (string.IsNullOrEmpty(dataDirectory))
{
    dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
}
var configStore = new HubConfigStore(dataDirectory);

builder.Services.AddSingleton(configStore);
builder.Services.AddSingleton(configStore.LoadOrCreate());
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton(new FirmwareStore(dataDirectory));
builder.Services.AddSingleton<RtpStreamer>();
builder.Services.AddHttpClient<DeviceCommandClient>();
builder.Services.AddHostedService<DiscoveryService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Firmware images served to devices for OTA pulls (protocol/OTA.md).
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        app.Services.GetRequiredService<FirmwareStore>().DirectoryPath),
    RequestPath = "/firmware",
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
});

app.MapGet("/api/health", (HubConfig config) => Results.Ok(new
{
    status = "ok",
    id = config.Id,
    name = config.Name,
    version = HubInfo.Version,
    protocol = ProtocolSuite.Version,
}));

app.MapGet("/api/devices", (DeviceRegistry registry) => Results.Ok(registry.Snapshot()));

app.MapGet("/api/devices/{id}", (string id, DeviceRegistry registry) =>
    registry.TryGet(id, out var device) ? Results.Ok(device) : Results.NotFound());

app.MapGet("/api/firmware", (FirmwareStore store) => Results.Ok(store.List()));

app.MapPost("/api/firmware", async (HttpRequest request, FirmwareStore store, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "expected multipart form upload" });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "no file provided" });
    }

    await using var content = file.OpenReadStream();
    try
    {
        var saved = await store.SaveAsync(file.FileName, content, cancellationToken);
        return saved is null
            ? Results.BadRequest(new { error = "invalid file name; expected a plain .bin name" })
            : Results.Ok(saved);
    }
    catch (InvalidFirmwareImageException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/devices/{id}/reboot",
    async (string id, DeviceRegistry registry, DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }
    var ok = await commands.RebootAsync(device, cancellationToken);
    return ok ? Results.Ok(new { status = "rebooting" }) : Results.StatusCode(502);
});

app.MapPost("/api/devices/{id}/ota",
    async (string id, OtaRequest request, DeviceRegistry registry, FirmwareStore store,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }
    if (string.IsNullOrEmpty(request.File) || !store.Exists(request.File))
    {
        return Results.BadRequest(new { error = "unknown firmware file" });
    }
    var ok = await commands.StartOtaAsync(device, request.File, cancellationToken);
    return ok ? Results.Ok(new { status = "accepted" }) : Results.StatusCode(502);
});

// --- Audio streaming (Phase 3) ---------------------------------------
// One stream at a time, from either a generated tone (diagnostics) or
// this computer's audio (the real Producer path).

static IResult ResolveDestinations(
    StreamRequest request, HttpContext context, DeviceRegistry registry, out List<IPAddress> destinations)
{
    // Each entry is either a device id or an IP address. Naming a device
    // means the address follows it if DHCP moves it; a literal address
    // covers machines that are not OpenAudioLink devices at all, such as a
    // PC running a player. With none given, the caller is the destination,
    // so a browser can aim a stream at the machine showing the page.
    destinations = [];

    var requested = new List<string>();
    if (request.Destinations is { Count: > 0 })
    {
        requested.AddRange(request.Destinations);
    }
    if (!string.IsNullOrWhiteSpace(request.DeviceId))
    {
        requested.Add(request.DeviceId);
    }
    if (!string.IsNullOrWhiteSpace(request.Address))
    {
        requested.Add(request.Address);
    }

    if (requested.Count == 0)
    {
        var caller = context.Connection.RemoteIpAddress;
        if (caller is not null && caller.IsIPv4MappedToIPv6)
        {
            caller = caller.MapToIPv4();
        }
        if (IPAddress.IPv6Loopback.Equals(caller))
        {
            caller = IPAddress.Loopback;
        }
        if (caller is null || caller.AddressFamily != AddressFamily.InterNetwork)
        {
            return Results.BadRequest(new { error = "could not determine an IPv4 destination" });
        }
        destinations.Add(caller);
        return Results.Empty;
    }

    // Replication cost is linear per receiver, so a runaway list is capped
    // rather than quietly overloading the sender. See docs/DECISIONS.md.
    if (requested.Count > StreamLimits.MaxDestinations)
    {
        return Results.BadRequest(new
        {
            error = $"at most {StreamLimits.MaxDestinations} destinations per stream",
        });
    }

    foreach (var entry in requested.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        IPAddress? resolved;
        if (registry.TryGet(entry, out var device))
        {
            if (!IPAddress.TryParse(device.Address, out resolved))
            {
                return Results.BadRequest(new { error = $"device '{entry}' has no usable address" });
            }
        }
        else if (!IPAddress.TryParse(entry, out resolved))
        {
            return Results.BadRequest(new { error = $"'{entry}' is neither a known device nor an IP address" });
        }

        if (resolved.AddressFamily != AddressFamily.InterNetwork)
        {
            return Results.BadRequest(new { error = $"'{entry}' is not an IPv4 address" });
        }
        destinations.Add(resolved);
    }

    return Results.Empty;
}

static AudioStreamFormat BuildFormat(StreamRequest request) => new()
{
    Encoding = string.Equals(request.Encoding, "L16", StringComparison.OrdinalIgnoreCase)
        ? PcmEncoding.L16
        : PcmEncoding.L24,
    PacketMilliseconds = request.PacketMilliseconds ?? 5,
};

app.MapGet("/api/stream", (RtpStreamer streamer) => Results.Ok(streamer.Status));

app.MapDelete("/api/stream", async (RtpStreamer streamer) =>
{
    await streamer.StopAsync();
    return Results.Ok(streamer.Status);
});

app.MapPost("/api/stream/test-tone", async (
    StreamRequest request, HttpContext context, DeviceRegistry registry, RtpStreamer streamer) =>
{
    var failure = ResolveDestinations(request, context, registry, out var destinations);
    if (destinations.Count == 0)
    {
        return failure;
    }

    var port = request.Port ?? 41100;
    if (port is < 1 or > 65535)
    {
        return Results.BadRequest(new { error = "port out of range" });
    }

    try
    {
        var format = BuildFormat(request);
        format.Validate();
        var tone = new SineToneSource(format, request.FrequencyHz ?? 1000.0);
        return Results.Ok(await streamer.StartAsync("test-tone", tone, destinations, port, format));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/stream/system-audio", async (
    StreamRequest request, HttpContext context, DeviceRegistry registry, RtpStreamer streamer) =>
{
    if (!OperatingSystem.IsWindows())
    {
        return Results.BadRequest(new { error = "system audio capture requires Windows" });
    }

    var failure = ResolveDestinations(request, context, registry, out var destinations);
    if (destinations.Count == 0)
    {
        return failure;
    }

    var port = request.Port ?? 41100;
    if (port is < 1 or > 65535)
    {
        return Results.BadRequest(new { error = "port out of range" });
    }

    try
    {
        var format = BuildFormat(request);
        format.Validate();
        var capture = new SystemAudioSource(format);
        return Results.Ok(await streamer.StartAsync("system-audio", capture, destinations, port, format));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (NotSupportedException ex)
    {
        // Endpoint sample-rate or format mismatch: the message tells the
        // user exactly what to change in Windows Sound settings.
        return Results.BadRequest(new { error = ex.Message });
    }
});

// SDP for the running stream, so receivers can be pointed at a URL:
//   ffplay -protocol_whitelist file,rtp,udp,http -i http://<hub>:41080/api/stream.sdp
app.MapGet("/api/stream.sdp", (HttpContext context, RtpStreamer streamer) =>
{
    var status = streamer.Status;
    if (!status.Running || status.Destinations.Count == 0)
    {
        return Results.NotFound(new { error = "no stream is running" });
    }

    // Describes the first destination; every destination receives the
    // identical stream, so the description differs only in its address.
    var origin = context.Connection.LocalIpAddress?.ToString() ?? "0.0.0.0";
    var sdp = SessionDescription.Build(
        streamer.Format, origin, status.Destinations[0], status.Port,
        sessionName: $"OpenAudioLink {status.Description}");

    return Results.Text(sdp, "application/sdp");
});

app.Run();

internal sealed record OtaRequest(string? File);

internal static class StreamLimits
{
    /// <summary>
    /// Planning threshold from docs/DECISIONS.md: unicast replication
    /// costs roughly 2.37 Mbit/s and 200 packets/s per receiver, so a
    /// stream is capped rather than allowed to overload its sender.
    /// </summary>
    public const int MaxDestinations = 8;
}

internal sealed record StreamRequest(
    IReadOnlyList<string>? Destinations,
    string? Address,
    string? DeviceId,
    int? Port,
    string? Encoding,
    double? FrequencyHz,
    int? PacketMilliseconds);
