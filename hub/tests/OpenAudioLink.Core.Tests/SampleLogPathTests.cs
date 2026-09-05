using OpenAudioLink.Hub.Services;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// Which file a day's rows go into.
/// </summary>
/// <remarks>
/// A day's file used to be opened once and appended to for the rest of the
/// day, with the header written only when the file did not yet exist. So
/// upgrading the Hub mid-day appended rows in the new shape under the old
/// header, and every column past the insertion point silently meant
/// something else.
///
/// That is not hypothetical: the log of 2026-09-05 has 224 rows of 62 fields
/// and 6 of 65 under a 62-field header, and <c>rssi</c> reads +13 in the new
/// ones because the field there is actually <c>phaseErrorMs</c>. A log that
/// misreads without complaining is worse than a missing one — the numbers
/// still look like numbers.
/// </remarks>
public class SampleLogPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "oal-samplelog-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Day = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private const string Header = "timeUtc,node,phaseErrorMs";

    public SampleLogPathTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    [Fact]
    public void The_days_file_is_used_when_nothing_exists_yet()
    {
        Assert.Equal(
            Path_("oal-2026-09-05.csv"),
            SampleLogService.NextFreePath(_dir, Day, Header));
    }

    [Fact]
    public void A_file_with_the_same_header_is_appended_to()
    {
        File.WriteAllText(Path_("oal-2026-09-05.csv"), Header + "\nrow\n");

        Assert.Equal(
            Path_("oal-2026-09-05.csv"),
            SampleLogService.NextFreePath(_dir, Day, Header));
    }

    /// <summary>The failure this exists for.</summary>
    [Fact]
    public void A_file_from_a_build_with_other_columns_is_left_alone()
    {
        File.WriteAllText(Path_("oal-2026-09-05.csv"), "timeUtc,node,rssi\nrow\n");

        Assert.Equal(
            Path_("oal-2026-09-05-2.csv"),
            SampleLogService.NextFreePath(_dir, Day, Header));
    }

    [Fact]
    public void Upgrading_twice_in_one_day_keeps_walking_along()
    {
        File.WriteAllText(Path_("oal-2026-09-05.csv"), "one\n");
        File.WriteAllText(Path_("oal-2026-09-05-2.csv"), "two\n");

        Assert.Equal(
            Path_("oal-2026-09-05-3.csv"),
            SampleLogService.NextFreePath(_dir, Day, Header));
    }

    /// <summary>
    /// And back into the matching one, so a Hub restarted on the same build
    /// does not start a new file every time.
    /// </summary>
    [Fact]
    public void A_later_file_with_the_right_header_is_reused()
    {
        File.WriteAllText(Path_("oal-2026-09-05.csv"), "timeUtc,node,rssi\n");
        File.WriteAllText(Path_("oal-2026-09-05-2.csv"), Header + "\nrow\n");

        Assert.Equal(
            Path_("oal-2026-09-05-2.csv"),
            SampleLogService.NextFreePath(_dir, Day, Header));
    }

    /// <summary>
    /// An empty file is one that was created and not yet written; the header
    /// still has to go in, so it is not a mismatch.
    /// </summary>
    [Fact]
    public void An_empty_file_is_not_a_mismatch()
    {
        File.WriteAllText(Path_("oal-2026-09-05.csv"), "");

        Assert.Equal(
            Path_("oal-2026-09-05.csv"),
            SampleLogService.NextFreePath(_dir, Day, Header));
    }

    /// <summary>
    /// Losing the log is a nuisance; spinning forever creating files is a
    /// fault. It gives up and appends rather than running away.
    /// </summary>
    [Fact]
    public void It_stops_walking_rather_than_filling_the_disk()
    {
        for (int i = 1; i <= 60; i++)
        {
            var name = i == 1 ? "oal-2026-09-05.csv" : $"oal-2026-09-05-{i}.csv";
            File.WriteAllText(Path_(name), "someone,elses,header\n");
        }

        Assert.Equal(
            Path_("oal-2026-09-05-50.csv"),
            SampleLogService.NextFreePath(_dir, Day, Header));
    }
}
