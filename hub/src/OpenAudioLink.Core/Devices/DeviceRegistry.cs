using System.Collections.Concurrent;
using System.Net;
using OpenAudioLink.Core.Discovery;
using OpenAudioLink.Core.Protocol;

namespace OpenAudioLink.Core.Devices;

/// <summary>
/// A device as known to the Controller: last announced attributes plus
/// liveness derived from the announce interval (protocol/DISCOVERY.md).
/// </summary>
public sealed record DeviceRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required string HardwareProfile { get; init; }
    public required string FirmwareVersion { get; init; }
    public required string ProtocolVersion { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public required string Address { get; init; }
    public int ControlPort { get; init; } = ProtocolSuite.DeviceControlPort;
    public DateTimeOffset LastSeen { get; init; }
    public bool Online { get; init; }
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
            Role = announcement.Role,
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

    public IReadOnlyList<DeviceRecord> Snapshot()
    {
        var now = _time.GetUtcNow();
        return _devices.Values
            .Select(d => d with { Online = now - d.LastSeen < OfflineAfter })
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool TryGet(string id, out DeviceRecord record)
    {
        if (_devices.TryGetValue(id, out var stored))
        {
            record = stored with { Online = _time.GetUtcNow() - stored.LastSeen < OfflineAfter };
            return true;
        }

        record = null!;
        return false;
    }
}
