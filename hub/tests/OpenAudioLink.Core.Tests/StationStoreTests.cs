using OpenAudioLink.Hub.Services;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class StationStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "oal-stations-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private StationStore Open() => new(_directory);

    /// <summary>
    /// A switchboard with nothing on it teaches nobody what it is for, so a
    /// fresh Hub arrives with a few stations already saved.
    /// </summary>
    [Fact]
    public void A_fresh_hub_starts_with_stations()
    {
        var stations = Open().Snapshot();

        Assert.NotEmpty(stations);
        Assert.All(stations, s => Assert.StartsWith("http", s.Url));
    }

    /// <summary>
    /// The seed is a starting point, not a policy. Re-applying it would put
    /// a deleted station back on every restart, which is worse than starting
    /// with none at all.
    /// </summary>
    [Fact]
    public void A_deleted_station_stays_deleted_across_a_restart()
    {
        var first = Open();
        var victim = first.Snapshot()[0].Id;
        Assert.True(first.Delete(victim));

        var reopened = Open();
        Assert.DoesNotContain(reopened.Snapshot(), s => s.Id == victim);
        Assert.NotEmpty(reopened.Snapshot());
    }

    [Fact]
    public void A_station_survives_a_restart()
    {
        Assert.Equal(
            StationError.None,
            Open().Create("Radio Nowhere", "https://ice.example/stream", out var created));

        Assert.Contains(Open().Snapshot(), s => s.Id == created.Id && s.Name == "Radio Nowhere");
    }

    [Theory]
    [InlineData("file:///home/someone/music.mp3")]
    [InlineData("/relative/path.mp3")]
    [InlineData("ice.example/stream")]
    [InlineData("not a url at all")]
    public void Anything_that_is_not_an_http_address_is_refused(string url) =>
        Assert.Equal(StationError.UrlUnusable, Open().Create("Somewhere", url, out _));

    [Fact]
    public void A_station_with_no_name_is_named_after_its_host()
    {
        Assert.Equal(
            StationError.None,
            Open().Create(null, "https://ice1.somafm.com/groovesalad-256-mp3", out var created));

        Assert.Equal("ice1.somafm.com", created.Name);
        Assert.Equal("ice1-somafm-com", created.Id);
    }

    /// <summary>
    /// The same clash a cast point has, for the same reason: two identical
    /// buttons on a switchboard cannot be told apart by the person pressing
    /// one.
    /// </summary>
    [Fact]
    public void Two_stations_may_not_share_a_name()
    {
        var store = Open();
        Assert.Equal(StationError.None, store.Create("P3", "https://a.example/s", out _));
        Assert.Equal(StationError.NameTaken, store.Create("  p3 ", "https://b.example/s", out _));
    }

    /// <summary>
    /// Nothing stops two names slugging to the same id, and the id is what
    /// the API refers to — so the second gets a suffix rather than
    /// overwriting the first.
    /// </summary>
    [Fact]
    public void Names_that_slug_alike_get_separate_ids()
    {
        var store = Open();
        Assert.Equal(StationError.None, store.Create("Kanal P3", "https://a.example/s", out var first));
        Assert.Equal(StationError.None, store.Create("Kanal-P3", "https://b.example/s", out var second));

        Assert.Equal("kanal-p3", first.Id);
        Assert.Equal("kanal-p3-2", second.Id);
    }

    [Fact]
    public void Renaming_keeps_the_id_it_was_created_with()
    {
        var store = Open();
        store.Create("Groove", "https://a.example/s", out var created);

        Assert.Equal(StationError.None, store.Update(created.Id, "Groove Salad", "https://b.example/s"));
        Assert.True(store.TryGet(created.Id, out var updated));
        Assert.Equal("Groove Salad", updated.Name);
        Assert.Equal("https://b.example/s", updated.Url);
    }

    [Fact]
    public void Renaming_a_station_to_what_it_is_already_called_is_not_a_clash()
    {
        var store = Open();
        store.Create("Groove", "https://a.example/s", out var created);

        Assert.Equal(StationError.None, store.Update(created.Id, "Groove", "https://a.example/other"));
    }

    [Fact]
    public void An_unknown_id_is_not_found() =>
        Assert.Equal(StationError.NotFound, Open().Update("nothing", "Name", "https://a.example/s"));

    /// <summary>
    /// A file somebody's stations are in must not be silently replaced by
    /// the four defaults: that would look like the list was lost rather than
    /// like the file needs looking at.
    /// </summary>
    [Fact]
    public void A_corrupt_file_costs_the_list_but_not_the_hub()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "stations.json"), "{ this is not json");

        Assert.Empty(Open().Snapshot());
    }

    [Fact]
    public void Entries_missing_a_url_are_dropped_rather_than_kept()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "stations.json"), """
            [
              { "Id": "good", "Name": "Good", "Url": "https://a.example/s" },
              { "Id": "bad", "Name": "Bad", "Url": "" }
            ]
            """);

        var stations = Open().Snapshot();
        Assert.Single(stations);
        Assert.Equal("good", stations[0].Id);
    }
}
