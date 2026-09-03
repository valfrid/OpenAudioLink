using System.Text;
using System.Text.Json;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Core.Net;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// Sends control-plane commands to a device's control API
/// (protocol/CONTROL.md). Audio never flows here.
/// </summary>
public sealed class DeviceCommandClient
{
    private readonly HttpClient _http;
    private readonly int _hubPort;
    private readonly ILogger<DeviceCommandClient> _logger;

    public DeviceCommandClient(HttpClient http, IConfiguration configuration, ILogger<DeviceCommandClient> logger)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _hubPort = ParseHubPort(configuration["Urls"]);
        _logger = logger;
    }

    public async Task<bool> RebootAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        return await PostAsync(device, "/reboot", body: null, cancellationToken);
    }

    /// <summary>
    /// Tells a node to forget the access point it is on and join again from
    /// a fresh scan.
    /// </summary>
    /// <remarks>
    /// A reboot ought to do this and does not: a node carried to within a
    /// metre of one access point kept rejoining one twenty metres away,
    /// through reboots and a power cycle. Reboot is the bigger hammer and
    /// the wrong one — it costs the stream, the claim and thirty seconds,
    /// and it did not work anyway.
    ///
    /// Cheaper than it looks from the outside, too. The node stays up, so
    /// its claim and configuration survive; what it loses is a second or two
    /// of audio while the radio scans.
    /// </remarks>
    public async Task<bool> RejoinWifiAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Asking {Device} to rejoin Wi-Fi from a fresh scan", device.Name);
        return await PostAsync(device, "/wifi/rejoin", body: null, cancellationToken);
    }

    /// <summary>
    /// What access points a node can hear, as the node hears them.
    /// </summary>
    /// <remarks>
    /// Rejoin asks a node to choose again and answers only whether the
    /// request was accepted — not what it chose, and not what it was
    /// choosing between. When a rejoin does not fix a node stuck on a
    /// distant access point, that difference is the whole question: whether
    /// the better one was there to be picked and was passed over, or was
    /// never on the list.
    ///
    /// Returned as the node's own JSON rather than reshaped here. It is a
    /// radio's opinion of the room, taken at one instant, and every hop it
    /// passes through is a chance to average away the thing worth seeing.
    ///
    /// Slower than the other calls: a scan sweeps every channel, so the
    /// node is off its own for a second or two and this can take most of
    /// ten. That is also why it is never called on a timer.
    /// </remarks>
    public async Task<string?> ScanWifiAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        var uri = $"http://{device.Address}:{device.ControlPort}/wifi/scan";
        try
        {
            using var response = await _http.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Scan on {Device} returned {Status}", device.Name, (int)response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Scan on {Device} failed", device.Name);
            return null;
        }
    }

    /// <summary>
    /// Stores the roles a node takes (decision 5). They apply at its next
    /// boot, because roles decide which tasks start — changing them under a
    /// running node would mean tearing down live audio.
    /// </summary>
    public async Task<bool> SetRolesAsync(
        DeviceRecord device, IReadOnlyList<string> roles, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Setting roles on {Device} to {Roles}", device.Name, string.Join(", ", roles));
        return await PostAsync(device, "/config", JsonSerializer.Serialize(new { roles }), cancellationToken);
    }

    /// <summary>
    /// Adds a destination to a running stream without interrupting it, so a
    /// speaker that asks to join hears the record everyone else is already
    /// listening to rather than restarting it for them.
    /// </summary>
    public async Task<bool> AddDestinationAsync(
        DeviceRecord producer, string address, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { add = new[] { address } });
        return await PostAsync(producer, "/stream/destinations", body, cancellationToken);
    }

    /// <summary>
    /// Adds and removes destinations in one request, without interrupting
    /// the stream.
    /// </summary>
    /// <remarks>
    /// One request rather than two because the node applies removals first
    /// (protocol/CONTROL.md): a speaker that came back on a new address is
    /// a remove and an add of the same box, and doing them separately can
    /// fill the set with the entry that is on its way out.
    /// </remarks>
    public async Task<bool> SetStreamDestinationsAsync(
        DeviceRecord producer, IReadOnlyList<string> add, IReadOnlyList<string> remove,
        CancellationToken cancellationToken)
    {
        if (add.Count == 0 && remove.Count == 0)
        {
            return true;
        }
        var body = JsonSerializer.Serialize(new { add, remove });
        return await PostAsync(producer, "/stream/destinations", body, cancellationToken);
    }

    /// <summary>
    /// Sets which of the stream's two channels the device plays (decision
    /// 10). Sent on its own rather than alongside roles, so changing a
    /// speaker from stereo to mono cannot disturb what it is.
    /// </summary>
    public async Task<bool> SetChannelAsync(
        DeviceRecord device, string channel, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Setting channel on {Device} to {Channel}", device.Name, channel);
        return await PostAsync(device, "/config", JsonSerializer.Serialize(new { channel }), cancellationToken);
    }

    /// <summary>
    /// Sets this node's extra playout delay, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Applies immediately and persists on the node, like volume rather
    /// than like roles. The node's pad/trim servo walks the ring to the new
    /// depth at a tenth of a percent — about a second of delay per thirty
    /// seconds of music, under two cents of pitch error — so the change
    /// slides in rather than jumping.
    ///
    /// That is deliberate and it is why this is worth a button: alignment
    /// is judged by ear against another speaker in the room, and a value
    /// that jumped would have to be re-judged from scratch after every
    /// nudge.
    /// </remarks>
    public async Task<bool> SetDelayAsync(
        DeviceRecord device, int delayMs, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Setting delay on {Device} to {DelayMs} ms", device.Name, delayMs);
        return await PostAsync(
            device, "/config", JsonSerializer.Serialize(new { delayMs }), cancellationToken);
    }

    /// <summary>
    /// Renames the node.
    /// </summary>
    /// <remarks>
    /// The one setting on this endpoint that lands immediately. Everything
    /// else decides which tasks start or which pins the I2S driver claims
    /// and waits for a boot; a name decides nothing, it is a label on an
    /// announce. Making a typo wait for a reboot is the kind of friction
    /// that stops people fixing it.
    ///
    /// An empty name is a deliberate erase, restoring the node's
    /// MAC-derived default — which is a real thing to want, because that
    /// default is what makes an unnamed node recognisably the one just
    /// provisioned.
    /// </remarks>
    public async Task<bool> SetNameAsync(
        DeviceRecord device, string name, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Renaming {Device} to {Name}", device.Name, name);
        return await PostAsync(
            device, "/config", JsonSerializer.Serialize(new { name }), cancellationToken);
    }

    /// <summary>
    /// Sets how audio leaves the board: "i2s" or "usb".
    /// </summary>
    /// <remarks>
    /// Asked for at provisioning and, until now, nowhere else — so a node
    /// that grew a dongle after it was set up could not be told about it
    /// without a re-provision, which costs the Wi-Fi password too.
    ///
    /// Applies at the next boot: it decides which output stage is brought
    /// up, and the choice is made once when the audio path starts.
    /// </remarks>
    public async Task<bool> SetOutputAsync(
        DeviceRecord device, string output, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Setting output on {Device} to {Output}", device.Name, output);
        return await PostAsync(
            device, "/config", JsonSerializer.Serialize(new { output }), cancellationToken);
    }

    /// <summary>
    /// Sets what a Producer captures from: "line" or "mic".
    /// </summary>
    /// <remarks>
    /// Applies at the next boot, and cannot sensibly do otherwise: the
    /// choice selects a set of GPIO pins <i>and</i> which end of the I²S bus
    /// generates the bit and word clocks. A self-clocked PCM1808 module
    /// drives them itself and makes the node a follower; an ICS-43434
    /// microphone is a slave and needs them supplied. Neither is something
    /// the I²S driver can be talked into changing mid-capture.
    /// </remarks>
    public async Task<bool> SetInputAsync(
        DeviceRecord device, string input, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Setting input on {Device} to {Input}", device.Name, input);
        return await PostAsync(
            device, "/config", JsonSerializer.Serialize(new { input }), cancellationToken);
    }

    /// <summary>
    /// Sets how much audio this node's ring holds, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Capacity, not target — how much room the buffer has, not where it
    /// normally sits. Applies at the next boot, unlike the delay, because
    /// the ring is an allocation and resizing it under a running playout
    /// would mean freeing the buffer the audio task is reading from.
    ///
    /// Worth exposing at all because the right value is not knowable from
    /// here. This project runs a 100 ms target in a 200 ms ring where
    /// Snapcast runs 1000 ms, on a network measured leaving 900 ms holes
    /// that no 200 ms ring can absorb. Which size actually sounds best is an
    /// experiment, and an experiment needs a knob rather than a rebuild and
    /// a cable — particularly for the node that is awkward to reach.
    /// </remarks>
    /// <summary>
    /// Capture gain for a microphone node, in whole decibels.
    /// </summary>
    /// <remarks>
    /// Applies at the node's next boot, like the ring and unlike the
    /// delay: the capture task reads it once when the I²S channel comes
    /// up, and a microphone's level is not something anybody rides during
    /// a take.
    /// </remarks>
    public async Task<bool> SetMicGainAsync(
        DeviceRecord device, int micGainDb, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Setting microphone gain on {Device} to {Db} dB", device.Name, micGainDb);
        return await PostAsync(
            device, "/config", JsonSerializer.Serialize(new { micGainDb }), cancellationToken);
    }

    /// <summary>
    /// Writes a room correction (docs/ROOM-CALIBRATION.md).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both vectors, the shared headroom and the switch in one request, so
    /// a node is never left running one channel's correction against the
    /// other's — which would be audible as the stereo image walking
    /// sideways, and would be nobody's fault in particular.
    /// </para>
    /// <para>
    /// The vectors are the readable triples the node stores, not
    /// coefficients: the node derives those, and a person reading
    /// <c>/status</c> should see what their loudspeaker is doing.
    /// </para>
    /// </remarks>
    public async Task<bool> SetCorrectionAsync(
        DeviceRecord device, string eqLeft, string eqRight, double preampDb, bool enabled,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Setting room correction on {Device}: L [{Left}] R [{Right}], preamp {Preamp} dB, {State}",
            device.Name, eqLeft, eqRight, preampDb, enabled ? "on" : "off");
        return await PostAsync(
            device, "/config",
            JsonSerializer.Serialize(new { eqLeft, eqRight, eqPreampDb = preampDb, eqEnabled = enabled }),
            cancellationToken);
    }

    /// <summary>
    /// Turns a stored correction on or off without touching it.
    /// </summary>
    /// <remarks>
    /// The control that makes a correction checkable. Comparing corrected
    /// against uncorrected must not mean deleting a profile and measuring
    /// again to get it back, or nobody will ever compare — and whether the
    /// correction helped is the one thing worth knowing.
    /// </remarks>
    public async Task<bool> SetCorrectionEnabledAsync(
        DeviceRecord device, bool enabled, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Room correction {State} on {Device}", enabled ? "on" : "off", device.Name);
        return await PostAsync(
            device, "/config", JsonSerializer.Serialize(new { eqEnabled = enabled }),
            cancellationToken);
    }

    /// <summary>
    /// Writes one or both vectors as given, without touching the switch or
    /// the headroom.
    /// </summary>
    /// <remarks>
    /// The hand-tuning path. Only the sides actually named are sent:
    /// echoing the other one back as it was last read would race a change
    /// made elsewhere since.
    /// </remarks>
    public async Task<bool> SetEqVectorAsync(
        DeviceRecord device, string? eqLeft, string? eqRight, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>();
        if (eqLeft is not null)
        {
            body["eqLeft"] = eqLeft;
        }
        if (eqRight is not null)
        {
            body["eqRight"] = eqRight;
        }

        _logger.LogInformation(
            "Writing eq on {Device}: {Body}", device.Name, JsonSerializer.Serialize(body));
        return await PostAsync(
            device, "/config", JsonSerializer.Serialize(body), cancellationToken);
    }

    public async Task<bool> SetRingAsync(
        DeviceRecord device, int ringMs, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Setting ring on {Device} to {RingMs} ms", device.Name, ringMs);
        return await PostAsync(
            device, "/config", JsonSerializer.Serialize(new { ringMs }), cancellationToken);
    }

    /// <summary>
    /// Sets the device's playback level, 0-100. Applies immediately on the
    /// node and persists there.
    /// </summary>
    /// <remarks>
    /// Its own endpoint rather than a field on <c>/config</c>, because
    /// <c>/config</c> means "stored, applies at reboot" and this means "the
    /// room is quieter now". Deliberately not logged at information level:
    /// a slider produces a request per movement, and a log that fills with
    /// them buries the events worth reading.
    /// </remarks>
    public async Task<bool> SetVolumeAsync(
        DeviceRecord device, int percent, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        _logger.LogDebug("Setting volume on {Device} to {Percent}%", device.Name, clamped);
        return await PostAsync(
            device, "/volume", JsonSerializer.Serialize(new { percent = clamped }), cancellationToken);
    }

    /// <summary>
    /// Tells the device to pull and install a firmware image from this Hub.
    /// The download URL uses the Hub address as seen from the device's
    /// subnet, so multi-homed hosts advertise a reachable address.
    /// </summary>
    public async Task<bool> StartOtaAsync(DeviceRecord device, string firmwareFile, CancellationToken cancellationToken)
    {
        var hubAddress = LocalAddressSelector.ForDevice(device.Address);
        var url = $"http://{hubAddress}:{_hubPort}/firmware/{Uri.EscapeDataString(firmwareFile)}";
        // Worth logging: when an update fails at connect, the first question
        // is always whether the node was handed an address it can reach.
        _logger.LogInformation(
            "Update for {Device} at {DeviceAddress}: serving {Url}", device.Name, device.Address, url);
        return await PostAsync(device, "/ota", JsonSerializer.Serialize(new { url }), cancellationToken);
    }

    /// <summary>
    /// Tells a producer to stream to the given consumers. The Hub does not
    /// relay audio (ARCHITECTURE.md section 3): it names the destinations
    /// and the producer sends to them directly.
    /// </summary>
    public async Task<bool> StartStreamAsync(
        DeviceRecord device, IReadOnlyList<string> destinations, int port,
        string source, int toneHz, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting {Source} stream on {Device} to {Count} destination(s) on port {Port}",
            source, device.Name, destinations.Count, port);
        var body = JsonSerializer.Serialize(new { destinations, port, source, toneHz });
        return await PostAsync(device, "/stream/start", body, cancellationToken);
    }

    /// <summary>Stops a producer, or clears a consumer's counters.</summary>
    public async Task<bool> StopStreamAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        return await PostAsync(device, "/stream/stop", body: null, cancellationToken);
    }

    /// <summary>Reads a node's stream state as raw JSON, shape depending on its role.</summary>
    public async Task<JsonDocument?> GetStreamAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        var uri = $"http://{device.Address}:{device.ControlPort}/stream";
        try
        {
            using var response = await _http.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "GET {Uri} failed", uri);
            return null;
        }
    }

    private async Task<bool> PostAsync(DeviceRecord device, string path, string? body, CancellationToken cancellationToken)
    {
        var uri = $"http://{device.Address}:{device.ControlPort}{path}";
        try
        {
            using var content = body is null
                ? null
                : new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(uri, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("POST {Uri} returned {Status}", uri, (int)response.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "POST {Uri} failed", uri);
            return false;
        }
    }

    private static int ParseHubPort(string? urls)
    {
        var first = urls?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is not null
            && Uri.TryCreate(first.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri))
        {
            return uri.Port;
        }
        return Core.Protocol.ProtocolSuite.DefaultHubPort;
    }
}
