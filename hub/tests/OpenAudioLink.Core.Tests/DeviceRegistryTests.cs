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
