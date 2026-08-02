using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAudioLink.Core.Audio;
using OpenAudioLink.Hub.Services;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// The destination list of a running stream. This is what lets a speaker
/// that rebooted mid-song rejoin without the rest of the room skipping, and
/// what keeps a playing cast point in step with its membership — the same
/// mutable list the firmware producer already carries.
///
/// Everything here goes to loopback, so no packet leaves the machine.
/// </summary>
public class RtpStreamerDestinationTests
{
    private const int Port = 41199;

    private static readonly IPAddress First = IPAddress.Parse("127.0.0.1");
    private static readonly IPAddress Second = IPAddress.Parse("127.0.0.2");
    private static readonly IPAddress Third = IPAddress.Parse("127.0.0.3");

    private static RtpStreamer NewStreamer() => new(NullLogger<RtpStreamer>.Instance);

    private static async Task StartAsync(RtpStreamer streamer, params IPAddress[] to)
    {
        var format = new AudioStreamFormat();
        await streamer.StartAsync("test-tone", new SineToneSource(format, 1000.0), to, Port, format);
    }

    [Fact]
    public async Task Reports_where_a_stream_is_going()
    {
        await using var streamer = NewStreamer();
        await StartAsync(streamer, First);

        Assert.Equal(new[] { "127.0.0.1" }, streamer.Status.Destinations);
    }

    /// <summary>
    /// A node that never left still asks to rejoin every thirty seconds, so
    /// adding one already there has to cost nothing — otherwise the list
    /// grows a duplicate per refresh and the speaker gets each packet twice.
    /// </summary>
    [Fact]
    public async Task Adding_the_same_destination_twice_changes_nothing()
    {
        await using var streamer = NewStreamer();
        await StartAsync(streamer, First);

        Assert.True(streamer.AddDestination(First));
        Assert.True(streamer.AddDestination(First));

        Assert.Equal(new[] { "127.0.0.1" }, streamer.Status.Destinations);
    }

    [Fact]
    public async Task A_new_destination_joins_the_running_stream()
    {
        await using var streamer = NewStreamer();
        await StartAsync(streamer, First);

        Assert.True(streamer.AddDestination(Second));

        Assert.Equal(new[] { "127.0.0.1", "127.0.0.2" }, streamer.Status.Destinations);
    }

    [Fact]
    public async Task The_whole_list_can_be_replaced()
    {
        await using var streamer = NewStreamer();
        await StartAsync(streamer, First, Second);

        Assert.True(streamer.SetDestinations([Third]));

        Assert.Equal(new[] { "127.0.0.3" }, streamer.Status.Destinations);
    }

    /// <summary>
    /// Nothing to change when nothing is running, and saying so is how the
    /// join handler knows a Hub-fed cast point is not the one playing.
    /// </summary>
    [Fact]
    public async Task Changing_destinations_of_a_stopped_stream_is_refused()
    {
        await using var streamer = NewStreamer();

        Assert.False(streamer.AddDestination(First));
        Assert.False(streamer.SetDestinations([First]));
    }

    [Fact]
    public async Task A_stream_to_nowhere_is_not_a_stream()
    {
        await using var streamer = NewStreamer();
        await StartAsync(streamer, First);

        Assert.Throws<ArgumentException>(() => streamer.SetDestinations([]));
    }
}
