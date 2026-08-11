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

var dataDirectory = DataDirectory.Resolve(
    builder.Configuration["Hub:DataDirectory"], out var dataDirectoryNote);
var configStore = new HubConfigStore(dataDirectory);

var librespot = new LibrespotOptions();
builder.Configuration.GetSection(LibrespotOptions.SectionName).Bind(librespot);

builder.Services.AddSingleton(configStore);
builder.Services.AddSingleton(configStore.LoadOrCreate());
builder.Services.AddSingleton(new HubPaths(dataDirectory));
builder.Services.AddSingleton(librespot);
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton(new FirmwareStore(dataDirectory));
builder.Services.AddSingleton(new CastPointStore(dataDirectory));
builder.Services.AddSingleton(new StationStore(dataDirectory));
builder.Services.AddSingleton<RtpStreamer>();
builder.Services.AddHttpClient<DeviceCommandClient>();
// A short timeout on purpose: a node that does not answer promptly is a
// node whose reading would be stale anyway, and the poll comes round again.
builder.Services.AddHttpClient(nameof(DeviceStatusService),
        client => client.Timeout = TimeSpan.FromSeconds(3))
    // Do not hoard connections to a device with seven sockets in total.
    // .NET keeps pooled connections alive for minutes, which is right for a
    // web service and wrong for an ESP32: an idle connection there is one
    // of very few slots, and a node that runs out stops accepting anything
    // at all — no status, no portal, no OTA. Seen on hardware as lwIP
    // refusing every accept with errno 23.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
        MaxConnectionsPerServer = 2,
    });
// A station is an endless response, so the default hundred-second timeout
// would end one mid-song. Infinite here is safe because the read is
// cancelled when the source is disposed. A User-Agent because some
// stations refuse requests without one, which reads as a dead station.
builder.Services.AddHttpClient(nameof(RadioSource), client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenAudioLink/1.0");
});
// GitHub asks for a User-Agent and refuses without one, which reads as a
// network fault rather than as a missing header.
builder.Services.AddHttpClient(nameof(FirmwareFetcher), client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenAudioLink-Hub");
});
builder.Services.AddSingleton<FirmwareFetcher>();
builder.Services.AddSingleton<HubUpdater>();
builder.Services.AddHostedService<DiscoveryService>();
builder.Services.AddHostedService<DeviceStatusService>();
// Puts a node-to-node stream back after a roam takes it away. Only node
// producers: what the Hub sends itself is already supervised by whatever
// is driving it.
builder.Services.AddHostedService<StreamSupervisor>();
// Registered twice on purpose: the host runs it, and the API reads what it
// knows. Two registrations of the type would be two instances.
builder.Services.AddSingleton<LibrespotService>();
builder.Services.AddHostedService(services => services.GetRequiredService<LibrespotService>());

var app = builder.Build();

// After the host, because this is decided before logging exists.
app.Logger.LogInformation("Data directory: {Directory}", dataDirectory);
if (dataDirectoryNote is not null)
{
    app.Logger.LogWarning("{Note}", dataDirectoryNote);
}

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

// The switchboard's short address, so a printed QR code or an NFC sticker
// carries "/play#room=kitchen" rather than "/play.html#room=kitchen"
// (docs/CONTROL-SURFACE.md). A redirect rather than a route that renders,
// because the page must stay a static file that something other than this
// Hub can serve; browsers carry the fragment across the redirect themselves.
app.MapGet("/play", () => Results.Redirect("/play.html"));

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

/*
 * What updating would do, and whether it can be done from here.
 *
 * Separate from starting it, because the answer to "is there anything
 * newer" is worth having on screen without arming a button that stops the
 * service.
 */
app.MapGet("/api/hub/update", async (HubUpdater updater, CancellationToken cancellationToken) =>
{
    var check = await updater.CheckAsync(cancellationToken);
    return Results.Ok(new
    {
        check.Installed, check.Available, check.Asset,
        check.CanUpdate, check.Reason, newer = check.Newer,
    });
});

/*
 * Starts the update. There is no success response worth waiting for: the
 * script's first act is to stop this service, so the honest answer is
 * "accepted" and the real one is the Hub coming back on a new version.
 * The page polls /api/health for that.
 */
app.MapPost("/api/hub/update", async (
    HubUpdateRequest? request, HubUpdater updater, CastPointStore castPoints,
    CancellationToken cancellationToken) =>
{
    var check = await updater.CheckAsync(cancellationToken);
    if (!check.CanUpdate)
    {
        return Results.BadRequest(new { error = check.Reason ?? "this Hub cannot update itself" });
    }
    if (!check.Newer && request?.Force != true)
    {
        return Results.BadRequest(new
        {
            error = $"already on {check.Installed}"
                + (check.Available is null ? "" : $"; {check.Available} is what is published"),
        });
    }

    // Updating stops the service, which stops the music. Worth refusing by
    // default rather than explaining afterwards why a record cut out.
    if (castPoints.Playing is not null && request?.Force != true)
    {
        return Results.BadRequest(new
        {
            error = "something is playing, and updating stops it. Stop it first, or force the update.",
        });
    }

    return updater.Start(out var error)
        ? Results.Ok(new { status = "updating", from = check.Installed, to = check.Available })
        : Results.StatusCode(502);
});

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

/*
 * Fetches the published node firmware into this Hub's store.
 *
 * Deliberately not automatic, and deliberately not a flash. Downloading is
 * a convenience; flashing every speaker in a house without being asked is
 * a way to lose an evening. This puts the image where the Hub can serve it
 * and leaves pressing Update a human act.
 */
app.MapPost("/api/firmware/fetch",
    async (FirmwareFetcher fetcher, CancellationToken cancellationToken) =>
{
    var result = await fetcher.FetchLatestAsync(cancellationToken);
    if (result.Message is not null)
    {
        return Results.BadRequest(new { error = result.Message });
    }
    return Results.Ok(new
    {
        status = result.AlreadyHad ? "already had it" : "fetched",
        file = result.File,
        version = result.Version,
    });
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
    /*
     * Quiet first, then start.
     *
     * The node has to download an image, write it to flash and reboot,
     * and it serves the Hub's polling from the same small control server
     * the whole time. Ordering matters: hushing after the POST leaves a
     * window where a poll lands on a node that has already begun.
     *
     * Ninety seconds covers a ~1 MB download over Wi-Fi with room to
     * spare, and expires on its own — a failed update must not leave a
     * device permanently unwatched.
     */
    registry.Hush(device.Id, TimeSpan.FromSeconds(90));

    var ok = await commands.StartOtaAsync(device, request.File, cancellationToken);
    if (!ok)
    {
        // It never started, so there is nothing to be quiet for.
        registry.Hush(device.Id, TimeSpan.Zero);
        return Results.StatusCode(502);
    }
    return Results.Ok(new { status = "accepted" });
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
           HubConfig hubConfig, RtpStreamer streamer,
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
    //
    // The producer is either a node or this Hub — a cast point fed by
    // Spotify is sent by the Hub itself — and the two are told in different
    // ways, which is the only place that distinction shows.
    if (playing.ProducerId == hubConfig.Id)
    {
        if (IPAddress.TryParse(device.Address, out var address))
        {
            streamer.AddDestination(address);
            logger.LogInformation("{Device} rejoined {CastPoint}", device.Name, point.Name);
        }
    }
    else if (registry.TryGet(playing.ProducerId, out var producer))
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

/// <summary>
/// One number for a room's volume, or null when no speaker in it has said.
/// </summary>
/// <remarks>
/// A room has one volume as far as anybody standing in it is concerned,
/// but its speakers each hold their own — they can be set individually,
/// and one that was offline when the room was turned down still holds the
/// old level. The loudest is the honest answer: a room with one speaker at
/// 40 and one at 100 is a room playing at 100, and showing 40 would put
/// the slider below what is audible.
///
/// Null rather than 100 when nothing answered, because "no speaker has
/// reported a level" and "every speaker is at full" are different, and
/// only one of them is worth drawing a slider for.
/// </remarks>
static int? RoomVolume(IReadOnlyList<string> destinations, DeviceRegistry registry)
{
    int? loudest = null;
    foreach (var id in destinations)
    {
        if (registry.TryGet(id, out var device) && device.Status?.Volume is int level)
        {
            loudest = loudest is null ? level : Math.Max(loudest.Value, level);
        }
    }
    return loudest;
}

app.MapGet("/api/castpoints", (CastPointStore store, DeviceRegistry registry,
                               LibrespotService spotify, RtpStreamer streamer, HubConfig hubConfig) =>
{
    var playing = store.Playing;

    /*
     * A stream the Hub produces is only playing if the Hub is sending it.
     *
     * The store records what was *asked for*, and it stays recorded until
     * somebody stops it — which is right for a node producer, since the Hub
     * cannot see a node's sender directly and StreamSupervisor is what
     * reconciles that. But when the Hub itself is the producer it has the
     * answer in hand, and a radio station that ended, failed to reconnect,
     * or was stopped some other way left the record saying "playing" with
     * nothing on the wire.
     *
     * The switchboard believed it, and offered Stop for something that was
     * not making a sound.
     */
    if (playing is not null && playing.ProducerId == hubConfig.Id && !streamer.Status.Running)
    {
        playing = null;
    }
    var receivers = spotify.Snapshot().ToDictionary(r => r.CastPointId);
    // Decorated with the devices' current names and liveness, so a room
    // whose speaker is unplugged says so instead of looking ready.
    return Results.Ok(store.Snapshot().Select(point => new
    {
        point.Id,
        point.Name,
        point.Destinations,
        playing = playing?.CastPointId == point.Id,
        /*
         * What kind of thing is playing, and who is producing it.
         *
         * The source apps need this to answer "is my thing the thing that
         * is playing here": the Vinyl app must not offer Stop for a room
         * playing radio, and the portal has to light the right tile. A
         * boolean says a room is busy; it does not say what with, and
         * guessing produced a Stop button that stopped the wrong source.
         */
        source = playing?.CastPointId == point.Id ? playing.Source : null,
        producer = playing?.CastPointId == point.Id ? playing.ProducerId : null,
        // Whether the phone can see this room, and whether it is the one
        // making sound. Without it a cast point that is not advertised looks
        // identical to one that is.
        receiver = receivers.TryGetValue(point.Id, out var receiver)
            ? new { offered = receiver.Running, receiver.Playing, receiver.Error }
            : null,
        volume = RoomVolume(point.Destinations, registry),
        members = point.Destinations.Select(id => registry.TryGet(id, out var device)
            ? new
            {
                id, name = device.Name, online = device.Online, known = true,
                volume = device.Status?.Volume,
            }
            : new { id, name = id, online = false, known = false, volume = (int?)null }),
    }));
});

// What each cast point's Spotify Connect receiver is doing. Separate from
// the cast point list because it is diagnostics: a receiver that will not
// start says why here.
app.MapGet("/api/librespot", (LibrespotService spotify) => Results.Ok(spotify.Snapshot()));

app.MapPost("/api/castpoints", (CastPointRequest request, CastPointStore store) =>
{
    var error = store.Create(request.Name, request.Destinations, out var created);
    return error switch
    {
        CastPointError.None => Results.Ok(created),
        CastPointError.NameRequired => Results.BadRequest(new { error = "a name is required" }),
        CastPointError.NameUnusable => Results.BadRequest(
            new { error = "that name has no letters or digits to build an id from" }),
        CastPointError.NameTaken => Results.Conflict(
            new { error = "a cast point with that name already exists" }),
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
    if (error == CastPointError.NameTaken)
    {
        return Results.Conflict(new { error = "a cast point with that name already exists" });
    }
    if (error != CastPointError.None)
    {
        return Results.BadRequest(new { error = error.ToString() });
    }
    return store.TryGet(id, out var updated) ? Results.Ok(updated) : Results.NotFound();
});

app.MapDelete("/api/castpoints/{id}", (string id, CastPointStore store) =>
    store.Delete(id) ? Results.Ok(new { status = "deleted" }) : Results.NotFound());

// --- Stations (docs/ROADMAP.md, internet radio) -----------------------
// Saved on the Hub rather than in a browser, because a control surface is
// a wall tag and a phone that has never been here before.

app.MapGet("/api/stations", (StationStore stations) => Results.Ok(stations.Snapshot()));

app.MapPost("/api/stations", (StationRequest request, StationStore stations) =>
{
    var error = stations.Create(request.Name, request.Url, out var created);
    return error == StationError.None ? Results.Ok(created) : StationFailure(error);
});

app.MapPut("/api/stations/{id}", (string id, StationRequest request, StationStore stations) =>
{
    var error = stations.Update(id, request.Name, request.Url);
    if (error == StationError.NotFound)
    {
        return Results.NotFound();
    }
    if (error != StationError.None)
    {
        return StationFailure(error);
    }
    return stations.TryGet(id, out var updated) ? Results.Ok(updated) : Results.NotFound();
});

app.MapDelete("/api/stations/{id}", (string id, StationStore stations) =>
    stations.Delete(id) ? Results.Ok(new { status = "deleted" }) : Results.NotFound());

static IResult StationFailure(StationError error) => error switch
{
    StationError.NameRequired => Results.BadRequest(new { error = "a name is required" }),
    StationError.NameUnusable => Results.BadRequest(
        new { error = "that name has no letters or digits to build an id from" }),
    StationError.UrlRequired => Results.BadRequest(new { error = "a station url is required" }),
    StationError.UrlUnusable => Results.BadRequest(
        new { error = "that is not an http or https address" }),
    StationError.NameTaken => Results.Conflict(
        new { error = "a station with that name already exists" }),
    _ => Results.BadRequest(new { error = error.ToString() }),
};

app.MapPost("/api/castpoints/{id}/play",
    async (string id, CastPointPlayRequest request, CastPointStore store, DeviceRegistry registry,
           HubConfig hubConfig, RtpStreamer streamer, StationStore stations,
           IHttpClientFactory clients, ILoggerFactory loggers,
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
        // The busy producer is either a node or this Hub. A Spotify-fed
        // cast point is sent by the Hub itself, and telling a node to stop
        // would be aimed at a device that is not sending anything.
        if (conflict.ProducerId == hubConfig.Id)
        {
            await streamer.StopAsync();
        }
        else if (registry.TryGet(conflict.ProducerId, out var busy))
        {
            await commands.StopStreamAsync(busy, cancellationToken);
        }
        store.MarkStopped(conflict.CastPointId);
        stopped = conflict.CastPointId == id ? null : conflict.CastPointId;
    }

    var port = request.Port ?? ProtocolSuite.RtpPort;

    /*
     * The Hub holds the producer role like any other device, but it is not
     * reachable at a node's control endpoint — telling it to start a stream
     * means starting one here. This is the same path LibrespotService takes
     * when Spotify drives a cast point; the difference is only who chose.
     *
     * Which is what makes this one endpoint answer the whole question the
     * switchboard asks: play *this* in *that room*, whichever machine turns
     * out to produce it.
     */
    if (producer.Id == hubConfig.Id)
    {
        var targets = new List<IPAddress>();
        foreach (var address in addresses)
        {
            if (!IPAddress.TryParse(address, out var target))
            {
                return Results.BadRequest(new { error = $"'{address}' is not a usable address" });
            }
            targets.Add(target);
        }

        var format = new AudioStreamFormat();
        IAudioSource source;
        string kind;

        switch ((request.Source ?? "").ToLowerInvariant())
        {
            case "radio":
                var url = request.Url;
                if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(request.StationId))
                {
                    if (!stations.TryGet(request.StationId, out var station))
                    {
                        return Results.BadRequest(new { error = "unknown station" });
                    }
                    url = station.Url;
                }
                if (string.IsNullOrWhiteSpace(url))
                {
                    return Results.BadRequest(new { error = "a station url or stationId is required" });
                }

                try
                {
                    // Connecting happens on the source's own thread, so an
                    // unreachable station is not an error here — it says so
                    // in the stream description the switchboard displays.
                    source = new RadioSource(
                        url, format, clients.CreateClient(nameof(RadioSource)),
                        loggers.CreateLogger<RadioSource>());
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = $"could not start {url}: {ex.Message}" });
                }
                kind = "radio";
                break;

            case "tone":
                source = new SineToneSource(format, request.ToneHz ?? 1000);
                kind = "test-tone";
                break;

            case "system-audio":
                if (!OperatingSystem.IsWindows())
                {
                    return Results.BadRequest(new { error = "system audio capture requires Windows" });
                }
                try
                {
                    source = new SystemAudioSource(format);
                }
                catch (NotSupportedException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                kind = "system-audio";
                break;

            default:
                // Spotify is missing from this list on purpose: a cast point
                // plays Spotify because somebody pressed play on a phone, and
                // the Hub cannot make that happen from here.
                return Results.BadRequest(new
                {
                    error = string.IsNullOrWhiteSpace(request.Source)
                        ? $"{producer.Name} needs a source: radio, tone or system-audio"
                        : $"{producer.Name} can produce radio, tone or system-audio, "
                            + $"not '{request.Source}'",
                });
        }

        try
        {
            await streamer.StartAsync(kind, source, targets, port, format);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        store.MarkPlaying(id, producer.Id);
        return Results.Ok(new
        {
            status = "playing",
            castPoint = point.Name,
            destinations = addresses,
            source = kind,
            stopped,
        });
    }

    var ok = await commands.StartStreamAsync(
        producer, addresses, port,
        request.Source ?? "pattern", request.ToneHz ?? 1000, cancellationToken);
    if (!ok)
    {
        return Results.StatusCode(502);
    }

    store.MarkPlaying(id, producer.Id, request.Source ?? "pattern", request.ToneHz ?? 1000);
    return Results.Ok(new { status = "playing", castPoint = point.Name, destinations = addresses, stopped });
});

/*
 * Volume for a room, which is the only place a person thinks about it.
 *
 * It reaches every speaker in the cast point, in parallel, and reports how
 * many answered. Partial success is a real outcome and is reported as one:
 * a room with three speakers where one is unplugged should get quieter by
 * two speakers rather than refusing because of the third.
 *
 * Nothing here is stored on the Hub. The level lives on each node, in its
 * own NVS, and survives the Hub being reinstalled — which is right, because
 * how loud a speaker should be is a property of where it stands.
 */
app.MapPost("/api/castpoints/{id}/volume",
    async (string id, VolumeRequest request, CastPointStore store, DeviceRegistry registry,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!store.TryGet(id, out var point))
    {
        return Results.NotFound();
    }
    if (request.Percent is not int percent || percent is < 0 or > 100)
    {
        return Results.BadRequest(new { error = "percent must be 0 to 100" });
    }

    var targets = new List<DeviceRecord>();
    foreach (var deviceId in point.Destinations)
    {
        if (registry.TryGet(deviceId, out var device) && device.Online)
        {
            targets.Add(device);
        }
    }

    if (targets.Count == 0)
    {
        return Results.BadRequest(new { error = $"no speaker in {point.Name} is online" });
    }

    var results = await Task.WhenAll(
        targets.Select(d => commands.SetVolumeAsync(d, percent, cancellationToken)));

    var reached = results.Count(ok => ok);
    return reached == 0
        ? Results.StatusCode(502)
        : Results.Ok(new
        {
            status = "set",
            volume = percent,
            castPoint = point.Name,
            speakers = reached,
            // Named rather than counted: "1 unreachable" makes somebody
            // count the room's speakers to work out which.
            unreachable = targets.Where((_, i) => !results[i]).Select(d => d.Name),
        });
});

// The same for one speaker, which is what balancing a stereo pair or a
// too-loud kitchen speaker needs. The setup page's control; the switchboard
// only ever moves a whole room.
app.MapPost("/api/devices/{id}/volume",
    async (string id, VolumeRequest request, DeviceRegistry registry,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }
    if (request.Percent is not int percent || percent is < 0 or > 100)
    {
        return Results.BadRequest(new { error = "percent must be 0 to 100" });
    }

    return await commands.SetVolumeAsync(device, percent, cancellationToken)
        ? Results.Ok(new { status = "set", volume = percent })
        : Results.StatusCode(502);
});

app.MapPost("/api/castpoints/{id}/stop",
    async (string id, CastPointStore store, DeviceRegistry registry, HubConfig hubConfig,
           RtpStreamer streamer, DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!store.TryGet(id, out _))
    {
        return Results.NotFound();
    }

    var playing = store.Playing;
    if (playing is not null && playing.CastPointId == id)
    {
        if (playing.ProducerId == hubConfig.Id)
        {
            // A Spotify-fed cast point resumes within a tick, because the
            // receiver is still playing and the receiver is what drives the
            // stream (docs/CAST-POINTS.md). Pausing belongs on the phone;
            // this stops the sending, not the music.
            await streamer.StopAsync();
        }
        else if (registry.TryGet(playing.ProducerId, out var producer))
        {
            await commands.StopStreamAsync(producer, cancellationToken);
        }
    }
    else if (playing is null && streamer.Status.Running)
    {
        /*
         * Stop means stop, including when the Hub has lost track.
         *
         * Nothing claims the sender and yet it is sending — an orphan. It
         * has happened: a service cleared its own bookkeeping while its
         * stream carried on, so the room was audible, the record said
         * nothing was playing, and Stop had no branch that applied. The
         * only cure was restarting the Hub.
         *
         * Somebody pressing Stop while sound is coming out is not making a
         * subtle request, so an orphan is stopped rather than reasoned
         * about. Only when nothing claims it: a stream another room owns is
         * that room's to stop.
         */
        app.Logger.LogWarning(
            "Stop on {CastPoint} found a stream nothing claims ({Source}); stopping it",
            id, streamer.Status.Source);
        await streamer.StopAsync();
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

app.MapPost("/api/stream/radio", async (
    StreamRequest request, HttpContext context, DeviceRegistry registry, RtpStreamer streamer,
    IHttpClientFactory clients, ILoggerFactory loggers) =>
{
    if (string.IsNullOrWhiteSpace(request.Url))
    {
        return Results.BadRequest(new { error = "a station url is required" });
    }

    var failure = ResolveDestinations(request, context, registry, out var destinations);
    if (destinations.Count == 0)
    {
        return failure;
    }

    try
    {
        var format = BuildFormat(request);
        format.Validate();

        // This returns as soon as the source's thread is started, so a
        // station that turns out to be unreachable is not a failure here.
        // It reports itself through the stream's description, which is what
        // GET /api/stream returns and what the switchboard shows.
        var radio = new RadioSource(
            request.Url, format, clients.CreateClient(nameof(RadioSource)),
            loggers.CreateLogger<RadioSource>());

        return Results.Ok(await streamer.StartAsync(
            "radio", radio, destinations, request.Port ?? 41100, format));
    }
    catch (NotSupportedException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"could not start {request.Url}: {ex.Message}" });
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
    string? Producer,
    int? Port,
    string? Source,
    int? ToneHz,
    // Both only apply when the producer is this Hub and the source is
    // radio. A station id is the switchboard's way of saying it; a bare
    // url is for anything driving the API without saving one first.
    string? Url = null,
    string? StationId = null);

/// <summary>Force skips the "already up to date" and "something is playing" refusals.</summary>
internal sealed record HubUpdateRequest(bool? Force = null);

internal sealed record StationRequest(string? Name, string? Url);

/// <summary>
/// Nullable rather than a plain int so a body with no percent at all is a
/// clear 400 instead of silently muting the room, which is what binding a
/// missing value to the default 0 would do.
/// </summary>
internal sealed record VolumeRequest(int? Percent);

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
    int? PacketMilliseconds,
    // The station, for /api/stream/radio. Ignored by every other endpoint.
    string? Url = null);
