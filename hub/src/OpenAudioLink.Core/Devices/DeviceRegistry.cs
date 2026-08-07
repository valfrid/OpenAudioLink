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
    /// Which of the stream's two channels this node plays: stereo, mono,
    /// left or right (decision 10). Read from /status rather than the
    /// announce, so the multicast every device hears stays lean.
    /// </summary>
    public string? AudioChannel { get; init; }

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
        Online = now - device.LastSeen < OfflineAfter,
        Status = _status.GetValueOrDefault(device.Id),
    };
}
