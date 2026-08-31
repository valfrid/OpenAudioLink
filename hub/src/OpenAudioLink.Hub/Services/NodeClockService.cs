using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Hub.Configuration;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// One node's sample clock against this Hub's, in parts per million.
/// </summary>
/// <param name="Ppm">Positive means the node's crystal runs fast.</param>
/// <param name="SigmaPpm">
/// The fit's own uncertainty. The figure is worth nothing until this is
/// small — see <see cref="NodeClockService"/>.
/// </param>
/// <param name="SpanSeconds">How long the fit covers.</param>
/// <param name="Samples">How many readings are in it.</param>
/// <param name="Suspect">
/// The rate is too far from nominal to be a crystal, so the counter behind
/// it is the likelier fault. See <see cref="NodeClockService"/>.
/// </param>
public sealed record ClockFit(
    double Ppm, double SigmaPpm, long SpanSeconds, int Samples, bool Suspect);

/// <summary>
/// Measures every consumer's playback crystal against the Hub's own clock.
/// </summary>
/// <remarks>
/// <para>
/// This lived in the browser first, and that was the wrong house for it.
/// A crystal error is tens of ppm and takes tens of minutes of samples to
/// see, but the page dropped every sample the moment it was closed, so the
/// measurement restarted from nothing each time somebody opened the panel
/// to look at it — and looking is the only reason to open it. A phone made
/// it worse: a backgrounded tab has its timers throttled, so the samples
/// that did survive were too few and too far apart to fit at all.
/// </para>
/// <para>
/// The Hub has neither problem. It runs continuously, it polls these nodes
/// anyway, and — the part that matters — <b>it owns the reference clock</b>.
/// <see cref="RtpStreamer"/> paces from a <see cref="Stopwatch"/>, and a
/// Stopwatch here reads the same counter, so a node fitted against this
/// clock is fitted against the very thing its buffer has to keep up with.
/// No second fit, no subtraction, no browser in the path.
/// </para>
/// <para>
/// Reads <c>/stream</c> rather than <c>/status</c> so it works against
/// firmware that predates it: <c>framesPlayed</c> has been on that endpoint
/// all along.
/// </para>
/// </remarks>
public sealed class NodeClockService : BackgroundService
{
    /// <summary>
    /// Half a minute, chosen for the node's sake rather than the fit's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This started at ten seconds, matching
    /// <see cref="DeviceStatusService"/>, and that was a poor trade. It put
    /// a second request per node on top of the status poll the Hub already
    /// makes and whatever the open page is asking for, on a device with
    /// very few sockets — the same scarcity the status client's handler
    /// configuration exists to respect, where running out stops a node
    /// accepting anything at all.
    /// </para>
    /// <para>
    /// The fit barely notices. Thirty seconds is sixty samples across the
    /// window instead of a hundred and eighty, and the slope's uncertainty
    /// grows only as the square root of the count — about 7 ppm at half an
    /// hour rather than 4, both comfortably inside the 25 ppm this refuses
    /// to print above. Three times less load for a third of a ppm that
    /// nobody can act on.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far back the fit reaches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The slope's uncertainty falls as the span and as the square root of
    /// the count, and since the count is the span over the poll interval it
    /// improves faster than linearly with this number.
    /// </para>
    /// <para>
    /// An hour, and it was half an hour until the poll went from ten
    /// seconds to thirty for the node's sake. That tripled the spacing and
    /// I did not re-check the arithmetic: with about 100 ms of jitter on an
    /// HTTP read of a node, thirty samples-per-thirty-minutes lands at
    /// <b>24.8 ppm</b> against a gate of 25. Sitting exactly on the
    /// threshold, the column showed a figure, crossed back, and read
    /// "settling" again — which is what it was reported doing.
    /// </para>
    /// <para>
    /// An hour takes the same jitter to about 8.8 ppm, comfortably inside
    /// the gate, and costs nothing but a longer memory: no extra request
    /// reaches any node. What it does cost is responsiveness to a genuine
    /// change — swap a power supply and the old rate stays in the window
    /// for an hour — and for a crystal, which does not move, that is the
    /// right trade.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Nothing before this much span, however many samples arrive. Fitting
    /// a slope through a short baseline is fitting the jitter.
    /// </summary>
    private static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(3);

    /// <summary>Frames per millisecond at the profile's 48 kHz.</summary>
    private const double FramesPerMs = 48.0;

    /// <summary>
    /// Past this, the reading is not a crystal and must not be shown as
    /// one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Quartz does not do this. A healthy crystal sits inside tens of ppm;
    /// the worst seen here was a node pulled to 178 by a failing power
    /// supply, and a genuine 2,000 ppm — a fifth of a percent — would be
    /// audible as pitch rather than visible only as a number. So a figure
    /// this large means the counter feeding it is wrong, which is exactly
    /// what happened: firmware before 0.39.0 lost part of a frame on every
    /// write to the sink and reported about −4,000.
    /// </para>
    /// <para>
    /// This replaces a check that compared framesPlayed against the node's
    /// total uptime, which was the wrong test and shipped broken. A node
    /// that sat idle before it started streaming has legitimately played
    /// less than its uptime — thirty seconds of idle in an hour is a ratio
    /// of 0.9917 — so the band meant to catch a leak caught every ordinary
    /// node instead. A rate cannot be fooled that way: the fit only
    /// accumulates while the node is playing, so idle time is absent from
    /// it rather than mixed into it.
    /// </para>
    /// </remarks>
    private const double ImplausiblePpm = 2000.0;

    private readonly record struct Sample(double AtMs, long Frames);

    private readonly DeviceRegistry _registry;
    private readonly IHttpClientFactory _clients;
    private readonly HubConfig _config;
    private readonly ILogger<NodeClockService> _logger;

    /// <summary>
    /// The reference. Monotonic, unaffected by anyone setting the wall
    /// clock, and the same counter the send loop paces from.
    /// </summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Sample>> _history = [];
    private readonly Dictionary<string, ClockFit> _fits = [];

    public NodeClockService(
        DeviceRegistry registry, IHttpClientFactory clients, HubConfig config,
        ILogger<NodeClockService> logger)
    {
        _registry = registry;
        _clients = clients;
        _config = config;
        _logger = logger;
    }

    /// <summary>Every node with a usable fit, by device id.</summary>
    public IReadOnlyDictionary<string, ClockFit> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, ClockFit>(_fits);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                // Consumers only, and not one being flashed: the same
                // reasoning as the status poll, and a node writing flash is
                // not playing anything to measure.
                var targets = _registry.Snapshot()
                    .Where(d => d.Online && d.Id != _config.Id
                                && d.Roles.Contains(DeviceRole.Consumer)
                                && !_registry.IsHushed(d.Id))
                    .ToList();

                await Task.WhenAll(targets.Select(d => PollAsync(d, stoppingToken)));

                Forget(targets.Select(d => d.Id).ToHashSet());
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PollAsync(DeviceRecord device, CancellationToken stoppingToken)
    {
        try
        {
            var client = _clients.CreateClient(nameof(DeviceStatusService));
            var uri = $"http://{device.Address}:{device.ControlPort}/stream";
            var stream = await client.GetFromJsonAsync<StreamResponse>(uri, stoppingToken);
            var playout = stream?.Playout;
            if (playout is null || !playout.Playing)
            {
                // Not playing means framesPlayed is standing still, and a
                // flat stretch in the middle of the window would read as a
                // slow crystal. Drop what we have and start again when it
                // resumes.
                lock (_gate)
                {
                    _history.Remove(device.Id);
                    _fits.Remove(device.Id);
                }
                return;
            }

            // Stamped after the reply, one network leg late. The lateness is
            // noise on a single sample and the fit averages it; what would
            // bias the slope is a lateness that grows, and a round trip does
            // not.
            var at = _clock.Elapsed.TotalMilliseconds;
            Record(device.Id, at, playout.FramesPlayed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException)
        {
            // A missed poll costs one sample out of a hundred and change.
            _logger.LogDebug(ex, "Clock poll of {Device} failed", device.Name);
        }
    }

    private void Record(string id, double atMs, long frames)
    {
        lock (_gate)
        {
            if (!_history.TryGetValue(id, out var samples))
            {
                samples = [];
                _history[id] = samples;
            }

            // Backwards only across a reboot or a restarted stream, and
            // either way the earlier samples describe a different run.
            if (samples.Count > 0 && frames < samples[^1].Frames)
            {
                samples.Clear();
                _fits.Remove(id);
            }

            samples.Add(new Sample(atMs, frames));
            while (samples.Count > 2 && atMs - samples[0].AtMs > Window.TotalMilliseconds)
            {
                samples.RemoveAt(0);
            }

            var fit = Fit(samples);
            if (fit is null)
            {
                _fits.Remove(id);
            }
            else
            {
                _fits[id] = fit;
            }
        }
    }

    /// <summary>Drop nodes that have gone away, so the tables do not grow.</summary>
    private void Forget(IReadOnlySet<string> keep)
    {
        lock (_gate)
        {
            foreach (var id in _history.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                _history.Remove(id);
                _fits.Remove(id);
            }
            foreach (var id in _fits.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                _fits.Remove(id);
            }
        }
    }

    /// <summary>
    /// Least squares through every sample, with the slope's own standard
    /// error.
    /// </summary>
    /// <remarks>
    /// The difference of the two end samples would be simpler and is not
    /// good enough: each reading is stamped a round trip from when the
    /// counter was really read, so an endpoint pair inherits the timing
    /// error of two particular samples. The error term is what decides
    /// whether the figure is fit to show, and shipping the fit without it
    /// once produced a column that moved by a hundred ppm between refreshes
    /// while looking perfectly confident.
    /// </remarks>
    private static ClockFit? Fit(List<Sample> samples)
    {
        if (samples.Count < 4)
        {
            return null;
        }

        double span = samples[^1].AtMs - samples[0].AtMs;
        if (span < MinimumSpan.TotalMilliseconds)
        {
            return null;
        }

        double mx = 0, my = 0;
        foreach (var s in samples)
        {
            mx += s.AtMs;
            my += s.Frames;
        }
        mx /= samples.Count;
        my /= samples.Count;

        double sxy = 0, sxx = 0;
        foreach (var s in samples)
        {
            double dx = s.AtMs - mx;
            sxy += dx * (s.Frames - my);
            sxx += dx * dx;
        }
        if (sxx <= 0)
        {
            return null;
        }

        double slope = sxy / sxx;
        double ss = 0;
        foreach (var s in samples)
        {
            double e = (s.Frames - my) - slope * (s.AtMs - mx);
            ss += e * e;
        }

        double ppm = (slope / FramesPerMs - 1) * 1e6;
        double sigma = Math.Sqrt(ss / (samples.Count - 2) / sxx) / FramesPerMs * 1e6;
        if (!double.IsFinite(ppm) || !double.IsFinite(sigma))
        {
            return null;
        }

        return new ClockFit(
            ppm, sigma, (long)(span / 1000), samples.Count,
            Math.Abs(ppm) > ImplausiblePpm);
    }

    private sealed record StreamResponse
    {
        [JsonPropertyName("playout")]
        public PlayoutResponse? Playout { get; init; }
    }

    private sealed record PlayoutResponse
    {
        [JsonPropertyName("playing")]
        public bool Playing { get; init; }

        [JsonPropertyName("framesPlayed")]
        public long FramesPlayed { get; init; }
    }
}
