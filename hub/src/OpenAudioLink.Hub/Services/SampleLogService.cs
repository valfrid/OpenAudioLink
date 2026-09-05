using System.Globalization;
using System.Text;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Hub.Configuration;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// Writes one CSV row per node per interval, so a run can be read as a
/// series rather than as a photograph of one instant.
/// </summary>
/// <remarks>
/// <para>
/// Every diagnostic mistake this project has made was a <b>saturated
/// lifetime counter</b>. <c>WorstStallMs</c> reading 145 for ever after one
/// bad moment. The node's <c>maxArrivalGapTicks</c> carrying 4 445 ms
/// across stream restarts, so a clean night still reported a four-second
/// gap. <c>framesPlayed</c> divided by an uptime that included the hours
/// the node was idle. Each time the fix was to add a per-window twin of
/// the counter, and each time the next lifetime counter caught somebody
/// out.
/// </para>
/// <para>
/// A series makes all of them per-interval for free — subtract
/// consecutive rows — so the class of bug stops existing rather than being
/// fixed one counter at a time.
/// </para>
/// <para>
/// It also makes this project's own rule enforceable across time. "Did a
/// counter move when the offset jumped?" is the check that settles every
/// argument here, and it cannot be answered from a screenshot: the jump
/// was thirty seconds ago and the panel shows now. With rows a caller can
/// line a step-back up against a Wi-Fi drop, a garbage collection and a
/// send stall, and say which came first.
/// </para>
/// <para>
/// <b>Nothing here asks a node for anything.</b> It reads what
/// <see cref="NodeClockService"/> already fetched and what
/// <see cref="DeviceStatusService"/> already polled. Adding a second
/// poller to a device with seven sockets is the mistake that produced the
/// HTTP 502s, and it is not worth repeating for a log file.
/// </para>
/// </remarks>
public sealed class SampleLogService : BackgroundService
{
    /// <summary>
    /// Matches <see cref="NodeClockService.PollInterval"/>, because that is
    /// how often the underlying readings change. Logging faster would write
    /// the same numbers twice and call it data.
    /// </summary>
    public static readonly TimeSpan Interval = NodeClockService.PollInterval;

    /// <summary>
    /// Days of history to keep. A night is about 1 400 rows per node and
    /// well under a megabyte, so this is generous; it exists so an
    /// unattended Hub cannot fill a disk over a year.
    /// </summary>
    public const int KeepDays = 14;

    private readonly DeviceRegistry _registry;
    private readonly NodeClockService _clocks;
    private readonly RtpStreamer _streamer;
    private readonly ILogger<SampleLogService> _logger;
    private readonly string _directory;

    private string? _openPath;
    private bool _warnedAboutWidth;

    public SampleLogService(
        DeviceRegistry registry, NodeClockService clocks, RtpStreamer streamer,
        HubPaths paths, ILogger<SampleLogService> logger)
    {
        _registry = registry;
        _clocks = clocks;
        _streamer = streamer;
        _logger = logger;
        _directory = Path.Combine(paths.DataDirectory, "samples");

        /*
         * Here, not in ExecuteAsync, and that distinction stopped the Hub
         * starting once.
         *
         * The directory is handed to a PhysicalFileProvider while the
         * request pipeline is being built, and that constructor throws if
         * its root does not exist. Hosted services start *after* the
         * pipeline is configured, so creating it in ExecuteAsync was
         * always too late: 0.71.0 shipped and the Hub would not come up.
         * FirmwareStore has created its directory in its constructor since
         * it was written, which is why /firmware never had this problem --
         * the registration pattern was copied without the guarantee that
         * made it safe.
         *
         * Swallowed rather than thrown, because this is diagnostics. A
         * sample log that cannot be written is a nuisance; a sample log
         * that stops the music is a fault. Program.cs checks the directory
         * exists before it registers the route, so a failure here costs
         * the log and nothing else.
         */
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex, "Could not create the sample log directory {Path}; "
                + "diagnostics logging is off for this run", _directory);
        }
    }

    /// <summary>Where the files land, for whoever has to go and fetch one.</summary>
    public string DirectoryPath => _directory;

    /*
     * One row is self-contained, including the Hub's own counters repeated
     * on every node's row.
     *
     * Redundant, and deliberately: it means any single row can be read
     * without joining it to another, and a reader filtering to one node
     * still sees what the sender was doing at that moment. The redundancy
     * costs bytes in a file that is already small.
     *
     * A row is stamped with the node reading's own time, but it also carries
     * columns from two other sources read at two other instants: the radio
     * fields from DeviceStatusService's 10-second poll, and the hub fields
     * read live as the row is written. The ages below say how far apart
     * those instants were, and they are not decoration.
     *
     * The first analysis run off this log compared the node's `received`
     * against `hubPacketsSent` and concluded the nodes were missing 0.25% of
     * the stream -- 3 880 packets over two hours, on both nodes, with the
     * nodes' own loss counters reading zero. That was arithmetic across a
     * seam: the node reading at the end of the run happened to be 19 seconds
     * staler than the one at the start, and 19 seconds at 200 packets per
     * second is exactly 3 880 packets. The audio was fine; the subtraction
     * was not.
     *
     * With an age on the row that check becomes possible to get right --
     * subtract the ages before comparing rates, or throw out rows where they
     * differ. Without it the seam is invisible and the reader invents a
     * fault. This is the same failure as a saturated lifetime counter: the
     * number is true and means something other than what it looks like.
     */
    private static readonly string[] Columns =
    [
        "timeUtc", "node", "nodeId", "rttMs", "readingAgeS", "statusAgeS",
        // Playout: where the ring sat and what moved it.
        "playing", "bufferedMs", "targetMs", "steerMs", "primedMs",
        "fillMinMs", "fillMaxMs", "framesPlayed",
        "trims", "pads", "resyncs", "reprimes", "underruns", "droppedMs", "silenceMs",
        "latePackets", "tightPackets", "writeErrors",
        // The playing position, so an offset between two nodes can be
        // computed afterwards rather than trusted from a live panel.
        "playingTimestamp", "playingKnown",
        // How far from where it should be, in milliseconds, and positive is
        // late. Beside the depth on purpose: on 2026-09-04 the two agreed at
        // r = 0.949, which is what said the echo *was* the depth difference —
        // but the depth also swings with every burst, and this does not. Two
        // columns are how the next evening decides whether the loop can move
        // onto the quieter one.
        "phaseErrorMs", "phaseKnown", "timelineBreaks",
        // The link.
        "received", "expected", "lost", "jitterMs", "lossEvents", "longestGap",
        "arrivalGaps", "maxArrivalGapMs", "duplicates", "reordered", "ssrcChanges",
        // Gaps by length rather than the worst there has ever been:
        // <20 ms, 20-50, 50-100, 100-200, >200. Monotonic, so consecutive
        // rows subtract to the interval's own distribution.
        "gapsTo20", "gapsTo50", "gapsTo100", "gapsTo200", "gapsOver200",
        // The radio, from the status poll.
        "rssi", "channel", "bssid", "disconnects", "lastReason", "roams", "uptimeS",
        // The crystal, from the fit.
        "clockPpm", "clockSigmaPpm", "clockSpanS", "clockSuspect",
        // The sender, identical on every row of one instant.
        "hubPacketsSent", "hubUnderrunSamples", "hubLateWakes",
        "hubRecentStallMs", "hubRecentSendMs", "hubRecentGcPauseMs",
        "hubPeakDbfs", "hubBufferedMs", "hubSource",
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Prune();

        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    WriteRows();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A log that cannot be written must not take the Hub
                    // with it: this is diagnostics, not the audio path.
                    _logger.LogDebug(ex, "Could not write the sample log");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void WriteRows()
    {
        var readings = _clocks.Readings();
        if (readings.Count == 0)
        {
            // Nothing playing. A row of zeroes would be a lie about a
            // system that is simply idle.
            return;
        }

        var fits = _clocks.Snapshot();
        var devices = _registry.Snapshot().ToDictionary(d => d.Id);
        var hub = _streamer.Status;
        var now = DateTimeOffset.UtcNow;

        var path = Path.Combine(
            _directory, $"oal-{now:yyyy-MM-dd}.csv");
        var isNew = !File.Exists(path);

        var text = new StringBuilder();
        if (isNew)
        {
            text.AppendLine(string.Join(',', Columns));
            if (_openPath != path)
            {
                Prune();
            }
        }
        _openPath = path;

        foreach (var (id, r) in readings)
        {
            devices.TryGetValue(id, out var device);
            var s = device?.Status;
            fits.TryGetValue(id, out var fit);

            var row = new List<string>(Columns.Length)
            {
                r.At.ToString("o", CultureInfo.InvariantCulture),
                Csv(device?.Name ?? id), id, N(r.RttMs, 1),
                N((now - r.At).TotalSeconds, 1),
                s is null ? "" : N((now - s.ObservedAt).TotalSeconds, 1),
                r.Playing ? "1" : "0",
                Ms(r.BufferedFrames), Ms(r.TargetFrames), Ms(r.SteerFrames),
                Ms(r.PrimedFrames), Ms(r.FillMinFrames), Ms(r.FillMaxFrames),
                r.FramesPlayed.ToString(CultureInfo.InvariantCulture),
                L(r.TrimmedFrames), L(r.PaddedFrames), L(r.Resyncs), L(r.Reprimes),
                L(r.Underruns),
                Ms(r.DroppedFrames), Ms(r.SilenceFrames),
                L(r.LatePackets), L(r.TightPackets), L(r.WriteErrors),
                L(r.PlayingTimestamp), r.PlayingKnown ? "1" : "0",
                N(r.PhaseErrorFrames / 48.0, 1), r.PhaseKnown ? "1" : "0",
                L(r.TimelineBreaks),
                L(r.Received), L(r.Expected), L(r.Lost),
                // Jitter and gaps arrive in RTP ticks; milliseconds are
                // what a buffer is sized in and what a reader thinks in.
                N(r.JitterTicks / 48.0, 2),
                L(r.LossEvents), L(r.LongestGap), L(r.ArrivalGaps),
                N(r.MaxArrivalGapTicks / 48.0, 0),
                L(r.Duplicates), L(r.Reordered), L(r.SsrcChanges),
                Bucket(r.GapBuckets, 0), Bucket(r.GapBuckets, 1), Bucket(r.GapBuckets, 2),
                Bucket(r.GapBuckets, 3), Bucket(r.GapBuckets, 4),
                s?.Rssi?.ToString(CultureInfo.InvariantCulture) ?? "",
                s?.Channel?.ToString(CultureInfo.InvariantCulture) ?? "",
                Csv(s?.Bssid ?? ""),
                s?.Disconnects?.ToString(CultureInfo.InvariantCulture) ?? "",
                s?.LastReason?.ToString(CultureInfo.InvariantCulture) ?? "",
                s?.Roams?.ToString(CultureInfo.InvariantCulture) ?? "",
                s?.UptimeSeconds.ToString(CultureInfo.InvariantCulture) ?? "",
                fit is null ? "" : N(fit.Ppm, 1),
                fit is null ? "" : N(fit.SigmaPpm, 1),
                fit is null ? "" : L(fit.SpanSeconds),
                fit is null ? "" : (fit.Suspect ? "1" : "0"),
                L(hub.PacketsSent), L(hub.UnderrunSamples), L(hub.LateWakes),
                L(hub.RecentStallMs), L(hub.RecentSendMs), L(hub.RecentGcPauseMs),
                hub.PeakDbfs is { } peak ? N(peak, 1) : "",
                L(hub.BufferedMs), Csv(hub.Source ?? ""),
            };
            /*
             * A row that does not match the header is worse than no row:
             * every column after the gap is silently attributed to the
             * wrong name, and a reader has no way to notice. Checked here
             * rather than trusted, because the two lists are edited
             * separately and a header and its values drifting apart is
             * exactly the shape of mistake this project keeps making --
             * the same one as a format string and its arguments.
             *
             * Logged and skipped rather than thrown: this is diagnostics,
             * and it must not be able to stop the Hub.
             */
            if (row.Count != Columns.Length)
            {
                if (!_warnedAboutWidth)
                {
                    _warnedAboutWidth = true;
                    _logger.LogError(
                        "Sample log row has {Actual} values against {Expected} columns; "
                        + "not writing. The header and the row are out of step.",
                        row.Count, Columns.Length);
                }
                return;
            }

            text.AppendLine(string.Join(',', row));
        }

        File.AppendAllText(path, text.ToString());
    }

    private static string L(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One histogram bucket, empty when a node too old to report them left
    /// the array short. Blank rather than zero, because a zero here would
    /// claim the node saw no gaps of that length when in fact it never
    /// said.
    /// </summary>
    private static string Bucket(IReadOnlyList<long> buckets, int index) =>
        index < buckets.Count ? L(buckets[index]) : "";

    private static string N(double value, int decimals) =>
        Math.Round(value, decimals).ToString(CultureInfo.InvariantCulture);

    /// <summary>Frames to milliseconds at the profile's 48 kHz.</summary>
    private static string Ms(long frames) =>
        (frames / 48).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A device name is whatever somebody typed, so it can hold a comma or
    /// a quote. Anything else here is a number the Hub produced.
    /// </summary>
    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-KeepDays);
        foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("oal-*.csv"))
        {
            if (file.LastWriteTimeUtc >= cutoff)
            {
                continue;
            }
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not remove old sample log {File}", file.Name);
            }
        }
    }
}
