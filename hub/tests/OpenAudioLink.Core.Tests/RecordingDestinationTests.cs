using OpenAudioLink.Hub.Services;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// The three lines that were wrong twice in a row, now reachable.
/// </summary>
public class RecordingDestinationTests
{
    private const string Hub = "192.168.0.201";

    private static string? Resolve(string id) => id switch
    {
        "mac-1cdbd4447900" => "192.168.0.71",
        "mac-aabbccddeeff" => "192.168.0.72",
        _ => null,
    };

    /// <summary>
    /// The first failure: the To list carries device ids, and a node cannot
    /// dial one. It refused the whole request and said only "refused".
    /// </summary>
    [Fact]
    public void Device_ids_become_addresses()
    {
        var result = RecordingService.BuildDestinations(
            ["mac-1cdbd4447900"], Hub, Resolve);

        Assert.Equal(["192.168.0.71", Hub], result);
    }

    /// <summary>
    /// The second failure: the producer parses each entry with inet_addr
    /// and takes one port for the whole list from a separate field, so a
    /// port on an address makes it unparseable rather than more precise.
    /// </summary>
    [Fact]
    public void No_destination_carries_a_port()
    {
        var result = RecordingService.BuildDestinations(
            ["192.168.0.71:41100"], Hub, Resolve);

        Assert.Equal(["192.168.0.71", Hub], result);
        Assert.All(result, address => Assert.DoesNotContain(':', address));
    }

    [Fact]
    public void The_hub_is_always_a_destination_and_always_last()
    {
        var result = RecordingService.BuildDestinations(
            ["mac-1cdbd4447900", "mac-aabbccddeeff"], Hub, Resolve);

        Assert.Equal(["192.168.0.71", "192.168.0.72", Hub], result);
    }

    [Fact]
    public void Recording_with_no_speaker_still_reaches_the_hub()
    {
        Assert.Equal([Hub], RecordingService.BuildDestinations([], Hub, Resolve));
    }

    /// <summary>
    /// An address typed by hand is not a device id and must survive.
    /// </summary>
    [Fact]
    public void An_unknown_entry_is_passed_through_as_typed()
    {
        var result = RecordingService.BuildDestinations(
            ["192.168.0.99"], Hub, Resolve);

        Assert.Equal(["192.168.0.99", Hub], result);
    }

    [Fact]
    public void Blank_entries_are_ignored()
    {
        var result = RecordingService.BuildDestinations(
            ["", "  ", "mac-1cdbd4447900"], Hub, Resolve);

        Assert.Equal(["192.168.0.71", Hub], result);
    }

    /// <summary>
    /// A node's destination set is small, and naming one twice wastes a
    /// slot — and would send it two copies of every packet.
    /// </summary>
    [Fact]
    public void A_speaker_named_twice_appears_once()
    {
        var result = RecordingService.BuildDestinations(
            ["mac-1cdbd4447900", "192.168.0.71"], Hub, Resolve);

        Assert.Equal(["192.168.0.71", Hub], result);
    }

    [Fact]
    public void Naming_the_hub_explicitly_does_not_duplicate_it()
    {
        var result = RecordingService.BuildDestinations(
            [Hub], Hub, Resolve);

        Assert.Equal([Hub], result);
    }
}
