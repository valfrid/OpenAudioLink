using System.Text;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Core.Discovery;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class DeviceRoleTests
{
    [Theory]
    [InlineData("controller")]
    [InlineData("producer")]
    [InlineData("consumer")]
    public void Architecture_roles_are_known(string role)
    {
        Assert.True(DeviceRole.IsKnown(role));
    }

    /// <summary>
    /// The names the wire used before it was aligned to the architecture.
    /// They must not quietly keep working, or both vocabularies survive.
    /// </summary>
    [Theory]
    [InlineData("receiver")]
    [InlineData("analog-source")]
    [InlineData("hub")]
    [InlineData("")]
    public void Superseded_and_unknown_names_are_not_known(string role)
    {
        Assert.False(DeviceRole.IsKnown(role));
    }

    [Fact]
    public void Hub_announces_controller_and_producer()
    {
        Assert.Equal([DeviceRole.Controller, DeviceRole.Producer], DeviceRole.HubRoles);
    }

    /// <summary>
    /// The case the single-value field could not express: an analog source
    /// that also plays (docs/ARCHITECTURE.md section 2).
    /// </summary>
    [Fact]
    public void A_device_can_announce_several_roles()
    {
        var json = """
            {"oal":"0.1","type":"announce","id":"mac-a0b1c2d3e4f5","name":"vinyl",
             "roles":["producer","consumer"],"hw":"xiao-esp32s3-pcm1808","fw":"0.3.0"}
            """;

        Assert.True(DiscoveryMessage.TryParse(Encoding.UTF8.GetBytes(json), out var parsed));
        var announce = Assert.IsType<DeviceAnnouncement>(parsed);
        Assert.Equal([DeviceRole.Producer, DeviceRole.Consumer], announce.Roles);
    }

    /// <summary>
    /// A node running newer firmware must not look less capable than it
    /// claims, so roles this build does not recognise are still carried.
    /// </summary>
    [Fact]
    public void Unrecognised_roles_are_kept_not_dropped()
    {
        var json = """
            {"oal":"0.1","type":"announce","id":"mac-a0b1c2d3e4f5","name":"n",
             "roles":["consumer","time-master"],"hw":"esp32s3-devkit","fw":"9.0.0"}
            """;

        Assert.True(DiscoveryMessage.TryParse(Encoding.UTF8.GetBytes(json), out var parsed));
        var announce = Assert.IsType<DeviceAnnouncement>(parsed);
        Assert.Contains("time-master", announce.Roles);
        Assert.False(DeviceRole.IsKnown("time-master"));
    }

    [Fact]
    public void An_announce_without_roles_is_rejected()
    {
        var json = """
            {"oal":"0.1","type":"announce","id":"mac-a0b1c2d3e4f5","name":"n",
             "hw":"esp32s3-devkit","fw":"0.3.0"}
            """;

        Assert.False(DiscoveryMessage.TryParse(Encoding.UTF8.GetBytes(json), out _));
    }
}
