using OpenAudioLink.Core.Devices;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class DeviceIdentityTests
{
    [Fact]
    public void FromMac_formats_factory_identity()
    {
        var id = DeviceIdentity.FromMac([0xA0, 0xB1, 0xC2, 0xD3, 0xE4, 0xF5]);
        Assert.Equal("mac-a0b1c2d3e4f5", id);
        Assert.True(DeviceIdentity.IsValid(id));
    }

    [Fact]
    public void NewProvisioned_is_valid_and_unique()
    {
        var a = DeviceIdentity.NewProvisioned();
        var b = DeviceIdentity.NewProvisioned();
        Assert.True(DeviceIdentity.IsValid(a));
        Assert.True(DeviceIdentity.IsValid(b));
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kitchen")]
    [InlineData("mac-A0B1C2D3E4F5")]
    [InlineData("mac-a0b1c2")]
    [InlineData("oal-not-a-uuid")]
    public void Invalid_identities_are_rejected(string? id)
    {
        Assert.False(DeviceIdentity.IsValid(id));
    }
}
