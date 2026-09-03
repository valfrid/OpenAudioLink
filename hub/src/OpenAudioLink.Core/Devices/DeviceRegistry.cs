using System.Collections.Concurrent;
using System.Net;
using OpenAudioLink.Core.Discovery;
using OpenAudioLink.Core.Protocol;

namespace OpenAudioLink.Core.Devices;

/// <summary>
/// What a device reports about its own running state (protocol/CONTROL.md
/// <c>GET /status</c>), as opposed to the identity it announces.
///
/// The Wi-Fi fields travel together on purpose. In a mesh every access
/// point advertises the same SSID, so a weak RSSI alone cannot tell "far
/// from the right node" from "attached to the wrong one" — the BSSID is
/// what answers that, and it was the missing piece when a node sat at
/// -77 dBm two metres from a stronger one.
/// </summary>
public sealed record DeviceStatus
{
    public long UptimeSeconds { get; init; }
    public long? FreeHeapBytes { get; init; }
    public bool Joined { get; init; }
    public string? Ssid { get; init; }
    public string? Bssid { get; init; }
    public int? Channel { get; init; }
    public int? Rssi { get; init; }

    /// <summary>
    /// Times this node has changed access point, been disconnected, and why
    /// the last disconnect happened. Null from firmware that predates them.
    /// </summary>
    /// <remarks>
    /// The counters that separate a link fault from everything it looks
    /// like downstream. A node that drops off for a few seconds loses a
    /// thousand consecutive packets, and from the audio counters that is
    /// indistinguishable from interference — same loss, same stalls, and a
    /// completely different fix.
    ///
    /// <see cref="LastReason"/> is an ESP-IDF wifi_err_reason_t. 8 is
    /// ASSOC_LEAVE, the access point asking the node to go, which is what
    /// steering looks like from the node; 200 is BEACON_TIMEOUT, the node
    /// losing the access point. One is fixed on the router and the other
    /// with an antenna or a wire.
    /// </remarks>
    public int? Roams { get; init; }

    public int? Disconnects { get; init; }

    public int? LastReason { get; init; }

    /// <summary>
    /// Extra playout delay this node holds, in milliseconds, or null from
    /// firmware that predates it.
    /// </summary>
    /// <remarks>
    /// Per node, because it corrects a difference *between* nodes rather
    /// than a property of the installation: given the same packet a USB
    /// dongle plays tens of milliseconds later than an I²S DAC, so the DAC
    /// has to be held back to meet it. Which one needs the trim depends on
    /// what is plugged into it.
    ///
    /// Only ever positive — nothing plays a sample before it arrives, so
    /// alignment is always the early node waiting for the late one.
    /// </remarks>
    public int? DelayMs { get; init; }

    /// <summary>
    /// What a Producer captures from — "line" for an I²S ADC, "mic" for an
    /// I²S microphone. Null on a Consumer, or from firmware predating it.
    /// </summary>
    /// <remarks>
    /// One box serves both: a microphone at the listening position for room
    /// measurement, a line input by the turntable, never at once. The two
    /// need the node in opposite clock roles, which is why this is read at
    /// boot rather than switched while running.
    /// </remarks>
    public string? InputStage { get; init; }

    /// <summary>
    /// How much audio this node's ring holds, in milliseconds, as actually
    /// allocated — capacity, not the target depth. Null from firmware that
    /// predates a settable ring.
    /// </summary>
    /// <remarks>
    /// Capacity and target are separate on purpose. The target is where the
    /// buffer normally sits and moves while playing; this is how much room
    /// exists above and below it, and it can only change at a reboot because
    /// it is an allocation.
    /// </remarks>
    public int? RingMs { get; init; }

    /// <summary>Three quarters of the ring: the deepest target it will hold.</summary>
    public int? MaxTargetMs { get; init; }

    /// <summary>
    /// The largest delay this node will accept, as the node reports it.
    /// </summary>
    /// <remarks>
    /// Always read, never assumed. The Delay dialog offered 0-200 ms for two
    /// releases against a real ceiling of 50, because one limit was written
    /// in two places. Once the ring became a setting no single constant
    /// could be right anyway: the ceiling is 50 on a 200 ms ring and 650 on
    /// a 1000 ms one, on nodes sitting side by side.
    /// </remarks>
    public int? MaxDelayMs { get; init; }

    /// <summary>
    /// Capture gain on a microphone node, in whole decibels.
    /// </summary>
    /// <remarks>
    /// Nothing else in the chain can supply it. A consumer's volume
    /// attenuates and never amplifies — deliberately, because the streams
    /// it normally rides arrive near full scale — and an ICS-43434 puts an
    /// ordinary room around −45 dBFS. The first microphone stream was
    /// perfectly audible and far too quiet with every consumer at 100 %,
    /// which is what this exists to fix.
    /// </remarks>
    public int? MicGainDb { get; init; }

    /*
     * Room correction as the node holds it (docs/ROOM-CALIBRATION.md).
     *
     * The vectors are the readable triples the node stores, not
     * coefficients, so what is shown here is exactly what is in NVS and can
     * be handed straight back for editing. Null from firmware predating it.
     */
    public string? EqLeft { get; init; }

    public string? EqRight { get; init; }

    public bool? EqEnabled { get; init; }

    public double? EqPreampDb { get; init; }

    /// <summary>
    /// Which app slot is running, and whether the image in it is confirmed.
    /// </summary>
    /// <remarks>
    /// Rollback without reporting is worse than no rollback: a reverted
    /// node comes back online, joined, streaming, reporting the *old*
    /// version, looking entirely normal. The update reads as one that never
    /// arrived — and "the download failed" and "the image installed and
    /// rejected itself" want completely different responses.
    ///
    /// <see cref="OtaOtherState"/> is the signal. After a rollback the
    /// running slot is valid, because it is the restored image and it is
    /// fine; the slot that was just tried reads aborted or invalid.
    ///
    /// Null from firmware that predates rollback.
    /// </remarks>
    public string? OtaSlot { get; init; }

    /// <summary>"valid" once confirmed, "pending" while on probation.</summary>
    public string? OtaState { get; init; }

    /// <summary>What became of the image in the other slot.</summary>
    public string? OtaOtherState { get; init; }

    /// <summary>Why this node last booted. Narrows a panic from a power cut.</summary>
    public string? ResetReason { get; init; }

    /// <summary>
    /// Which of the stream's two channels this node plays: stereo, mono,
    /// left or right (decision 10). Read from /status rather than the
    /// announce, so the multicast every device hears stays lean.
    /// </summary>
    public string? AudioChannel { get; init; }

    /// <summary>
    /// How audio leaves this board: "i2s" for a soldered DAC, "usb" for a
    /// dongle the node hosts (docs/USB-AUDIO.md). Null from firmware older
    /// than the setting, which is I2S by definition — it is the only stage
    /// that existed.
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// Whether that stage can currently take samples.
    ///
    /// The field worth watching. A USB node with no dongle plugged in is
    /// online, joined, claimed and streaming, with every counter rising and
    /// no sound in the room — and this is the only thing that says so. Null
    /// from firmware that predates it.
    /// </summary>
    public bool? OutputReady { get; init; }

    /// <summary>
    /// Playback level, 0-100, as the node reports it actually is — not as
    /// the Hub last asked for. The two differ while a request is in flight
    /// and after one that failed, and a slider that shows what was asked
    /// rather than what happened is a slider that lies about an offline
    /// speaker.
    ///
    /// Null from firmware that predates volume, which is a different thing
    /// from a speaker turned all the way down.
    /// </summary>
    public int? Volume { get; init; }

    /// <summary>
    /// What the node's analog input is hearing, or null on a node with no
    /// ADC.
    /// </summary>
    /// <remarks>
    /// The only reading that distinguishes a working ADC from a connected
    /// one. Everything else a capture path reports counts frames, and an
    /// ADC produces frames whether or not a turntable is plugged into it —
    /// which is how a first attempt at wiring one looks perfectly healthy
    /// and makes no sound.
    /// </remarks>
    public InputLevel? Input { get; init; }

    /// <summary>
    /// Who this node believes holds the Controller role: "self", the name
    /// of a peer, or null when it has found nobody (decision 9).
    ///
    /// Without it the house case is invisible — a Consumer asks, the Hub
    /// says stand by, and nothing happens, which is correct and looks
    /// exactly like nothing working.
    /// </summary>
    public string? Controller { get; init; }

    /// <summary>The last answer a Controller gave this node's join request.</summary>
    public string? JoinStatus { get; init; }

    /// <summary>When the Hub last read this, so a stale reading is visible as stale.</summary>
    public DateTimeOffset ObservedAt { get; init; }
}

/// <summary>
/// Peak level on a node's analog input, in whole decibels below full scale.
/// </summary>
/// <param name="LeftDb">Left channel, -120 for digital silence.</param>
/// <param name="RightDb">
/// Right channel. Reported apart from the left because a turntable is the
/// one source where half of it failing is ordinary — a lifted ground, a bad
/// RCA, a worn cartridge coil — and one number for both would report that as
/// merely quiet.
/// </param>
/// <param name="Hz">
/// The rate the ADC's own clock turned out to be, measured rather than
/// configured.
/// </param>
/// <param name="ReadErrors">Times the I2S driver refused a read.</param>
public readonly record struct InputLevel(int LeftDb, int RightDb, int Hz, int ReadErrors)
{
    /// <summary>Nothing above the noise floor on either channel.</summary>
    public bool Silent => LeftDb <= -120 && RightDb <= -120;
}

/// <summary>
/// A device as known to the Controller: last announced attributes plus
/// liveness derived from the announce interval (protocol/DISCOVERY.md).
/// </summary>
public sealed record DeviceRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<string> Roles { get; init; }
    public required string HardwareProfile { get; init; }
    public required string FirmwareVersion { get; init; }
    public required string ProtocolVersion { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public required string Address { get; init; }
    public int ControlPort { get; init; } = ProtocolSuite.DeviceControlPort;
    public DateTimeOffset LastSeen { get; init; }
    public bool Online { get; init; }

    /// <summary>Last successful /status read, or null if never polled.</summary>
    public DeviceStatus? Status { get; init; }
}

/// <summary>
/// Thread-safe device inventory keyed by device identity. An announce for a
/// known id updates the record (including a changed IP address); it never
/// creates a duplicate device.
/// </summary>
public sealed class DeviceRegistry
{
    /// <summary>
    /// How long silence means offline. Deliberately generous: announces are
    /// multicast, which Wi-Fi neither acknowledges nor retransmits, so a
    /// tight window makes a healthy device flap between states. The Hub also
    /// probes devices directly, and a probe's reply is unicast, so several
    /// chances to report in fit inside this window.
    /// </summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, DeviceRecord> _devices = new();

    /// <summary>
    /// Kept apart from the records because announces and status arrive
    /// independently: an announce rebuilds the record wholesale, and
    /// storing status inside it would discard the last reading every five
    /// seconds.
    /// </summary>
    private readonly ConcurrentDictionary<string, DeviceStatus> _status = new();

    private readonly TimeProvider _time;

    /// <summary>
    /// The Hub's own id, once it has registered itself, so its record is
    /// never aged out. Everything else goes offline by falling silent; this
    /// one cannot, because the code deciding is the thing being asked about.
    /// </summary>
    private string? _self;

    public DeviceRegistry(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public DeviceRecord Upsert(DeviceAnnouncement announcement, IPAddress address)
    {
        var record = new DeviceRecord
        {
            Id = announcement.Id,
            Name = announcement.Name,
            Roles = announcement.Roles,
            HardwareProfile = announcement.HardwareProfile,
            FirmwareVersion = announcement.FirmwareVersion,
            ProtocolVersion = announcement.ProtocolVersion,
            Capabilities = announcement.Capabilities ?? [],
            Address = address.ToString(),
            ControlPort = announcement.ControlPort ?? ProtocolSuite.DeviceControlPort,
            LastSeen = _time.GetUtcNow(),
            Online = true,
        };
        _devices[record.Id] = record;
        return record;
    }

    /// <summary>
    /// Puts the Hub in its own inventory, as the device it is.
    /// </summary>
    /// <remarks>
    /// The Hub holds the Producer role — it is what sends internet radio,
    /// system audio and the test tone — but it learned about devices only by
    /// hearing them announce, and it skips its own announce to avoid
    /// answering itself. So it was the one producer in the house that was
    /// never in the list.
    ///
    /// Everything downstream reads the registry, so everything downstream
    /// agreed it did not exist: <c>POST /castpoints/{id}/play</c> rejected
    /// it as an unknown producer, and the switchboard looked for a
    /// windows-hub device, found none, and quietly disabled every control
    /// that needed one. Pressing a radio station did nothing at all — no
    /// sound, no error, because the branch that would have complained was
    /// the branch that never ran.
    ///
    /// Registering it here rather than special-casing it at each call site
    /// is the point: a producer is a producer, and the Hub differs only in
    /// being reached by calling a method instead of opening a socket.
    /// </remarks>
    public DeviceRecord UpsertSelf(DeviceAnnouncement announcement, IPAddress address)
    {
        _self = announcement.Id;
        return Upsert(announcement, address);
    }

    /// <summary>Devices that must not be polled, and until when.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _quiet = new();

    /// <summary>
    /// Stops the Hub polling a device for a while, because it is busy with
    /// something that polling can ruin.
    /// </summary>
    /// <remarks>
    /// Written for OTA. A node downloading an image is running an HTTP
    /// client, writing flash, and serving the Hub's /status every ten
    /// seconds and /stream every five — all on one small control server.
    /// That is peak load at the worst possible moment: if the node dies
    /// mid-download the write is abandoned, the boot slot never changes,
    /// and it comes back running exactly what it was running before.
    ///
    /// Which is what happened. Firmware 0.14.0 fixes a control-server stack
    /// overflow, and on 0.13.0 that overflow is reliably triggered by the
    /// polling that accompanies an update — so the fix could not install
    /// itself. The update looked like it did nothing.
    ///
    /// Polling is a convenience here, not a mechanism; nothing depends on a
    /// reading arriving during the ninety seconds an update takes. Liveness
    /// still comes from announces, so a node that dies while quiet is still
    /// noticed on the usual timer.
    /// </remarks>
    public void Hush(string id, TimeSpan duration) =>
        _quiet[id] = _time.GetUtcNow() + duration;

    /// <summary>Whether this device is inside a quiet period.</summary>
    public bool IsHushed(string id) =>
        _quiet.TryGetValue(id, out var until) && _time.GetUtcNow() < until;

    /// <summary>Records a /status reading for a device the Hub polled.</summary>
    public void UpdateStatus(string id, DeviceStatus status)
    {
        _status[id] = status with { ObservedAt = _time.GetUtcNow() };
    }

    public IReadOnlyList<DeviceRecord> Snapshot()
    {
        var now = _time.GetUtcNow();
        return _devices.Values
            .Select(d => Decorate(d, now))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool TryGet(string id, out DeviceRecord record)
    {
        if (_devices.TryGetValue(id, out var stored))
        {
            record = Decorate(stored, _time.GetUtcNow());
            return true;
        }

        record = null!;
        return false;
    }

    private DeviceRecord Decorate(DeviceRecord device, DateTimeOffset now) => device with
    {
        // The Hub does not announce to itself, so ageing its own record out
        // would mark it offline after thirty seconds of running perfectly.
        Online = device.Id == _self || now - device.LastSeen < OfflineAfter,
        Status = _status.GetValueOrDefault(device.Id),
    };
}
