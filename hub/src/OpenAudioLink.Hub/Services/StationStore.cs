using System.Text.Json;
using OpenAudioLink.Core;
using OpenAudioLink.Core.Radio;

namespace OpenAudioLink.Hub.Services;

/// <summary>Why a station could not be saved.</summary>
public enum StationError
{
    None,
    NotFound,
    NameRequired,
    NameUnusable,
    UrlRequired,

    /// <summary>
    /// The URL is not an absolute http or https address. Refused here rather
    /// than at play time, because "nothing happened when I pressed it" is a
    /// much worse message than "that is not a URL".
    /// </summary>
    UrlUnusable,

    /// <summary>
    /// Another station already has this name. Two identical buttons on a
    /// switchboard is the same problem two identical entries in Spotify's
    /// picker is: the person choosing cannot tell them apart.
    /// </summary>
    NameTaken,
}

/// <summary>
/// The stations this house listens to (docs/ROADMAP.md, internet radio).
///
/// Deliberately shaped like <see cref="CastPointStore"/>: a JSON file beside
/// the Hub's identity, an ordered list rather than a dictionary, and a
/// corrupt entry costs one station rather than the whole Hub.
/// </summary>
public sealed class StationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// What a fresh Hub starts with, so the switchboard has something on it
    /// before anyone has pasted a URL. All MP3, because MP3 is what the
    /// decoder handles today (<see cref="RadioSource"/>).
    /// </summary>
    /// <remarks>
    /// Seeded once, on the first run that finds no file. Not re-applied
    /// afterwards: a station somebody deleted must stay deleted, and a Hub
    /// that put four stations back every restart would be worse than one
    /// that started empty.
    ///
    /// These are the addresses the stations publish, not addresses this
    /// project has reachability guarantees over. A station that moves shows
    /// up as an error on the button, which is why the switchboard reports
    /// what failed instead of going quiet.
    /// </remarks>
    private static readonly Station[] Seed =
    [
        new() { Id = "groove-salad", Name = "SomaFM Groove Salad", Url = "https://somafm.com/groovesalad.pls" },
        new() { Id = "drone-zone", Name = "SomaFM Drone Zone", Url = "https://somafm.com/dronezone.pls" },
        new() { Id = "secret-agent", Name = "SomaFM Secret Agent", Url = "https://somafm.com/secretagent.pls" },
        new() { Id = "radio-paradise", Name = "Radio Paradise Main Mix", Url = "http://stream.radioparadise.com/mp3-192" },

        // The same programme, losslessly, and deliberately beside the MP3
        // one: the two together are an A/B nobody has to set up, and it was
        // this station's Ogg-FLAC that established the path exists at all.
        // Radio Paradise publishes its current addresses at
        // https://radioparadise.com/listen/stream-links — worth checking
        // there rather than trusting this line, which will age.
        new() { Id = "radio-paradise-flac", Name = "Radio Paradise Main Mix (FLAC)", Url = "http://stream.radioparadise.com/flac" },
    ];

    private readonly string _path;
    private readonly object _gate = new();

    private List<Station> _stations = [];

    public StationStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "stations.json");
        Load();
    }

    public IReadOnlyList<Station> Snapshot()
    {
        lock (_gate)
        {
            return [.. _stations];
        }
    }

    public bool TryGet(string id, out Station station)
    {
        lock (_gate)
        {
            var found = _stations.FirstOrDefault(s => s.Id == id);
            station = found!;
            return found is not null;
        }
    }

    public StationError Create(string? name, string? url, out Station created)
    {
        created = null!;

        if (string.IsNullOrWhiteSpace(url))
        {
            return StationError.UrlRequired;
        }
        if (!IsPlayableUrl(url, out var normalised))
        {
            return StationError.UrlUnusable;
        }

        // A station pasted with no name is named after its host, because
        // "ice1.somafm.com" on a button beats an empty one and beats making
        // the person think of a name before they can hear anything.
        var label = string.IsNullOrWhiteSpace(name) ? HostOf(normalised) : name.Trim();

        var slug = Slug.FromName(label);
        if (slug is null)
        {
            return string.IsNullOrWhiteSpace(name)
                ? StationError.UrlUnusable
                : StationError.NameUnusable;
        }

        lock (_gate)
        {
            if (_stations.Any(s => NameMatches(s.Name, label)))
            {
                return StationError.NameTaken;
            }

            var station = new Station
            {
                Id = Slug.MakeUnique(slug, candidate => _stations.Any(s => s.Id == candidate)),
                Name = label,
                Url = normalised,
            };
            _stations.Add(station);
            Save();
            created = station;
            return StationError.None;
        }
    }

    public StationError Update(string id, string? name, string? url)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return StationError.NameRequired;
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            return StationError.UrlRequired;
        }
        if (!IsPlayableUrl(url, out var normalised))
        {
            return StationError.UrlUnusable;
        }

        lock (_gate)
        {
            var index = _stations.FindIndex(s => s.Id == id);
            if (index < 0)
            {
                return StationError.NotFound;
            }
            if (_stations.Any(s => s.Id != id && NameMatches(s.Name, name)))
            {
                return StationError.NameTaken;
            }

            _stations[index] = _stations[index] with { Name = name.Trim(), Url = normalised };
            Save();
            return StationError.None;
        }
    }

    /// <summary>
    /// Puts the stations in the order given.
    /// </summary>
    /// <remarks>
    /// The order is the operator's arrangement of their own buttons, which
    /// is theirs — the same reasoning as cast points being a list rather
    /// than a dictionary. Somebody who listens to one station daily wants
    /// it first, and no amount of sorting by name or by date added will
    /// work that out.
    ///
    /// Ids not named are kept, in their existing order, after the ones that
    /// were. A reorder sent from a page that has since gone stale then
    /// loses nobody's station — it just leaves the ones it did not know
    /// about at the end.
    /// </remarks>
    /// <returns>False when an id names no station, so a typo is refused
    /// rather than silently dropping the entry.</returns>
    public bool Reorder(IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var wanted = new List<Station>(_stations.Count);
            foreach (var id in ids)
            {
                var station = _stations.FirstOrDefault(s => s.Id == id);
                if (station is null || wanted.Contains(station))
                {
                    return false;
                }
                wanted.Add(station);
            }

            wanted.AddRange(_stations.Where(s => !wanted.Contains(s)));
            _stations = wanted;
            Save();
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var removed = _stations.RemoveAll(s => s.Id == id) > 0;
            if (removed)
            {
                Save();
            }
            return removed;
        }
    }

    /// <summary>
    /// Absolute http(s) only, for the reason <see cref="StationPlaylist"/>
    /// refuses the same things inside a playlist: a <c>file://</c> URL handed
    /// to an HTTP client is a request to read the Hub's disk, and a relative
    /// path is a mistake nobody meant to save.
    /// </summary>
    private static bool IsPlayableUrl(string url, out string normalised)
    {
        normalised = url.Trim();
        return Uri.TryCreate(normalised, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static bool NameMatches(string existing, string? candidate) =>
        string.Equals(existing.Trim(), candidate?.Trim(), StringComparison.OrdinalIgnoreCase);

    private void Load()
    {
        if (!File.Exists(_path))
        {
            _stations = [.. Seed];
            Save();
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<Station>>(File.ReadAllText(_path));
            if (loaded is not null)
            {
                _stations =
                [
                    .. loaded.Where(s =>
                        !string.IsNullOrEmpty(s.Id)
                        && !string.IsNullOrEmpty(s.Name)
                        && !string.IsNullOrEmpty(s.Url)),
                ];
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Empty, not seeded: a file that exists and will not parse is a
            // file somebody's stations are in, and quietly replacing them
            // with four defaults would look like the list was lost.
            _stations = [];
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_stations, SerializerOptions));
        }
        catch (IOException)
        {
            // A station list that could not be written is worth losing on
            // restart; it is not worth failing the request that added it.
        }
    }
}
