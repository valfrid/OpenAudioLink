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

    /// <summary>When the Hub last read this, so a stale reading is visible as stale.</summary>
    public DateTimeOffset ObservedAt { get; init; }
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
