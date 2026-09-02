using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// What buffer a node is <em>meant</em> to be running: which latency
/// profile, and how far early it plays.
/// </summary>
/// <param name="Profile">
/// A <c>LatencyProfile</c> id, or null when somebody has set the ring or
/// delay by hand and taken manual control.
/// </param>
/// <param name="AlignMs">
/// Milliseconds of extra delay this node needs because its output stage is
/// faster than its partner's — a soldered I²S DAC against a USB dongle.
/// </param>
public sealed record NodeAudio(
    [property: JsonPropertyName("profile")] string? Profile,
    [property: JsonPropertyName("alignMs")] int AlignMs);

/// <summary>
/// The Hub's record of what each node's buffer should be, which is not the
/// same as what it currently is.
/// </summary>
/// <remarks>
/// <para>
/// Two things forced this to be stored rather than computed.
/// </para>
/// <para>
/// <b>The alignment offset has nowhere else to live.</b> <c>delayMs</c>
/// does two unrelated jobs — it sets the depth of the buffer, and it holds
/// an early node back so two speakers with different output stages line up
/// — a wart TUNING.md has named for several releases. Harmless until
/// something else starts writing the depth. A latency profile writes the
/// depth, so without this the profile would silently discard the
/// alignment, and that failure is the quiet kind: each speaker fine alone,
/// the pair smeared, and no counter anywhere able to say why.
/// </para>
/// <para>
/// <b>A profile cannot be applied in one step.</b> The ring is an
/// allocation that takes effect at the next boot; the delay takes effect
/// at once, and the node <em>refuses</em> a delay its current ring cannot
/// hold rather than clamping it. So moving a node from Standard to Long
/// asks for a 450 ms delay that its 400 ms ring rejects outright, and the
/// only honest sequence is: store the ring, wait for the reboot, then set
/// the delay. That wait is why the desired state is written down and
/// reconciled instead of pushed and forgotten.
/// </para>
/// <para>
/// Kept in the Hub rather than the node, so none of this needed a firmware
/// change or a reflash — which mattered, because it was written between
/// two measurement runs. The node still owns <c>ringMs</c> and
/// <c>delayMs</c>; the Hub owns the intent behind them.
/// </para>
/// </remarks>
public sealed class NodeAudioStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// The largest alignment offset worth allowing. A USB dongle against
    /// an I²S DAC is about 80 ms; past 200 it is a buffer decision wearing
    /// an alignment label, and belongs in the profile instead.
    /// </summary>
    public const int MaxAlignMs = 200;

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, NodeAudio> _nodes = [];

    public NodeAudioStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "node-audio.json");
        Load();
    }

    public NodeAudio Get(string deviceId)
    {
        lock (_gate)
        {
            return _nodes.GetValueOrDefault(deviceId, new NodeAudio(null, 0));
        }
    }

    public IReadOnlyDictionary<string, NodeAudio> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, NodeAudio>(_nodes);
        }
    }

    /// <summary>Records the profile a node should be running, and its offset.</summary>
    public void SetProfile(string deviceId, string profile, int alignMs)
    {
        lock (_gate)
        {
            _nodes[deviceId] = new NodeAudio(profile, alignMs);
            Save();
        }
    }

    /// <summary>
    /// Forgets the profile because somebody set the ring or delay by hand.
    /// </summary>
    /// <remarks>
    /// The alignment survives, because it describes the hardware rather
    /// than the intent — a dongle is still a dongle. Without this the
    /// reconciler and the operator would fight: a hand-set delay would be
    /// put back within twenty seconds and look like the Hub ignoring the
    /// request.
    /// </remarks>
    public void ClearProfile(string deviceId)
    {
        lock (_gate)
        {
            if (!_nodes.TryGetValue(deviceId, out var current) || current.Profile is null)
            {
                return;
            }
            _nodes[deviceId] = current with { Profile = null };
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _nodes = JsonSerializer.Deserialize<Dictionary<string, NodeAudio>>(
                    File.ReadAllText(_path)) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable file must not stop the Hub starting. Every node
            // reverts to "no profile, no offset", which leaves the nodes
            // running whatever they already had rather than changing
            // anything underneath them.
            _nodes = [];
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_nodes, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held in memory for this run either way.
        }
    }
}
