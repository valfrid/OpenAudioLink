using OpenAudioLink.Core.CastPoints;
using OpenAudioLink.Hub.Services;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class CastPointIdTests
{
    [Theory]
    [InlineData("Kitchen", "kitchen")]
    [InlineData("Living room", "living-room")]
    [InlineData("  Whole   House  ", "whole-house")]
    [InlineData("Sovrum 2", "sovrum-2")]
    public void DerivesASlugFromTheName(string name, string expected) =>
        Assert.Equal(expected, CastPointId.FromName(name));

    /// <summary>
    /// Scandinavian names are the normal case here, not an edge one, and a
    /// decomposing normalise folds them without a translation table.
    /// </summary>
    [Theory]
    [InlineData("Kök", "kok")]
    [InlineData("Vardagsrum", "vardagsrum")]
    [InlineData("Övervåningen", "overvaningen")]
    public void FoldsAccentedLetters(string name, string expected) =>
        Assert.Equal(expected, CastPointId.FromName(name));

    /// <summary>
    /// A name with nothing to build an id from is different from no name:
    /// the operator typed something, and the message should say why it
    /// cannot be used rather than claim the field was empty.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("🎵")]
    [InlineData("---")]
    public void ReturnsNullWhenNothingUsableRemains(string name) =>
        Assert.Null(CastPointId.FromName(name));

    [Fact]
    public void SuffixesUntilFree()
    {
        var used = new HashSet<string> { "bedroom", "bedroom-2" };
        Assert.Equal("bedroom-3", CastPointId.MakeUnique("bedroom", used.Contains));
    }
}

public class CastPointStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "oal-castpoints-" + Guid.NewGuid().ToString("N"));

    private CastPointStore NewStore() => new(_directory);

    [Fact]
    public void CreatesWithASlugAndKeepsTheDisplayName()
    {
        var store = NewStore();
        Assert.Equal(CastPointError.None, store.Create("Living room", ["a", "b"], out var created));

        Assert.Equal("living-room", created.Id);
        Assert.Equal("Living room", created.Name);
        Assert.Equal(["a", "b"], created.Destinations);
    }

    /// <summary>
    /// This asserted the opposite until a real cast point was set up and the
    /// consequence turned up in Spotify: two entries called "Party", no way
    /// to tell which was which, and separate credentials so one was signed
    /// in and the other was not. Unique ids do not help — the name is the
    /// only thing anyone sees in a device picker.
    /// </summary>
    [Fact]
    public void TwoRoomsMayNotShareAName()
    {
        var store = NewStore();
        Assert.Equal(CastPointError.None, store.Create("Bedroom", [], out _));
        Assert.Equal(CastPointError.NameTaken, store.Create("Bedroom", [], out _));

        // Case and surrounding space do not make it a different room, because
        // they do not make it look like one.
        Assert.Equal(CastPointError.NameTaken, store.Create(" bedroom ", [], out _));
    }

    /// <summary>
    /// Ids still have to be disambiguated, which is easy to assume this
    /// change made unnecessary. Distinct names can share a slug: punctuation
    /// and accents are folded away, so "Kök" and "Kok" are two rooms a person
    /// can tell apart and one id cannot hold both.
    /// </summary>
    [Fact]
    public void DifferentNamesThatSlugTheSameGetDistinctIds()
    {
        var store = NewStore();
        Assert.Equal(CastPointError.None, store.Create("Kök", [], out var first));
        Assert.Equal(CastPointError.None, store.Create("Kok", [], out var second));

        Assert.Equal("kok", first.Id);
        Assert.Equal("kok-2", second.Id);
    }

    [Fact]
    public void RenamingOntoAnotherRoomsNameIsRefused()
    {
        var store = NewStore();
        store.Create("Kitchen", [], out var kitchen);
        store.Create("Bedroom", [], out var bedroom);

        Assert.Equal(CastPointError.NameTaken, store.Update(bedroom.Id, "Kitchen", []));

        // Renaming a room to what it is already called is not a clash.
        Assert.Equal(CastPointError.None, store.Update(kitchen.Id, "Kitchen", ["a"]));
    }

    [Fact]
    public void RejectsANameWithNothingToBuildAnIdFrom()
    {
        var store = NewStore();
        Assert.Equal(CastPointError.NameRequired, store.Create("  ", [], out _));
        Assert.Equal(CastPointError.NameUnusable, store.Create("🎵", [], out _));
    }

    /// <summary>
    /// A room is often named before its speaker is unpacked, so an empty
    /// cast point has to be creatable. It is starting one that needs
    /// destinations.
    /// </summary>
    [Fact]
    public void AllowsACastPointWithNoSpeakersYet()
    {
        var store = NewStore();
        Assert.Equal(CastPointError.None, store.Create("Garage", [], out var created));
        Assert.Empty(created.Destinations);
    }

    [Fact]
    public void DropsDuplicateDestinations()
    {
        var store = NewStore();
        store.Create("Kitchen", ["a", "a", "b"], out var created);
        Assert.Equal(["a", "b"], created.Destinations);
    }

    /// <summary>
    /// The id is what persistence and the API refer to, so renaming must
    /// not mint a new one and orphan the room being renamed.
    /// </summary>
    [Fact]
    public void RenamingKeepsTheId()
    {
        var store = NewStore();
        store.Create("Kitchen", ["a"], out var created);

        Assert.Equal(CastPointError.None, store.Update(created.Id, "Kök", ["a", "c"]));

        Assert.True(store.TryGet("kitchen", out var updated));
        Assert.Equal("Kök", updated.Name);
        Assert.Equal(["a", "c"], updated.Destinations);
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public void SurvivesARestart()
    {
        var store = NewStore();
        store.Create("Kitchen", ["a"], out _);
        store.Create("House", ["a", "b"], out _);

        var reopened = NewStore();
        Assert.Equal(["Kitchen", "House"], reopened.Snapshot().Select(p => p.Name));
    }

    /// <summary>
    /// Nothing plays after a restart, and saying otherwise would show the
    /// operator a stream that does not exist.
    /// </summary>
    [Fact]
    public void PlaybackDoesNotSurviveARestart()
    {
        var store = NewStore();
        store.Create("Kitchen", ["a"], out var kitchen);
        store.MarkPlaying(kitchen.Id, "producer-1");
        Assert.NotNull(store.Playing);

        Assert.Null(NewStore().Playing);
    }

    [Fact]
    public void DeletingAPlayingCastPointClearsPlayback()
    {
        var store = NewStore();
        store.Create("Kitchen", ["a"], out var kitchen);
        store.MarkPlaying(kitchen.Id, "producer-1");

        Assert.True(store.Delete(kitchen.Id));
        Assert.Null(store.Playing);
        Assert.Empty(store.Snapshot());
    }

    /// <summary>
    /// One speaker cannot play two streams, so a cast point that shares a
    /// consumer with the playing one has to replace it.
    /// </summary>
    [Fact]
    public void OverlappingCastPointsConflict()
    {
        var store = NewStore();
        store.Create("Kitchen", ["a"], out var kitchen);
        store.Create("House", ["a", "b"], out var house);
        store.MarkPlaying(kitchen.Id, "producer-1");

        var conflict = store.ConflictWith(house.Id);
        Assert.NotNull(conflict);
        Assert.Equal(kitchen.Id, conflict!.CastPointId);
    }

    /// <summary>
    /// Disjoint cast points conflict too, because the store remembers one
    /// playing pair.
    /// </summary>
    /// <remarks>
    /// This used to assert the opposite, on the reasoning that two rooms
    /// sharing no speaker could both play. They cannot: starting the second
    /// left the first one streaming and overwrote the only record that it
    /// existed, so nothing displayed it and Stop could not reach it. The
    /// stream ran until somebody rebooted the node.
    ///
    /// Concurrent pairs are a real feature and a bigger change than this —
    /// the single playing field becomes a collection and every reader learns
    /// to ask which one. Until that happens, reporting the conflict means
    /// the old stream is stopped rather than abandoned.
    /// </remarks>
    [Fact]
    public void A_second_room_replaces_the_first_even_with_no_speaker_in_common()
    {
        var store = NewStore();
        store.Create("Kitchen", ["a"], out var kitchen);
        store.Create("Garage", ["b"], out var garage);
        store.MarkPlaying(kitchen.Id, "producer-1");

        var conflict = store.ConflictWith(garage.Id);

        Assert.NotNull(conflict);
        Assert.Equal(kitchen.Id, conflict!.CastPointId);
    }

    /// <summary>
    /// Pressing play on what is already playing is a restart, not a
    /// conflict to refuse — but the running stream still has to be stopped
    /// first, so it must be reported.
    /// </summary>
    [Fact]
    public void RestartingTheSameCastPointReportsItself()
    {
        var store = NewStore();
        store.Create("Kitchen", ["a"], out var kitchen);
        store.MarkPlaying(kitchen.Id, "producer-1");

        var conflict = store.ConflictWith(kitchen.Id);
        Assert.NotNull(conflict);
        Assert.Equal(kitchen.Id, conflict!.CastPointId);
    }

    [Fact]
    public void NothingConflictsWhenNothingIsPlaying()
    {
        var store = NewStore();
        store.Create("Kitchen", ["a"], out var kitchen);
        Assert.Null(store.ConflictWith(kitchen.Id));
    }

    /// <summary>
    /// A corrupt file should cost the rooms it holds, not the whole Hub —
    /// an operator locked out of the interface cannot fix anything.
    /// </summary>
    [Fact]
    public void StartsCleanWhenTheFileIsUnreadable()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "castpoints.json"), "{ not json");

        Assert.Empty(NewStore().Snapshot());
    }

    [Fact]
    public void UpdatingSomethingThatIsNotThereIsNotFound() =>
        Assert.Equal(CastPointError.NotFound, NewStore().Update("nope", "Nope", []));

    [Fact]
    public void DeletingSomethingThatIsNotThereIsFalse() =>
        Assert.False(NewStore().Delete("nope"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        GC.SuppressFinalize(this);
    }
}
