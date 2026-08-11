using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Core.Protocol;
using OpenAudioLink.Hub.Configuration;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// Puts a node-to-node stream back after the network takes it away.
/// </summary>
/// <remarks>
/// A node that roams between mesh access points loses its association for a
/// second or two, and until now that was the end of the music: the producer
/// came back on the network with no idea it had been asked to play, and
/// nothing on the Hub was watching. Measured on hardware as a record
/// stopping after some minutes, every time the producer changed access
/// point.
///
/// The Hub is the only thing that can fix this, because it is the only
/// thing that remembers somebody put a record on. A node knows how to
/// stream and where to send; it does not know that it is supposed to be
/// doing so.
///
/// Two failures, both invisible from either end alone:
///
///   - the producer stopped, so nothing is being sent
///   - the producer is streaming to an address a consumer no longer has,
///     which is worse, because everything reports healthy and the room is
///     silent
///
/// Only node producers are supervised. A stream the Hub itself sends —
/// Spotify, radio, system audio — is already driven by something inside
/// this process that notices its own failures.
/// </remarks>
public sealed class StreamSupervisor : BackgroundService
{
    /// <summary>
    /// Slower than a roam and faster than a person fetching their phone.
    /// A re-association takes a second or two; checking more often would
    /// mean restarting a stream that was about to recover on its own.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a producer may be unreachable before its stream is treated
    /// as lost rather than as briefly away.
    /// </summary>
    /// <remarks>
    /// The whole point of the grace period. A node mid-roam answers nothing
    /// at all, and a supervisor with no patience would tear down a stream
    /// that was two seconds from coming back — then do it again on the next
    /// roam, which is a worse fault than the one it was fixing.
    /// </remarks>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);

    private readonly CastPointStore _castPoints;
    private readonly DeviceRegistry _registry;
    private readonly DeviceCommandClient _commands;
    private readonly IHttpClientFactory _clients;
    private readonly HubConfig _config;
    private readonly ILogger<StreamSupervisor> _logger;

    private DateTimeOffset? _unreachableSince;
    private string? _lastComplaint;

    public StreamSupervisor(
        CastPointStore castPoints, DeviceRegistry registry, DeviceCommandClient commands,
        IHttpClientFactory clients, HubConfig config, ILogger<StreamSupervisor> logger)
    {
        _castPoints = castPoints;
        _registry = registry;
        _commands = commands;
        _clients = clients;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A tick that throws must not take the Hub with it: a
                    // hosted service that faults stops the host by default,
                    // and losing discovery because one node was slow would
                    // be a poor trade.
                    _logger.LogError(ex, "Stream supervision tick failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var playing = _castPoints.Playing;
        if (playing is null || playing.ProducerId == _config.Id)
        {
            _unreachableSince = null;
            return;
        }

        if (!_castPoints.TryGet(playing.CastPointId, out var point)
            || !_registry.TryGet(playing.ProducerId, out var producer))
        {
            return;
        }

        // Not while it is being updated. A node under OTA is about to reboot
        // by design, so a supervisor that reads "the stream stopped" and
        // restarts it is fighting the update — and the poll itself is load
        // the node can do without while it writes flash.
        if (_registry.IsHushed(producer.Id))
        {
            _unreachableSince = null;
            return;
        }

        // Where the stream should be going, as the registry knows it now
        // rather than as it was when play was pressed. A consumer that
        // rejoined on a new address is the case this exists for.
        var wanted = new List<string>();
        foreach (var deviceId in point.Destinations)
        {
            if (_registry.TryGet(deviceId, out var consumer) && consumer.Online)
            {
                wanted.Add(consumer.Address);
            }
        }

        if (wanted.Count == 0)
        {
            // Every speaker in the room is away. Leaving the stream alone is
            // right: they are coming back, and restarting into an empty
            // destination list would only fail.
            return;
        }

        var state = await ReadStreamAsync(producer, cancellationToken);

        if (state is null)
        {
            /*
             * The producer did not answer. Almost always a node mid-roam,
             * which is exactly what must not be treated as a fault — so it
             * is timed rather than acted on, and only a silence longer than
             * any re-association counts as the stream being gone.
             */
            _unreachableSince ??= DateTimeOffset.UtcNow;
            if (DateTimeOffset.UtcNow - _unreachableSince < Grace)
            {
                return;
            }

            Complain($"{producer.Name} has not answered for {Grace.TotalSeconds:F0}s; "
                     + "will restart its stream when it comes back");
            return;
        }

        _unreachableSince = null;

        if (!state.Running)
        {
            _logger.LogWarning(
                "{Producer} stopped streaming {CastPoint}; starting it again",
                producer.Name, point.Name);

            var restarted = await _commands.StartStreamAsync(
                producer, wanted, ProtocolSuite.RtpPort,
                playing.Source ?? "pattern", playing.ToneHz ?? 1000, cancellationToken);

            if (restarted)
            {
                _lastComplaint = null;
                _logger.LogInformation(
                    "{CastPoint} is playing again from {Producer}", point.Name, producer.Name);
            }
            else
            {
                Complain($"could not restart {point.Name} on {producer.Name}");
            }
            return;
        }

        /*
         * Running, but possibly at the wrong address. This is the quieter
         * half and the more dangerous one: the producer reports healthy, the
         * counters climb, packets leave — and the room is silent because a
         * consumer came back on a different address after its own roam.
         */
        var current = state.Destinations ?? [];
        var missing = wanted.Except(current, StringComparer.OrdinalIgnoreCase).ToList();
        var stale = current.Except(wanted, StringComparer.OrdinalIgnoreCase).ToList();

        if (missing.Count == 0 && stale.Count == 0)
        {
            _lastComplaint = null;
            return;
        }

        _logger.LogWarning(
            "{Producer} is sending {CastPoint} to {Current}, but its speakers are at {Wanted}; correcting",
            producer.Name, point.Name, string.Join(", ", current), string.Join(", ", wanted));

        // Without interrupting the stream: a speaker that moved should
        // rejoin what everyone else is already hearing rather than restart
        // the record for the room.
        var changed = await _commands.SetStreamDestinationsAsync(
            producer, missing, stale, cancellationToken);
        if (!changed)
        {
            Complain($"could not re-point {point.Name} on {producer.Name}");
        }
    }

    private async Task<ProducerStream?> ReadStreamAsync(
        DeviceRecord producer, CancellationToken cancellationToken)
    {
        try
        {
            var client = _clients.CreateClient(nameof(DeviceStatusService));
            return await client.GetFromJsonAsync<ProducerStream>(
                $"http://{producer.Address}:{producer.ControlPort}/stream", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Says a thing once. This runs every five seconds, and a node that is
    /// away for a minute would otherwise write twelve identical lines.
    /// </summary>
    private void Complain(string message)
    {
        if (_lastComplaint == message)
        {
            return;
        }
        _lastComplaint = message;
        _logger.LogWarning("{Message}", message);
    }

    private sealed record ProducerStream
    {
        [JsonPropertyName("running")]
        public bool Running { get; init; }

        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonPropertyName("destinations")]
        public int DestinationCount { get; init; }

        /// <summary>
        /// Absent from firmware that reports only a count, in which case a
        /// wrong address cannot be detected and only a stopped stream is.
        /// </summary>
        [JsonPropertyName("destinationList")]
        public IReadOnlyList<string>? Destinations { get; init; }
    }
}
