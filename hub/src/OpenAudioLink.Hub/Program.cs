using System.Net;
using System.Net.Sockets;
using OpenAudioLink.Core.Audio;
using OpenAudioLink.Core.CastPoints;
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
builder.Services.AddSingleton(new CastPointStore(dataDirectory));
builder.Services.AddSingleton<RtpStreamer>();
builder.Services.AddHttpClient<DeviceCommandClient>();
// A short timeout on purpose: a node that does not answer promptly is a
// node whose reading would be stale anyway, and the poll comes round again.
builder.Services.AddHttpClient(nameof(DeviceStatusService),
    client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHostedService<DiscoveryService>();
builder.Services.AddHostedService<DeviceStatusService>();

var app = builder.Build();

// Device liveness and stream counters are worthless if a browser serves a
// cached copy, and a stale reading is indistinguishable from a dead device.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
    }
    await next();
});

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

app.MapPost("/api/devices/{id}/roles",
    async (string id, RolesRequest request, DeviceRegistry registry,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }

    var roles = request.Roles ?? [];
    if (roles.Count == 0)
    {
        return Results.BadRequest(new { error = "at least one role is required" });
    }

    // Reject names the device would reject anyway, so the operator gets a
    // clear message here instead of a 400 relayed from the node.
    var unknown = roles.Where(r => !DeviceRole.IsKnown(r)).ToList();
    if (unknown.Count > 0)
    {
        return Results.BadRequest(new { error = $"unknown role(s): {string.Join(", ", unknown)}" });
    }

    var ok = await commands.SetRolesAsync(device, roles, cancellationToken);
    return ok
        ? Results.Ok(new { status = "stored", roles, appliesAt = "reboot" })
        : Results.StatusCode(502);
});

app.MapPost("/api/devices/{id}/channel",
    async (string id, ChannelRequest request, DeviceRegistry registry,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }

    // Rejected here as well as on the node, so the operator gets the list
    // of valid values rather than a bare 400 relayed from the firmware.
    if (!AudioChannels.IsKnown(request.Channel))
    {
        return Results.BadRequest(new
        {
            error = $"unknown channel; expected one of {string.Join(", ", AudioChannels.All)}",
        });
    }

    var ok = await commands.SetChannelAsync(device, request.Channel!, cancellationToken);
    return ok
        ? Results.Ok(new { status = "stored", channel = request.Channel, appliesAt = "reboot" })
        : Results.StatusCode(502);
});

// --- Node-to-node streaming (Phase 3, synthetic source) ---------------
// The Hub coordinates but does not relay: it tells a producer which
// consumers to send to, and the audio goes directly between them.

app.MapPost("/api/devices/{id}/stream/start",
    async (string id, NodeStreamRequest request, DeviceRegistry registry,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var producer))
    {
        return Results.NotFound();
    }
    if (!producer.Roles.Contains(DeviceRole.Producer))
    {
        return Results.BadRequest(new { error = $"{producer.Name} does not hold the producer role" });
    }

    // Destinations may be device ids or literal addresses, so a node can
    // stream to a PC running a player as easily as to another node.
    var destinations = new List<string>();
    foreach (var entry in request.Destinations ?? [])
    {
        if (registry.TryGet(entry, out var consumer))
        {
            destinations.Add(consumer.Address);
        }
        else if (System.Net.IPAddress.TryParse(entry, out var address))
        {
            destinations.Add(address.ToString());
        }
        else
        {
            return Results.BadRequest(new { error = $"unknown destination '{entry}'" });
        }
    }

    if (destinations.Count == 0)
    {
        return Results.BadRequest(new { error = "at least one destination is required" });
    }

    var ok = await commands.StartStreamAsync(
        producer, destinations, request.Port ?? ProtocolSuite.RtpPort,
        request.Source ?? "pattern", request.ToneHz ?? 1000, cancellationToken);
    return ok
        ? Results.Ok(new { status = "streaming", destinations })
        : Results.StatusCode(502);
});

app.MapPost("/api/devices/{id}/stream/stop",
    async (string id, DeviceRegistry registry, DeviceCommandClient commands,
           CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }
    var ok = await commands.StopStreamAsync(device, cancellationToken);
    return ok ? Results.Ok(new { status = "stopped" }) : Results.StatusCode(502);
});

app.MapGet("/api/devices/{id}/stream",
    async (string id, DeviceRegistry registry, DeviceCommandClient commands,
           CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }
    using var stream = await commands.GetStreamAsync(device, cancellationToken);
    return stream is null
        ? Results.StatusCode(502)
        : Results.Text(stream.RootElement.GetRawText(), "application/json");
});

// --- Joining (docs/DECISIONS.md decision 9) ---------------------------
// A Consumer that has finished booting asks the Controller what to do. The
// Hub is a Controller that knows about rooms, so it usually answers "stand
// by" — the difference between a Hub and a turntable is entirely in the
// answer, never in the question.

app.MapPost("/join",
    async (JoinRequest request, CastPointStore castPoints,
           DeviceRegistry registry, DeviceCommandClient commands,
           ILoggerFactory loggers, CancellationToken cancellationToken) =>
{
    var logger = loggers.CreateLogger("Join");
    var id = request.Id ?? "";
    if (!registry.TryGet(id, out var device))
    {
        // A node the Hub has not heard announce. It will announce shortly
        // and ask again, so this is a moment rather than a failure.
        logger.LogDebug("Join from unknown device {Id}", id);
        return Results.Ok(new { status = "standby", reason = "not yet discovered" });
    }

    var playing = castPoints.Playing;
    if (playing is null
        || !castPoints.TryGet(playing.CastPointId, out var point)
        || !point.Destinations.Contains(device.Id))
    {
        return Results.Ok(new { status = "standby" });
    }

    // The speaker belongs to what is playing, so put it back in the stream.
    // This is what heals a speaker that rebooted mid-party: it asks, and the
    // producer is told about it again. Adding is idempotent, so a node that
    // never left costs nothing by asking.
    if (registry.TryGet(playing.ProducerId, out var producer))
    {
        await commands.AddDestinationAsync(producer, device.Address, cancellationToken);
        logger.LogInformation(
            "{Device} rejoined {CastPoint}", device.Name, point.Name);
    }
    return Results.Ok(new { status = "playing", castPoint = point.Name });
});

// --- Cast points (docs/CAST-POINTS.md) --------------------------------
// A named place to send audio. A zone and a group are the same object:
// "Kitchen" has one consumer, "House" has twelve, and the Producer
// replicates one packet to however many it is given either way.

app.MapGet("/api/castpoints", (CastPointStore store, DeviceRegistry registry) =>
{
    var playing = store.Playing;
    // Decorated with the devices' current names and liveness, so a room
    // whose speaker is unplugged says so instead of looking ready.
    return Results.Ok(store.Snapshot().Select(point => new
    {
        point.Id,
        point.Name,
        point.Destinations,
        playing = playing?.CastPointId == point.Id,
        members = point.Destinations.Select(id => registry.TryGet(id, out var device)
            ? new { id, name = device.Name, online = device.Online, known = true }
            : new { id, name = id, online = false, known = false }),
    }));
});

app.MapPost("/api/castpoints", (CastPointRequest request, CastPointStore store) =>
{
    var error = store.Create(request.Name, request.Destinations, out var created);
    return error switch
    {
        CastPointError.None => Results.Ok(created),
        CastPointError.NameRequired => Results.BadRequest(new { error = "a name is required" }),
        CastPointError.NameUnusable => Results.BadRequest(
            new { error = "that name has no letters or digits to build an id from" }),
        _ => Results.BadRequest(new { error = error.ToString() }),
    };
});

app.MapPut("/api/castpoints/{id}", (string id, CastPointRequest request, CastPointStore store) =>
{
    var error = store.Update(id, request.Name, request.Destinations);
    if (error == CastPointError.NotFound)
    {
        return Results.NotFound();
    }
    if (error == CastPointError.NameRequired)
    {
        return Results.BadRequest(new { error = "a name is required" });
    }
    if (error != CastPointError.None)
    {
        return Results.BadRequest(new { error = error.ToString() });
    }
    return store.TryGet(id, out var updated) ? Results.Ok(updated) : Results.NotFound();
});

app.MapDelete("/api/castpoints/{id}", (string id, CastPointStore store) =>
    store.Delete(id) ? Results.Ok(new { status = "deleted" }) : Results.NotFound());

app.MapPost("/api/castpoints/{id}/play",
    async (string id, CastPointPlayRequest request, CastPointStore store, DeviceRegistry registry,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!store.TryGet(id, out var point))
    {
        return Results.NotFound();
    }
    if (point.Destinations.Count == 0)
    {
        return Results.BadRequest(new { error = $"{point.Name} has no speakers in it yet" });
    }
    if (!registry.TryGet(request.Producer ?? "", out var producer))
    {
        return Results.BadRequest(new { error = "unknown producer" });
    }
    if (!producer.Roles.Contains(DeviceRole.Producer))
    {
        return Results.BadRequest(new { error = $"{producer.Name} does not hold the producer role" });
    }

    var addresses = new List<string>();
    foreach (var deviceId in point.Destinations)
    {
        if (!registry.TryGet(deviceId, out var consumer))
        {
            return Results.BadRequest(new { error = $"{point.Name} refers to a device the Hub has never seen ({deviceId})" });
        }
        if (!consumer.Online)
        {
            return Results.BadRequest(new { error = $"{consumer.Name} is offline" });
        }
        addresses.Add(consumer.Address);
    }

    // One speaker cannot play two streams, so a cast point that overlaps
    // the playing one replaces it. Stopping first keeps the overlap from
    // existing even briefly.
    var conflict = store.ConflictWith(id);
    string? stopped = null;
    if (conflict is not null)
    {
        if (registry.TryGet(conflict.ProducerId, out var busy))
        {
            await commands.StopStreamAsync(busy, cancellationToken);
        }
        store.MarkStopped(conflict.CastPointId);
        stopped = conflict.CastPointId == id ? null : conflict.CastPointId;
    }

    var ok = await commands.StartStreamAsync(
        producer, addresses, request.Port ?? ProtocolSuite.RtpPort,
        request.Source ?? "pattern", request.ToneHz ?? 1000, cancellationToken);
    if (!ok)
    {
        return Results.StatusCode(502);
    }

    store.MarkPlaying(id, producer.Id);
    return Results.Ok(new { status = "playing", castPoint = point.Name, destinations = addresses, stopped });
});

app.MapPost("/api/castpoints/{id}/stop",
    async (string id, CastPointStore store, DeviceRegistry registry,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!store.TryGet(id, out _))
    {
        return Results.NotFound();
    }

    var playing = store.Playing;
    if (playing is not null && playing.CastPointId == id
        && registry.TryGet(playing.ProducerId, out var producer))
    {
        await commands.StopStreamAsync(producer, cancellationToken);
    }
    store.MarkStopped(id);
    return Results.Ok(new { status = "stopped" });
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

internal sealed record RolesRequest(IReadOnlyList<string>? Roles);

internal sealed record NodeStreamRequest(
    IReadOnlyList<string>? Destinations, int? Port, string? Source, int? ToneHz);

internal sealed record JoinRequest(string? Id, int? Port);

internal sealed record ChannelRequest(string? Channel);

internal sealed record CastPointRequest(string? Name, IReadOnlyList<string>? Destinations);

/// <summary>
/// The producer is named per play rather than stored on the cast point: a
/// cast point is a place, and which source feeds it is a property of the
/// moment. Once receivers drive playback (docs/CAST-POINTS.md) this is what
/// the receiver adapter supplies.
/// </summary>
internal sealed record CastPointPlayRequest(
    string? Producer, int? Port, string? Source, int? ToneHz);

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
