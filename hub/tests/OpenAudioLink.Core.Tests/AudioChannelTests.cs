using OpenAudioLink.Core.Devices;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class AudioChannelTests
{
    /// <summary>
    /// These strings cross to the firmware verbatim, in /status and
    /// /config. A rename on either side that misses the other produces a
    /// node the Hub can read but not configure.
    /// </summary>
    [Theory]
    [InlineData("stereo")]
    [InlineData("mono")]
    [InlineData("left")]
    [InlineData("right")]
    public void The_four_profiles_are_known(string channel) =>
        Assert.True(AudioChannels.IsKnown(channel));

    [Theory]
    [InlineData("Stereo")]   // the firmware's parse is case sensitive
    [InlineData("l")]
    [InlineData("both")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? channel) =>
        Assert.False(AudioChannels.IsKnown(channel));

    /// <summary>
    /// Whole rooms before halves of a pair: the halves are the rarer answer
    /// and the one that costs a synchronisation problem.
    /// </summary>
    [Fact]
    public void The_list_reads_in_the_order_a_person_chooses() =>
        Assert.Equal(["stereo", "mono", "left", "right"], AudioChannels.All);

    [Fact]
    public void Only_left_and_right_are_half_of_a_pair()
    {
        Assert.True(AudioChannels.IsHalfOfAPair(AudioChannel.Left));
        Assert.True(AudioChannels.IsHalfOfAPair(AudioChannel.Right));
        Assert.False(AudioChannels.IsHalfOfAPair(AudioChannel.Stereo));
        Assert.False(AudioChannels.IsHalfOfAPair(AudioChannel.Mono));
        Assert.False(AudioChannels.IsHalfOfAPair(null));
    }
}
