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
builder.Services.AddSingleton(new HubNetworkSetting(builder.Configuration["Hub:Network"]));
builder.Services.AddSingleton(librespot);
builder.Services.AddSingleton<DeviceRegistry>();
// Resolved rather than constructed inline so the store can log what it
// prunes; it deletes all but the newest few images at startup.
builder.Services.AddSingleton(sp => new FirmwareStore(
    dataDirectory, sp.GetRequiredService<ILoggerFactory>().CreateLogger<FirmwareStore>()));
builder.Services.AddSingleton(new CastPointStore(dataDirectory));
builder.Services.AddSingleton(new NodeAudioStore(dataDirectory));
builder.Services.AddSingleton(new StationStore(dataDirectory));
builder.Services.AddSingleton<RtpStreamer>();
/*
 * The same socket discipline as the status client below, and it was missing
 * here — on the client that talks to nodes most.
 *
 * DeviceCommandClient carries every command and the /stream reads the
 * switchboard makes for each speaker every few seconds. Registered bare it
 * took .NET's defaults: an unbounded pool per server, held open for a
 * minute. That is correct for a web service and wrong for an ESP32 with
 * seven sockets in total, which stops accepting anything at all when they
 * run out — no status, no portal, no OTA, and lwIP refusing every accept
 * with errno 23.
 *
 * Seen as both nodes reporting "no answer (HTTP 502)" while the audio
 * itself kept playing, because UDP needs no socket from that pool.
 */
builder.Services.AddHttpClient<DeviceCommandClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
        MaxConnectionsPerServer = 2,
    });
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
// Singleton as well as hosted: the endpoint reads the fits the loop
// builds, and two instances would each measure half as often.
builder.Services.AddSingleton<NodeClockService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NodeClockService>());
// Writes the readings the clock service already took to a CSV per day, so
// a run can be read as a series instead of photographed one instant at a
// time. Asks no node for anything of its own.
builder.Services.AddSingleton<SampleLogService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SampleLogService>());
// Puts a node-to-node stream back after a roam takes it away. Only node
// producers: what the Hub sends itself is already supervised by whatever
// is driving it.
builder.Services.AddHostedService<LatencyProfileReconciler>();
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

/*
 * The page itself must revalidate, or an upgraded Hub serves an old
 * switchboard.
 *
 * The no-store rule above covers /api, so the numbers were always fresh --
 * and the page drawing them was not. A Hub updated to a version with a new
 * column reported the new version in its header, from /api/health, while
 * rendering the previous page from the browser's cache: the operator sees
 * "0.50.0 — up to date" above a table that predates it, and the only clue
 * is a column that should not be there.
 *
 * no-cache rather than no-store: the browser may keep the copy, it just
 * has to ask first, and the static file middleware answers 304 from the
 * ETag when nothing changed. Only documents, because the pages here carry
 * their CSS and script inline and there is nothing else to revalidate.
 */
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
        }
    },
});

// The switchboard's short address, so a printed QR code or an NFC sticker
// carries "/play#room=kitchen" rather than "/play.html#room=kitchen"
// (docs/CONTROL-SURFACE.md). A redirect rather than a route that renders,
// because the page must stay a static file that something other than this
// Hub can serve; browsers carry the fragment across the redirect themselves.
app.MapGet("/play", () => Results.Redirect("/play.html"));

/*
 * The sample log, downloadable rather than only on disk.
 *
 * A file somebody has to find in a data directory is a file nobody sends,
 * and the whole point of writing it is that the numbers travel to whoever
 * is reading them. Listed at /api/samples and fetched from /samples/<name>.
 */
var sampleLogDirectory = app.Services.GetRequiredService<SampleLogService>().DirectoryPath;
// Only if it is really there. PhysicalFileProvider throws on a missing
// root, and that throw happens here, while the pipeline is being built --
// so an absent directory does not disable a download, it stops the Hub
// starting at all. Diagnostics must not be able to do that.
if (Directory.Exists(sampleLogDirectory))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
            sampleLogDirectory),
        RequestPath = "/samples",
        ServeUnknownFileTypes = true,
        DefaultContentType = "text/csv",
    });
}

app.MapGet("/api/samples", (SampleLogService log) =>
{
    var dir = new DirectoryInfo(log.DirectoryPath);
    if (!dir.Exists)
    {
        return Results.Ok(Array.Empty<object>());
    }
    return Results.Ok(dir.EnumerateFiles("oal-*.csv")
        .OrderByDescending(f => f.Name)
        .Select(f => new
        {
            file = f.Name,
            size = f.Length,
            modifiedAt = f.LastWriteTimeUtc,
            url = $"/samples/{f.Name}",
        })
        .ToList());
});

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

// Every node's crystal against this Hub's, measured by the Hub so it
// survives the page being closed. See NodeClockService for why that
// matters more than it sounds.
app.MapGet("/api/clocks", (NodeClockService clocks) => Results.Ok(clocks.Snapshot()));

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

/*
 * Rejoin, beside reboot, because reboot is the wrong tool for this and was
 * the only one available. See DeviceCommandClient.RejoinWifiAsync.
 *
 * Briefly hushed for the same reason an update is: the node's radio stops
 * serving the connection while it scans, so a poll landing in that window
 * reads as an offline device and paints the row red for no reason.
 */
app.MapPost("/api/devices/{id}/wifi/rejoin",
    async (string id, DeviceRegistry registry, DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }
    registry.Hush(device.Id, TimeSpan.FromSeconds(15));
    var ok = await commands.RejoinWifiAsync(device, cancellationToken);
    if (!ok)
    {
        registry.Hush(device.Id, TimeSpan.Zero);
        return Results.StatusCode(502);
    }
    return Results.Ok(new { status = "rejoining" });
});

/*
 * What a node can hear, on demand.
 *
 * The companion to rejoin: that endpoint reports whether the node accepted
 * the instruction, which is not the same as whether it did anything useful.
 * A node stuck on a distant access point through repeated rejoins is either
 * passing over a better one or cannot hear it, and only a scan separates
 * those.
 *
 * Hushed like rejoin, and for longer, because the node leaves its channel
 * to sweep the others: a status poll landing mid-scan reads as an offline
 * device and paints the row red for no reason.
 *
 * Passed through as the node's own JSON. Nothing here is better placed to
 * interpret a radio's view of a room than the radio.
 */
app.MapGet("/api/devices/{id}/wifi/scan",
    async (string id, DeviceRegistry registry, DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }
    registry.Hush(device.Id, TimeSpan.FromSeconds(20));
    var json = await commands.ScanWifiAsync(device, cancellationToken);
    if (json is null)
    {
        registry.Hush(device.Id, TimeSpan.Zero);
        return Results.StatusCode(502);
    }
    return Results.Content(json, "application/json");
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

/*
 * Extra playout delay, per node.
 *
 * Exists because two speakers playing one stream through different output
 * stages do not come out together: the USB path carries the host driver's
 * ring, 1 ms USB frames and the dongle's own buffering on top of the
 * playout, where I²S carries four DMA descriptors. Tens of milliseconds,
 * and obvious the moment both play in one room.
 *
 * Bounds checked here as well as on the node so the operator gets the
 * range rather than a bare 400 relayed from firmware — and because the
 * useful half of the message is *why* it is one-sided: nothing can play a
 * sample before it arrives, so alignment is always the early node waiting.
 */
app.MapPost("/api/devices/{id}/delay",
    async (string id, DelayRequest request, DeviceRegistry registry,
           DeviceCommandClient commands, NodeAudioStore audio,
           CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }

    /*
     * The ceiling comes from the node, not from here.
     *
     * It was the constant 50, which was right for a 200 ms ring and is now
     * right for nothing in particular: with the ring settable, two nodes on
     * one shelf can have ceilings of 50 and 650. Hardcoding either is the
     * same mistake that had this dialog offering 0-200 against a real limit
     * of 50 for two releases.
     *
     * Falling back to 50 when a node has not reported one keeps older
     * firmware working rather than locking it out, and 50 is exactly what
     * that firmware's fixed ring allowed.
     */
    var ceiling = device.Status?.MaxDelayMs ?? 50;
    var delay = request.DelayMs ?? -1;
    if (delay < 0 || delay > ceiling)
    {
        return Results.BadRequest(new
        {
            error = $"delayMs must be 0 to {ceiling} on this node; delay is only ever "
                  + "added, to whichever node plays early",
        });
    }

    /*
     * Setting this by hand is taking manual control, so the node stops
     * being held to a profile. Without this the reconciler would put the
     * profile's delay back within twenty seconds, and from outside that is
     * indistinguishable from the Hub ignoring the request.
     */
    var ok = await commands.SetDelayAsync(device, delay, cancellationToken);
    if (ok)
    {
        audio.ClearProfile(id);
    }
    return ok
        ? Results.Ok(new { status = "stored", delayMs = delay })
        : Results.StatusCode(502);
});

/*
 * The ring, per node: how much audio the buffer can hold at all.
 *
 * A different question from the delay above, and the difference is worth
 * keeping straight. Delay moves the target *within* the ring and takes
 * effect while the music plays; this changes how big the ring is, applies at
 * the next boot, and is the only one of the two that can buy headroom that
 * does not exist yet.
 *
 * Settable because the right answer is not known. This project runs a 100 ms
 * target in a 200 ms ring; Snapcast runs about 1000 ms, AirPlay about 2000.
 * The network it runs on has been measured leaving 900 ms holes, which no
 * 200 ms ring can absorb no matter where the target sits. Finding the size
 * that actually sounds best is an experiment, and the margin buckets already
 * report the result of each attempt.
 */
app.MapPost("/api/devices/{id}/ring",
    async (string id, RingRequest request, DeviceRegistry registry,
           DeviceCommandClient commands, NodeAudioStore audio,
           CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }

    // The node clamps too, and refuses out of range. Checked here as well so
    // the operator gets the range instead of a bare 400 relayed from
    // firmware.
    var ring = request.RingMs ?? -1;
    if (ring < 50 || ring > 1000)
    {
        return Results.BadRequest(new
        {
            error = "ringMs must be 50 to 1000; this is the buffer's capacity, "
                  + "not its target, and it applies at the node's next reboot",
        });
    }

    // As with the delay above: by hand means by hand.
    var ok = await commands.SetRingAsync(device, ring, cancellationToken);
    if (ok)
    {
        audio.ClearProfile(id);
    }
    return ok
        ? Results.Ok(new { status = "stored", ringMs = ring, appliesAt = "reboot" })
        : Results.StatusCode(502);
});

/*
 * Capture gain, which only a microphone node needs.
 *
 * The chain has no other place to put it. A consumer's volume attenuates
 * and never amplifies -- deliberately, because the streams it normally
 * rides arrive mastered near full scale -- and an ICS-43434 gives
 * -26 dBFS at 94 dB SPL, so an ordinary room lands around -45. The first
 * microphone stream this project sent was perfectly audible and far too
 * quiet with every consumer at 100 %, and no setting that existed at the
 * time could have fixed it.
 */
app.MapPost("/api/devices/{id}/mic-gain",
    async (string id, MicGainRequest request, DeviceRegistry registry,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }

    /*
     * Sixty, matching OAL_BOOST_DB_MAX. The ceiling is the capsule's noise
     * rather than the arithmetic: 65 dB SNR puts the ICS-43434's own hiss
     * near -91 dBFS, so 60 dB of boost lifts it to -31 and more would buy
     * noise rather than signal.
     */
    var db = request.MicGainDb ?? -1;
    if (db < 0 || db > 60)
    {
        return Results.BadRequest(new
        {
            error = "micGainDb must be 0 to 60; it is whole decibels of gain on what "
                  + "this node captures, and only a microphone needs any",
        });
    }

    var ok = await commands.SetMicGainAsync(device, db, cancellationToken);
    return ok
        ? Results.Ok(new { status = "stored", micGainDb = db, appliesAt = "reboot" })
        : Results.StatusCode(502);
});

/*
 * The two settings above, as a decision rather than as arithmetic.
 *
 * Ring and delay are the right primitives and a poor interface: they fail
 * differently, only one needs a reboot, and choosing a pair that works
 * together means knowing three firmware constants and which of two
 * ceilings binds. Run 40 ended by observing that the buffer was a decision
 * nobody had made -- 225 ms of cushion on a network measured leaving
 * 200 ms holes, against Snapcast's 1000 and AirPlay's 2000 -- so here it
 * is, made three times and named.
 */
app.MapGet("/api/profiles", (DeviceRegistry registry, NodeAudioStore store) =>
{
    var wanted = store.Snapshot();
    return Results.Ok(new
    {
        profiles = LatencyProfile.All.Select(p => new
        {
            p.Id, p.Name, p.Use, p.RingMs, p.TargetMs, p.DelayMs,
            p.PadBelowMs, p.TrimAboveMs, p.SteerToMs, p.SurvivesGapMs,
            ringKilobytes = p.RingBytes / 1024,
            airToEarMs = p.AirToEarMs(false),
            airToEarUsbMs = p.AirToEarMs(true),
        }),
        /*
         * What each node is actually running, matched on the settings it
         * reports rather than on anything stored here -- so a node set by
         * hand, or by an older Hub, or before profiles existed, still
         * answers honestly. A node with its ring changed but not yet
         * rebooted matches nothing, which is correct: it is running
         * neither the old profile nor the new one.
         */
        devices = registry.Snapshot()
            .Where(d => d.Status is not null)
            .Select(d =>
            {
                var intent = wanted.GetValueOrDefault(d.Id, new NodeAudio(null, 0));
                var target = d.Status!.DelayMs is { } ms
                    ? LatencyProfile.BaseTargetMs + ms - intent.AlignMs
                    : (int?)null;
                var running = LatencyProfile.Match(d.Status.RingMs, target);
                return new
                {
                    id = d.Id,
                    name = d.Name,
                    ringMs = d.Status.RingMs,
                    delayMs = d.Status.DelayMs,
                    targetMs = target,
                    alignMs = intent.AlignMs,
                    // What it is doing now, and what it was asked to do.
                    // They differ while a node is waiting for the reboot
                    // that gives it its new ring, and saying so is the
                    // difference between "not yet" and "did not work".
                    profile = running?.Id,
                    wanted = intent.Profile,
                    pending = intent.Profile is not null && running?.Id != intent.Profile,
                };
            }),
    });
});

/*
 * Applying a profile to one node: the ring, and the delay that the profile
 * and this node's alignment offset add up to.
 *
 * The offset is the reason this endpoint exists rather than the GUI
 * calling the two above in turn. `delayMs` does two jobs -- it sets the
 * depth of the buffer, and it holds an early node back so a USB dongle and
 * an I2S DAC line up -- so writing it from a profile alone silently
 * discards whatever alignment was there. That failure is the quiet kind:
 * each speaker fine on its own, the pair smeared, and no counter anywhere
 * that can say why, because alignment is not something a node can measure
 * about itself.
 */
app.MapPost("/api/devices/{id}/profile",
    async (string id, ProfileRequest request, DeviceRegistry registry,
           DeviceCommandClient commands, NodeAudioStore store,
           CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }

    var profile = LatencyProfile.ById(request.Profile);
    if (profile is null)
    {
        return Results.BadRequest(new
        {
            error = $"unknown profile '{request.Profile}'",
            profiles = LatencyProfile.All.Select(p => p.Id),
        });
    }

    var align = request.AlignMs ?? store.Get(id).AlignMs;
    if (align < 0 || align > NodeAudioStore.MaxAlignMs)
    {
        return Results.BadRequest(new
        {
            error = $"alignMs must be 0 to {NodeAudioStore.MaxAlignMs}; it is how far "
                  + "*early* this node plays, and delay is only ever added",
        });
    }

    /*
     * Checked against the profile's own ring, not the node's current one.
     *
     * The ring is being changed in this same call, so the ceiling that
     * matters is the one the node will have after it reboots -- and the
     * node cannot tell us that yet. A profile whose target does not fit
     * its ring is not refused by the firmware; it is quietly clamped, and
     * the operator ends up running a buffer nobody chose with nothing to
     * indicate it happened. `Fits` is that check, made here where it can
     * still be reported.
     */
    if (!profile.Fits)
    {
        return Results.StatusCode(500);
    }

    var delay = profile.DelayMs + align;
    if (delay > profile.RingMs * 3 / 4 - LatencyProfile.BaseTargetMs)
    {
        return Results.BadRequest(new
        {
            error = $"an alignment of {align} ms does not fit the {profile.Name} profile; "
                  + "raise the profile or reduce the offset",
        });
    }

    /*
     * Recorded, then converged on -- not pushed here.
     *
     * The two settings cannot both be applied now. The ring takes effect at
     * the next boot, and until the node is running it the node *refuses*
     * any delay the old ring cannot hold: Standard to Long asks for 450 ms
     * against a 400 ms ring's ceiling of 200, and gets a 400 back. So the
     * intent is written down and LatencyProfileReconciler walks the node
     * there across the reboot, which also covers a node that was offline
     * when the profile was picked.
     */
    store.SetProfile(id, profile.Id, align);

    var ringAlready = device.Status?.RingMs == profile.RingMs;
    if (ringAlready && !await commands.SetDelayAsync(device, delay, cancellationToken))
    {
        // Left stored deliberately: the reconciler will keep trying, and a
        // node that was briefly busy should not lose the setting.
        return Results.StatusCode(502);
    }
    if (!ringAlready && !await commands.SetRingAsync(device, profile.RingMs, cancellationToken))
    {
        return Results.StatusCode(502);
    }

    return Results.Ok(new
    {
        status = "stored",
        profile = profile.Id,
        ringMs = profile.RingMs,
        targetMs = profile.TargetMs,
        delayMs = delay,
        alignMs = align,
        restingMs = profile.SteerToMs,
        // The ring is an allocation, so the depth a profile promises is not
        // what is playing until the node has come back with it.
        appliesAt = ringAlready ? "now" : "reboot",
        note = ringAlready
            ? null
            : $"reboot {device.Name} to give it the {profile.RingMs} ms ring; "
            + "the delay follows on its own once it has.",
    });
});

/*
 * The node's name, and the last of the provisioning form's settings to
 * reach the admin GUI.
 *
 * Provisioning asks for network, name, roles, speaker and output, because
 * that is the one moment the board is in your hands and you know which one
 * it is. Everything but the network belongs here too — a node is named
 * wrong, or grows a dongle, long after it was set up, and the alternative
 * was re-provisioning it: clearing the Wi-Fi credentials and typing the
 * password again to fix a typo in a label.
 *
 * Immediate, unlike its neighbours. The node rebuilds its announce, so
 * every list on the network follows within a few seconds.
 */
app.MapPost("/api/devices/{id}/name",
    async (string id, NameRequest request, DeviceRegistry registry,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }

    // Trimmed, because a name with leading spaces sorts oddly in every list
    // that shows it and looks like nothing at all in most of them.
    var name = (request.Name ?? string.Empty).Trim();

    // 31 characters plus a terminator: the width of the announce field, so
    // a longer name is one no other device could display anyway. Checked
    // here as well as on the node so the operator gets the limit rather
    // than a bare 400 relayed from firmware.
    if (name.Length > 31)
    {
        return Results.BadRequest(new
        {
            error = "name must be 31 characters or fewer; it travels on the "
                  + "discovery announce, which is that wide",
        });
    }

    var ok = await commands.SetNameAsync(device, name, cancellationToken);
    return ok
        ? Results.Ok(new
        {
            status = "stored",
            name,
            appliesAt = "now",
            note = name.Length == 0 ? "cleared; the node falls back to its default name" : null,
        })
        : Results.StatusCode(502);
});

/*
 * How audio leaves the board. Provisioning asked; nothing else could,
 * until now.
 *
 * Worth having separately from the roles button beside it, because this is
 * the setting that makes a silent speaker: a node set to usb with no dongle
 * plugged in receives, buffers and plays nothing while every other counter
 * on the page looks healthy. /status reports outputReady for exactly that
 * case, and this is how the answer gets fixed once it is spotted.
 */
app.MapPost("/api/devices/{id}/output",
    async (string id, OutputRequest request, DeviceRegistry registry,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }

    var output = request.Output?.Trim().ToLowerInvariant();
    if (output is not ("i2s" or "usb"))
    {
        return Results.BadRequest(new
        {
            error = "output must be \"i2s\" or \"usb\"; it applies at the node's next reboot",
        });
    }

    var ok = await commands.SetOutputAsync(device, output, cancellationToken);
    return ok
        ? Results.Ok(new { status = "stored", output, appliesAt = "reboot" })
        : Results.StatusCode(502);
});

/*
 * What a Producer captures from: a line-level ADC, or a microphone.
 *
 * One box, two jobs, never at once — a measurement microphone at the
 * listening position, or the line input by the turntable. Both sets of pins
 * are wired at the same time (docs/HARDWARE.md); this says which set is
 * live, so a node does not have to be rebuilt to change hats.
 *
 * Reboot, not immediate, and that is not laziness. The choice picks GPIO
 * pins *and* which end of the I²S bus makes the clocks: the PCM1808 module
 * generates BCK and LRCK and the node follows, the ICS-43434 is a slave and
 * the node generates them. Two masters on one clock line produce silence,
 * so the role is settled once, at boot, before anything drives a pin.
 */
app.MapPost("/api/devices/{id}/input",
    async (string id, InputRequest request, DeviceRegistry registry,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }

    var input = request.Input?.Trim().ToLowerInvariant();
    if (input is not ("line" or "mic"))
    {
        return Results.BadRequest(new
        {
            error = "input must be \"line\" or \"mic\"; it applies at the node's "
                  + "next reboot, because it also decides which end of the I²S "
                  + "bus makes the clocks",
        });
    }

    var ok = await commands.SetInputAsync(device, input, cancellationToken);
    return ok
        ? Results.Ok(new { status = "stored", input, appliesAt = "reboot" })
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
    async (string id, HttpContext http, DeviceRegistry registry,
           DeviceCommandClient commands, CancellationToken cancellationToken) =>
{
    if (!registry.TryGet(id, out var device))
    {
        return Results.NotFound();
    }

    /*
     * How long the node itself took, reported separately from how long the
     * whole thing took.
     *
     * The speaker-sync panel places a node's playing position in time by
     * the round trip that carried it, and discards a reading whose trip was
     * slow enough to make the placement meaningless. Measured in the
     * browser that trip is browser to Hub to node and back again, and the
     * only leg with real jitter in it is the wireless one. Gating on the
     * total threw away perfectly good readings whenever the page itself was
     * a little slow, and then told the operator to update firmware that was
     * already current.
     *
     * A header rather than a field in the body, because the body is the
     * node's own document relayed verbatim and nothing here should be
     * putting the Hub's opinions inside it.
     */
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    using var stream = await commands.GetStreamAsync(device, cancellationToken);
    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
    http.Response.Headers["X-Oal-Node-Rtt-Ms"] =
        ((int)Math.Round(elapsed.TotalMilliseconds)).ToString();

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
    /*
     * Why it stopped, kept rather than discarded.
     *
     * Nulling `playing` is right and was not enough. A cast point asked to
     * play would show "playing" for one poll and then "idle", with the
     * reason sitting in StreamStatus.Error where nothing rendered it — so
     * the screen said a room was doing nothing, having just been told to do
     * something, and offered no way to tell a dead radio URL from an
     * unreachable speaker from a decoder that refused the format.
     *
     * Only for the cast point that was asked for. Attaching a stale
     * streamer error to every room would say all of them had failed.
     */
    string? stoppedReason = null;
    // Taken before `playing` is cleared below, not read from the store a
    // second time: two reads of a mutable property can disagree, and the
    // one that decides which row shows the error must be the same reading
    // that decided the row is not playing.
    var askedCastPointId = playing?.CastPointId;
    if (playing is not null && playing.ProducerId == hubConfig.Id && !streamer.Status.Running)
    {
        stoppedReason = streamer.Status.Error
            // Running went false with nothing recorded. Rare, and worth
            // saying out loud rather than showing a blank: it means the
            // stream ended without raising, which is a different fault from
            // one that threw.
            ?? "the stream stopped without saying why";
        playing = null;
    }
    var stoppedCastPointId = stoppedReason is null ? null : askedCastPointId;
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
        // Set only on the room that was asked to play and is not, so the
        // switchboard can say what went wrong instead of quietly reverting
        // to Play and leaving the operator to guess.
        stoppedReason = stoppedCastPointId == point.Id ? stoppedReason : null,
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

/*
 * Where a phone's volume slider arrives.
 *
 * librespot runs a script on every playback event and the script curls
 * here; the cast point is in the path because librespot does not know
 * about cast points, only about itself. Decision 14 wants one gain stage
 * at the Consumer, so this is the path that puts the phone's slider
 * there instead of in front of it.
 *
 * Loopback only. Nothing outside this machine has any business setting a
 * room's volume without going through the ordinary endpoint, and the
 * script has no way to authenticate itself.
 */
/*
 * The two volume switches, readable and settable while the Hub runs.
 *
 * They exist to be bisected, and a bisect needs somebody able to change
 * one thing at a time. appsettings.json is inside Program Files, which on
 * a locked-down machine the operator cannot edit at all — so a switch
 * that lives only there is a switch nobody can throw.
 *
 * Not persisted. This is a diagnostic: a restart should put the Hub back
 * in the state its configuration describes, rather than leaving an
 * experiment running that nobody remembers starting.
 */
app.MapGet("/api/librespot/volume-mode", (LibrespotOptions options) => Results.Ok(new
{
    volumeCtrlFixed = options.VolumeCtrlFixed,
    volumeEvents = options.VolumeEvents,
    meaning = options.VolumeCtrlFixed
        ? "librespot hands over samples untouched; the speaker's volume is the only one"
        : "librespot applies the phone's volume to the samples before the Hub sees them",
}));

app.MapPost("/api/librespot/volume-mode",
    (VolumeModeRequest request, LibrespotOptions options, ILoggerFactory loggers) =>
{
    if (request.VolumeCtrlFixed is bool fixedVolume)
    {
        options.VolumeCtrlFixed = fixedVolume;
    }
    if (request.VolumeEvents is bool events)
    {
        options.VolumeEvents = events;
    }

    // Loud, because this is the setting a bisect is turning and the whole
    // exercise depends on knowing which way it was turned.
    loggers.CreateLogger("Librespot").LogInformation(
        "Volume mode set to volumeCtrlFixed={Fixed}, volumeEvents={Events}; "
        + "receivers restart on the next tick",
        options.VolumeCtrlFixed, options.VolumeEvents);

    return Results.Ok(new
    {
        status = "set",
        volumeCtrlFixed = options.VolumeCtrlFixed,
        volumeEvents = options.VolumeEvents,
        note = "Receivers restart within a second. Not persisted: a Hub restart "
            + "returns to the configured values.",
    });
});

app.MapPost("/api/librespot/event/{castPointId}",
    async (string castPointId, HttpContext context, LibrespotService spotify,
           CancellationToken cancellationToken) =>
{
    if (!(context.Connection.RemoteIpAddress?.Equals(IPAddress.Loopback) ?? false)
        && !(context.Connection.RemoteIpAddress?.Equals(IPAddress.IPv6Loopback) ?? false))
    {
        return Results.NotFound();
    }

    var kind = context.Request.Query["event"].ToString();

    // Every event runs the script, and only one of them carries a volume.
    // The rest are ordinary and uninteresting, so they are accepted
    // quietly rather than refused noisily.
    var applied = await spotify.ApplyPhoneVolumeAsync(
        castPointId, context.Request.Query["volume"].ToString(), cancellationToken);

    return Results.Ok(new { status = applied is null ? "ignored" : "applied", eventKind = kind, volume = applied });
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

/*
 * The order of the buttons, which belongs to whoever presses them.
 *
 * A whole list rather than a move-up/move-down, because a drag produces one
 * final arrangement and sending it once cannot end half applied — two
 * separate swaps racing a refresh can.
 */
app.MapPut("/api/stations/order", (StationOrderRequest request, StationStore stations) =>
    stations.Reorder(request.Ids)
        ? Results.Ok(stations.Snapshot())
        : Results.BadRequest(new { error = "that is not this Hub's list of stations" }));

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
internal sealed record DelayRequest(int? DelayMs);

internal sealed record RingRequest(int? RingMs);

/// <summary>Whole decibels of capture gain for a microphone node.</summary>
internal sealed record MicGainRequest(int? MicGainDb);

/// <summary>
/// A named buffer setting, plus how far early this node plays. Omitting
/// <c>AlignMs</c> keeps whatever offset the node already had, which is what
/// changing only the profile should do.
/// </summary>
internal sealed record ProfileRequest(string? Profile, int? AlignMs);

/// <summary>
/// "line" or "mic" — which capture stage a Producer uses. Named Input to
/// match the node's <c>/config</c> key; the node reports it back as
/// <c>inputStage</c>, because <c>input</c> was already the live ADC levels.
/// </summary>
internal sealed record InputRequest(string? Input);

/// <summary>The node's name. Empty restores its MAC-derived default.</summary>
internal sealed record NameRequest(string? Name);

/// <summary>"i2s" or "usb" — which output stage the node brings up.</summary>
internal sealed record OutputRequest(string? Output);

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

/// <summary>Every station id, in the order they should appear.</summary>
internal sealed record StationOrderRequest(IReadOnlyList<string>? Ids = null);

internal sealed record StationRequest(string? Name, string? Url);

/// <summary>
/// Nullable rather than a plain int so a body with no percent at all is a
/// clear 400 instead of silently muting the room, which is what binding a
/// missing value to the default 0 would do.
/// </summary>
/// <summary>Either switch, or both; anything omitted is left alone.</summary>
internal sealed record VolumeModeRequest(bool? VolumeCtrlFixed, bool? VolumeEvents);

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
