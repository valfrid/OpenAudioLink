using System.Text;
using System.Text.Json;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Core.Net;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// Sends control-plane commands to a device's control API
/// (protocol/CONTROL.md). Audio never flows here.
/// </summary>
public sealed class DeviceCommandClient
{
    private readonly HttpClient _http;
    private readonly int _hubPort;
    private readonly ILogger<DeviceCommandClient> _logger;

    public DeviceCommandClient(HttpClient http, IConfiguration configuration, ILogger<DeviceCommandClient> logger)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _hubPort = ParseHubPort(configuration["Urls"]);
        _logger = logger;
    }

    public async Task<bool> RebootAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        return await PostAsync(device, "/reboot", body: null, cancellationToken);
    }

    /// <summary>
    /// Stores the roles a node takes (decision 5). They apply at its next
    /// boot, because roles decide which tasks start — changing them under a
    /// running node would mean tearing down live audio.
    /// </summary>
    public async Task<bool> SetRolesAsync(
        DeviceRecord device, IReadOnlyList<string> roles, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Setting roles on {Device} to {Roles}", device.Name, string.Join(", ", roles));
        return await PostAsync(device, "/config", JsonSerializer.Serialize(new { roles }), cancellationToken);
    }

    /// <summary>
    /// Tells the device to pull and install a firmware image from this Hub.
    /// The download URL uses the Hub address as seen from the device's
    /// subnet, so multi-homed hosts advertise a reachable address.
    /// </summary>
    public async Task<bool> StartOtaAsync(DeviceRecord device, string firmwareFile, CancellationToken cancellationToken)
    {
        var hubAddress = LocalAddressSelector.ForDevice(device.Address);
        var url = $"http://{hubAddress}:{_hubPort}/firmware/{Uri.EscapeDataString(firmwareFile)}";
        // Worth logging: when an update fails at connect, the first question
        // is always whether the node was handed an address it can reach.
        _logger.LogInformation(
            "Update for {Device} at {DeviceAddress}: serving {Url}", device.Name, device.Address, url);
        return await PostAsync(device, "/ota", JsonSerializer.Serialize(new { url }), cancellationToken);
    }

    /// <summary>
    /// Tells a producer to stream to the given consumers. The Hub does not
    /// relay audio (ARCHITECTURE.md section 3): it names the destinations
    /// and the producer sends to them directly.
    /// </summary>
    public async Task<bool> StartStreamAsync(
        DeviceRecord device, IReadOnlyList<string> destinations, int port,
        string source, int toneHz, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting {Source} stream on {Device} to {Count} destination(s) on port {Port}",
            source, device.Name, destinations.Count, port);
        var body = JsonSerializer.Serialize(new { destinations, port, source, toneHz });
        return await PostAsync(device, "/stream/start", body, cancellationToken);
    }

    /// <summary>Stops a producer, or clears a consumer's counters.</summary>
    public async Task<bool> StopStreamAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        return await PostAsync(device, "/stream/stop", body: null, cancellationToken);
    }

    /// <summary>Reads a node's stream state as raw JSON, shape depending on its role.</summary>
    public async Task<JsonDocument?> GetStreamAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        var uri = $"http://{device.Address}:{device.ControlPort}/stream";
        try
        {
            using var response = await _http.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "GET {Uri} failed", uri);
            return null;
        }
    }

    private async Task<bool> PostAsync(DeviceRecord device, string path, string? body, CancellationToken cancellationToken)
    {
        var uri = $"http://{device.Address}:{device.ControlPort}{path}";
        try
        {
            using var content = body is null
                ? null
                : new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(uri, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("POST {Uri} returned {Status}", uri, (int)response.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "POST {Uri} failed", uri);
            return false;
        }
    }

    private static int ParseHubPort(string? urls)
    {
        var first = urls?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is not null
            && Uri.TryCreate(first.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri))
        {
            return uri.Port;
        }
        return Core.Protocol.ProtocolSuite.DefaultHubPort;
    }
}
