using System.Net;
using Microsoft.Extensions.Time.Testing;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Core.Discovery;
using OpenAudioLink.Core.Protocol;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class DeviceRegistryTests
{
    private static DeviceAnnouncement Announce(string id = "mac-a0b1c2d3e4f5", string name = "testnode") => new()
    {
        ProtocolVersion = ProtocolSuite.Version,
        Id = id,
        Name = name,
        Roles = [DeviceRole.Consumer],
        HardwareProfile = "esp32c3-devkit",
        FirmwareVersion = "0.1.0",
    };

    [Fact]
    public void New_announce_creates_online_device()
    {
        var registry = new DeviceRegistry(new FakeTimeProvider());
        registry.Upsert(Announce(), IPAddress.Parse("192.168.1.40"));

        var device = Assert.Single(registry.Snapshot());
        Assert.True(device.Online);
        Assert.Equal("192.168.1.40", device.Address);
        Assert.Equal(ProtocolSuite.DeviceControlPort, device.ControlPort);
    }

    [Fact]
    public void Same_id_updates_instead_of_duplicating()
    {
        var registry = new DeviceRegistry(new FakeTimeProvider());
        registry.Upsert(Announce(), IPAddress.Parse("192.168.1.40"));
        registry.Upsert(Announce(name: "renamed"), IPAddress.Parse("192.168.1.41"));

        var device = Assert.Single(registry.Snapshot());
        Assert.Equal("renamed", device.Name);
        Assert.Equal("192.168.1.41", device.Address);
    }

    [Fact]
    public void Device_goes_offline_after_missed_announces()
    {
        var time = new FakeTimeProvider();
        var registry = new DeviceRegistry(time);
        registry.Upsert(Announce(), IPAddress.Loopback);

        time.Advance(DeviceRegistry.OfflineAfter - TimeSpan.FromSeconds(1));
        Assert.True(Assert.Single(registry.Snapshot()).Online);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(Assert.Single(registry.Snapshot()).Online);
    }

    [Fact]
    public void Announce_brings_offline_device_back()
    {
        var time = new FakeTimeProvider();
        var registry = new DeviceRegistry(time);
        registry.Upsert(Announce(), IPAddress.Loopback);
        time.Advance(TimeSpan.FromMinutes(5));
        registry.Upsert(Announce(), IPAddress.Loopback);

        Assert.True(registry.TryGet("mac-a0b1c2d3e4f5", out var device));
        Assert.True(device.Online);
    }
}

public class DeviceStatusTests
{
    private static DeviceAnnouncement Announce(string id = "mac-a0b1c2d3e4f5") => new()
    {
        ProtocolVersion = "0.1",
        Id = id,
        Name = "node",
        Roles = [DeviceRole.Consumer],
        HardwareProfile = "esp32s3-devkit",
        FirmwareVersion = "0.5.0",
    };

    private static DeviceStatus Reading(int rssi = -68) => new()
    {
        UptimeSeconds = 1234,
        Joined = true,
        Ssid = "valfrid-n",
        Bssid = "7c:10:c9:7a:0b:d0",
        Channel = 9,
        Rssi = rssi,
    };

    [Fact]
    public void A_device_without_a_reading_has_no_status()
    {
        var registry = new DeviceRegistry();
        registry.Upsert(Announce(), IPAddress.Parse("192.168.0.71"));

        Assert.Null(Assert.Single(registry.Snapshot()).Status);
    }

    [Fact]
    public void A_reading_appears_on_the_device()
    {
        var registry = new DeviceRegistry();
        registry.Upsert(Announce(), IPAddress.Parse("192.168.0.71"));

        registry.UpdateStatus("mac-a0b1c2d3e4f5", Reading());

        var status = Assert.Single(registry.Snapshot()).Status;
        Assert.Equal("7c:10:c9:7a:0b:d0", status!.Bssid);
        Assert.Equal(-68, status.Rssi);
        Assert.Equal(9, status.Channel);
    }

    /// <summary>
    /// Announces arrive every 5 s and rebuild the record; status is polled
    /// every 10 s. Held inside the record, every announce would wipe the
    /// last reading and the columns would flicker empty.
    /// </summary>
    [Fact]
    public void An_announce_does_not_discard_the_last_reading()
    {
        var registry = new DeviceRegistry();
        registry.Upsert(Announce(), IPAddress.Parse("192.168.0.71"));
        registry.UpdateStatus("mac-a0b1c2d3e4f5", Reading());

        registry.Upsert(Announce(), IPAddress.Parse("192.168.0.71"));

        Assert.NotNull(Assert.Single(registry.Snapshot()).Status);
    }

    [Fact]
    public void A_later_reading_replaces_the_earlier_one()
    {
        var registry = new DeviceRegistry();
        registry.Upsert(Announce(), IPAddress.Parse("192.168.0.71"));

        registry.UpdateStatus("mac-a0b1c2d3e4f5", Reading(rssi: -68));
        registry.UpdateStatus("mac-a0b1c2d3e4f5", Reading(rssi: -55));

        Assert.Equal(-55, Assert.Single(registry.Snapshot()).Status!.Rssi);
    }

    /// <summary>
    /// So the UI can show how old a reading is rather than implying it is
    /// current — the distinction that made a flapping device confusing.
    /// </summary>
    [Fact]
    public void A_reading_is_stamped_with_when_it_was_taken()
    {
        var time = new FakeTimeProvider();
        var registry = new DeviceRegistry(time);
        registry.Upsert(Announce(), IPAddress.Parse("192.168.0.71"));

        registry.UpdateStatus("mac-a0b1c2d3e4f5", Reading());

        Assert.Equal(time.GetUtcNow(), Assert.Single(registry.Snapshot()).Status!.ObservedAt);
    }

    [Fact]
    public void A_reading_for_an_unknown_device_is_simply_unused()
    {
        var registry = new DeviceRegistry();

        registry.UpdateStatus("mac-never-announced", Reading());

        Assert.Empty(registry.Snapshot());
    }

    private static DeviceAnnouncement Hub() => new()
    {
        ProtocolVersion = ProtocolSuite.Version,
        Id = "hub-01",
        Name = "Vardagsrum",
        Roles = DeviceRole.HubRoles,
        HardwareProfile = "windows-hub",
        FirmwareVersion = "0.9.4",
        ControlPort = ProtocolSuite.DefaultHubPort,
    };

    /// <summary>
    /// The Hub is a producer — it is what plays internet radio, system audio
    /// and the test tone — and every one of those is chosen by naming a
    /// producer from this list.
    /// </summary>
    [Fact]
    public void The_hub_is_in_its_own_inventory()
    {
        var registry = new DeviceRegistry(new FakeTimeProvider());

        registry.UpsertSelf(Hub(), IPAddress.Parse("192.168.0.201"));

        var hub = Assert.Single(registry.Snapshot());
        Assert.Equal("windows-hub", hub.HardwareProfile);
        Assert.Contains(DeviceRole.Producer, hub.Roles);
        Assert.True(registry.TryGet("hub-01", out _));
    }

    /// <summary>
    /// Nothing announces to the Hub on the Hub's behalf, so ageing its record
    /// out the way a silent node is aged out would take the radio away after
    /// thirty seconds of running perfectly.
    /// </summary>
    [Fact]
    public void The_hub_does_not_go_offline_by_saying_nothing()
    {
        var time = new FakeTimeProvider();
        var registry = new DeviceRegistry(time);
        registry.UpsertSelf(Hub(), IPAddress.Parse("192.168.0.201"));

        time.Advance(DeviceRegistry.OfflineAfter * 10);

        Assert.True(Assert.Single(registry.Snapshot()).Online);
    }

    /// <summary>
    /// Only the Hub gets that exemption. A node that stopped announcing is a
    /// node that is gone, and saying otherwise would send a stream into the
    /// dark.
    /// </summary>
    [Fact]
    public void A_node_still_goes_offline_when_the_hub_does_not()
    {
        var time = new FakeTimeProvider();
        var registry = new DeviceRegistry(time);
        registry.UpsertSelf(Hub(), IPAddress.Parse("192.168.0.201"));
        registry.Upsert(Announce(), IPAddress.Parse("192.168.0.235"));

        time.Advance(DeviceRegistry.OfflineAfter * 10);

        var devices = registry.Snapshot();
        Assert.True(devices.Single(d => d.Id == "hub-01").Online);
        Assert.False(devices.Single(d => d.Id == "mac-a0b1c2d3e4f5").Online);
    }
}
