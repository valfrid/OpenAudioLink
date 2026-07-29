using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAudioLink.Core.Protocol;

namespace OpenAudioLink.Core.Discovery;

/// <summary>
/// Wire model of the discovery protocol (protocol/DISCOVERY.md).
/// </summary>
public abstract record DiscoveryMessage
{
    public const string AnnounceType = "announce";
    public const string ProbeType = "probe";

    [JsonPropertyName("oal")]
    public required string ProtocolVersion { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Parses a discovery datagram. Returns false for invalid JSON, unknown
    /// message types, missing required fields, or an incompatible
    /// protocol-suite major version.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> datagram, out DiscoveryMessage? message)
    {
        message = null;
        try
        {
            var reader = new Utf8JsonReader(datagram);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("oal", out var oal)
                || !root.TryGetProperty("type", out var type)
                || !ProtocolSuite.IsCompatible(oal.GetString()))
            {
                return false;
            }

            message = type.GetString() switch
            {
                AnnounceType => root.Deserialize<DeviceAnnouncement>(SerializerOptions),
                ProbeType => root.Deserialize<DiscoveryProbe>(SerializerOptions),
                _ => null,
            };
            return message is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public byte[] Serialize() =>
        JsonSerializer.SerializeToUtf8Bytes<object>(this, SerializerOptions);
}

/// <summary>Periodic (or probe-triggered) presence announcement.</summary>
public sealed record DeviceAnnouncement : DiscoveryMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = AnnounceType;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Roles are capabilities and a device may hold several
    /// (ARCHITECTURE.md section 2), so this is a list rather than one
    /// value — the analog source that also plays is the case that forces
    /// it.
    /// </summary>
    [JsonPropertyName("roles")]
    public required IReadOnlyList<string> Roles { get; init; }

    [JsonPropertyName("hw")]
    public required string HardwareProfile { get; init; }

    [JsonPropertyName("fw")]
    public required string FirmwareVersion { get; init; }

    [JsonPropertyName("caps")]
    public IReadOnlyList<string>? Capabilities { get; init; }

    [JsonPropertyName("ctrlPort")]
    public int? ControlPort { get; init; }
}

/// <summary>Controller request for an immediate unicast announce.</summary>
public sealed record DiscoveryProbe : DiscoveryMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = ProbeType;
}
