using System.Text.Json;
using OpenAudioLink.Core.CastPoints;

namespace OpenAudioLink.Hub.Services;

/// <summary>Why a cast point could not be started or changed.</summary>
public enum CastPointError
{
    None,
    NotFound,
    NameRequired,
    NameUnusable,
    NoDestinations,

    /// <summary>
    /// Another cast point already has this name. The id would have been
    /// made unique, but the name is what Spotify shows: two "Party"
    /// entries in the picker are indistinguishable to the person choosing,
    /// and each has its own credentials, so one can be signed in and the
    /// other not.
    /// </summary>
    NameTaken,
}

/// <summary>
/// Which cast point is currently playing, and through which producer.
/// Runtime state, deliberately not persisted: after a Hub restart nothing
/// is playing, and claiming otherwise would show the operator a stream
/// that does not exist.
/// </summary>
public sealed record CastPointPlayback
{
    public required string CastPointId { get; init; }
    public required string ProducerId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// Identifies this playback, and no other. Rises with every start.
    /// </summary>
    /// <remarks>
    /// A cast point id is not enough to clean up by. Two sources can own the
    /// same room one after the other within a tick, and a service tidying up
    /// after its own stream would then delete the record belonging to
    /// whatever replaced it — the stream is still running and the Hub has
    /// forgotten it exists, so the interface shows nothing playing while the
    /// speaker plays, and Stop has nothing to aim at.
    ///
    /// That is not hypothetical: switching from Spotify to Vinyl did exactly
    /// this. Whoever started a playback holds its token and can only end
    /// that one.
    /// </remarks>
    public long Token { get; init; }

    /// <summary>
    /// What the producer was asked for — "capture", "tone", "pattern" — so
    /// a stream that stopped can be started again as the same thing.
    /// </summary>
    /// <remarks>
    /// Without this a recovery is a guess. A node that fell off the network
    /// mid-record comes back knowing nothing about what it was doing, and
    /// the Hub is the only thing that remembers somebody put a record on.
    /// </remarks>
    public string? Source { get; init; }

    public int? ToneHz { get; init; }
}

/// <summary>
/// The Hub's cast points: create, name, choose consumers, persist
/// (docs/CAST-POINTS.md).
///
/// Configuration lives in a JSON file beside the Hub's identity, because a
/// house's rooms are the one thing an operator sets up by hand and must
/// never have to set up twice.
/// </summary>
public sealed class CastPointStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    /// <summary>
    /// Ordered rather than keyed: the operator's arrangement of rooms is
    /// theirs, and re-sorting a list they chose the order of is a small
    /// rudeness a dictionary would force.
    /// </summary>
    private List<CastPoint> _points = [];

    private CastPointPlayback? _playing;

    /// <summary>Rises with every start, so no two playbacks share a token.</summary>
    private long _token;

    public CastPointStore(string dataDirectory, TimeProvider? time = null)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "castpoints.json");
        _time = time ?? TimeProvider.System;
        Load();
    }

    public IReadOnlyList<CastPoint> Snapshot()
    {
        lock (_gate)
        {
            return [.. _points];
        }
    }

    public CastPointPlayback? Playing
    {
        get
        {
            lock (_gate)
            {
                return _playing;
            }
        }
    }

    public bool TryGet(string id, out CastPoint point)
    {
        lock (_gate)
        {
            var found = _points.FirstOrDefault(p => p.Id == id);
            point = found!;
            return found is not null;
        }
    }

    public CastPointError Create(string? name, IReadOnlyList<string>? destinations, out CastPoint created)
    {
        created = null!;
        if (string.IsNullOrWhiteSpace(name))
        {
            return CastPointError.NameRequired;
        }

        var slug = CastPointId.FromName(name);
        if (slug is null)
        {
            return CastPointError.NameUnusable;
        }

        lock (_gate)
        {
            if (_points.Any(p => NameMatches(p.Name, name)))
            {
                return CastPointError.NameTaken;
            }

            var point = new CastPoint
            {
                Id = CastPointId.MakeUnique(slug, candidate => _points.Any(p => p.Id == candidate)),
                Name = name.Trim(),
                // An empty cast point is legitimate: a room is often named
                // before its speaker is unpacked. Starting one is what
                // requires destinations, not creating one.
                Destinations = Distinct(destinations),
            };
            _points.Add(point);
            Save();
            created = point;
            return CastPointError.None;
        }
    }

    /// <summary>
    /// Renames and re-points a cast point. The id is untouched, so a rename
    /// cannot orphan the thing being renamed.
    /// </summary>
    public CastPointError Update(string id, string? name, IReadOnlyList<string>? destinations)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return CastPointError.NameRequired;
        }

        lock (_gate)
        {
            var index = _points.FindIndex(p => p.Id == id);
            if (index < 0)
            {
                return CastPointError.NotFound;
            }

            // Renaming onto another cast point's name is the same clash as
            // creating one; renaming a cast point to what it is already
            // called is not, which is why this skips itself.
            if (_points.Any(p => p.Id != id && NameMatches(p.Name, name)))
            {
                return CastPointError.NameTaken;
            }

            _points[index] = _points[index] with
            {
                Name = name.Trim(),
                Destinations = Distinct(destinations),
            };
            Save();
            return CastPointError.None;
        }
    }

    /// <summary>
    /// Case- and whitespace-insensitive, because the clash is about what a
    /// person reads in a device picker, not about what they typed.
    /// </summary>
    private static bool NameMatches(string existing, string? candidate) =>
        string.Equals(existing.Trim(), candidate?.Trim(), StringComparison.OrdinalIgnoreCase);

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var removed = _points.RemoveAll(p => p.Id == id) > 0;
            if (removed)
            {
                if (_playing?.CastPointId == id)
                {
                    _playing = null;
                }
                Save();
            }
            return removed;
        }
    }

    /// <summary>
    /// The cast point that must be stopped before <paramref name="id"/> can
    /// play, or null if nothing conflicts.
    ///
    /// Two playing cast points that share a consumer would send that speaker
    /// two streams, and it can only play one. Rather than refuse — which
    /// makes the operator work out which room to stop — the newer request
    /// wins and the older one is stopped, because a person pressing play on
    /// the kitchen has said what they want to hear in the kitchen.
    /// </summary>
    public CastPointPlayback? ConflictWith(string id)
    {
        lock (_gate)
        {
            // Copied to a local before the lambdas below: the compiler
            // cannot know a field stays non-null across a closure, and
            // reading it twice would be a lie anyway.
            var playing = _playing;
            if (playing is null)
            {
                return null;
            }
            if (playing.CastPointId == id)
            {
                return playing;
            }

            /*
             * Every playing pair, not only an overlapping one.
             *
             * This used to return null when the two cast points shared no
             * speaker, on the reasoning that they could both play. They
             * cannot — not because of the audio, but because _playing is a
             * single field. Starting a second, non-overlapping cast point
             * left the first one streaming and overwrote the only record
             * that it existed: nothing showed it, and Stop could not reach
             * it. The only way to silence it was to reboot the node.
             *
             * One pair at a time is also the model the control surface is
             * built around (docs/CONTROL-SURFACE.md): pick a source, pick a
             * cast point, and that is what the house is playing. Genuine
             * simultaneous streams are a real feature and a bigger change —
             * _playing becomes a collection, and every reader of it learns
             * to ask "which one". Until then this refuses to pretend.
             */
            return playing;
        }
    }

    /// <summary>
    /// Records what is playing, and returns it so the caller keeps the token
    /// it needs to end this playback and only this one.
    /// </summary>
    public CastPointPlayback MarkPlaying(
        string castPointId, string producerId, string? source = null, int? toneHz = null)
    {
        lock (_gate)
        {
            _playing = new CastPointPlayback
            {
                CastPointId = castPointId,
                ProducerId = producerId,
                StartedAt = _time.GetUtcNow(),
                Source = source,
                ToneHz = toneHz,
                Token = ++_token,
            };
            return _playing;
        }
    }

    /// <summary>
    /// Ends a playback the caller started, and does nothing if something
    /// else has taken the room since.
    /// </summary>
    /// <remarks>
    /// For a service cleaning up after itself. A person pressing Stop wants
    /// <see cref="MarkStopped(string)"/> instead: they mean "silence that
    /// room", whoever turns out to be playing in it.
    /// </remarks>
    /// <returns>Whether this playback was the one still recorded.</returns>
    public bool MarkStoppedIfCurrent(long token)
    {
        lock (_gate)
        {
            if (_playing?.Token != token)
            {
                return false;
            }
            _playing = null;
            return true;
        }
    }

    public void MarkStopped(string castPointId)
    {
        lock (_gate)
        {
            if (_playing?.CastPointId == castPointId)
            {
                _playing = null;
            }
        }
    }

    /// <summary>
    /// Duplicates would make a Producer send the same packet to one speaker
    /// twice, which the consumer counts as a duplicate and the air pays for.
    /// </summary>
    private static IReadOnlyList<string> Distinct(IReadOnlyList<string>? destinations) =>
        destinations is null
            ? []
            : [.. destinations
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim())
                .Distinct(StringComparer.Ordinal)];

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<CastPoint>>(File.ReadAllText(_path));
            if (loaded is not null)
            {
                // Drop anything unusable rather than refusing to start. A
                // corrupt entry should cost one room, not the whole Hub.
                _points = [.. loaded.Where(p => !string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(p.Name))];
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _points = [];
        }
    }

    private void Save() =>
        File.WriteAllText(_path, JsonSerializer.Serialize(_points, SerializerOptions));
}
