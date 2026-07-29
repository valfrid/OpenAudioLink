using System.Text;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Core.Discovery;
using OpenAudioLink.Core.Protocol;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class DiscoveryMessageTests
{
    private static readonly DeviceAnnouncement Sample = new()
    {
        ProtocolVersion = ProtocolSuite.Version,
        Id = "mac-a0b1c2d3e4f5",
        Name = "testnode",
        Roles = [DeviceRole.Consumer],
        HardwareProfile = "esp32c3-devkit",
        FirmwareVersion = "0.1.0",
        Capabilities = ["control-v0"],
        ControlPort = 41001,
    };

    [Fact]
    public void Announce_round_trips()
    {
        Assert.True(DiscoveryMessage.TryParse(Sample.Serialize(), out var parsed));
        var announce = Assert.IsType<DeviceAnnouncement>(parsed);
        Assert.Equal(Sample.Id, announce.Id);
        Assert.Equal(Sample.Name, announce.Name);
        Assert.Equal(Sample.Roles, announce.Roles);
        Assert.Equal(Sample.HardwareProfile, announce.HardwareProfile);
        Assert.Equal(Sample.FirmwareVersion, announce.FirmwareVersion);
        Assert.Equal(Sample.Capabilities, announce.Capabilities);
        Assert.Equal(Sample.ControlPort, announce.ControlPort);
    }

    [Fact]
    public void Probe_round_trips()
    {
        var probe = new DiscoveryProbe { ProtocolVersion = ProtocolSuite.Version };
        Assert.True(DiscoveryMessage.TryParse(probe.Serialize(), out var parsed));
        Assert.IsType<DiscoveryProbe>(parsed);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        var json = """{"oal":"0.1","type":"announce","id":"mac-a0b1c2d3e4f5","name":"n","roles":["consumer"],"hw":"esp32c3-devkit","fw":"0.1.0","futureField":42}""";
        Assert.True(DiscoveryMessage.TryParse(Encoding.UTF8.GetBytes(json), out var parsed));
        Assert.IsType<DeviceAnnouncement>(parsed);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"type":"announce"}""")]
    [InlineData("""{"oal":"1.0","type":"probe"}""")]
    [InlineData("""{"oal":"0.1","type":"unknown"}""")]
    [InlineData("""{"oal":"0.1","type":"announce","id":"mac-a0b1c2d3e4f5"}""")]
    public void Invalid_messages_are_rejected(string json)
    {
        Assert.False(DiscoveryMessage.TryParse(Encoding.UTF8.GetBytes(json), out var parsed));
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData("0.1", true)]
    [InlineData("0.2", true)]
    [InlineData("1.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Compatibility_uses_major_version(string? version, bool compatible)
    {
        Assert.Equal(compatible, ProtocolSuite.IsCompatible(version));
    }
}
