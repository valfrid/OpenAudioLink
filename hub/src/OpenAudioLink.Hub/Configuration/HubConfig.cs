using System.Text.Json;
using OpenAudioLink.Core.Devices;

namespace OpenAudioLink.Hub.Configuration;

/// <summary>Persisted Hub identity and settings.</summary>
public sealed record HubConfig
{
    public required string Id { get; init; }
    public string Name { get; init; } = "OpenAudioLink Hub";
}

/// <summary>
/// JSON-file configuration storage. The Hub's identity must survive
/// restarts, so it is generated once and persisted in the data directory.
/// </summary>
public sealed class HubConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public HubConfigStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "hub.json");
    }

    public HubConfig LoadOrCreate()
    {
        if (File.Exists(_path))
        {
            var loaded = JsonSerializer.Deserialize<HubConfig>(File.ReadAllText(_path));
            if (loaded is not null && DeviceIdentity.IsValid(loaded.Id))
            {
                return loaded;
            }
        }

        var created = new HubConfig { Id = DeviceIdentity.NewProvisioned() };
        Save(created);
        return created;
    }

    public void Save(HubConfig config) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(config, SerializerOptions));
}
