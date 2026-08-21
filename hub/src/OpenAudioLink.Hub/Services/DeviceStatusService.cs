using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Hub.Configuration;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// Polls each online device's <c>GET /status</c> (protocol/CONTROL.md) so
/// the device list can show uptime and Wi-Fi state.
///
/// This is deliberately not carried on the discovery announce. RSSI moves
/// constantly, and an announce is multicast to every device on the
/// network twelve times a minute; telemetry belongs on the control plane,
/// asked for by whoever wants it.
/// </summary>
public sealed class DeviceStatusService : BackgroundService
{
    /// <summary>
    /// Slower than the announce interval. Signal strength is worth watching,
    /// not worth a request per second per node, and a stale reading carries
    /// its own timestamp so nothing is passed off as fresher than it is.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly DeviceRegistry _registry;
    private readonly IHttpClientFactory _clients;
    private readonly HubConfig _config;
    private readonly ILogger<DeviceStatusService> _logger;

    public DeviceStatusService(
        DeviceRegistry registry, IHttpClientFactory clients, HubConfig config,
        ILogger<DeviceStatusService> logger)
    {
        _registry = registry;
        _clients = clients;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                // Only devices that are online and not this Hub: polling a
                // device that has stopped announcing just burns a timeout
                // per cycle, and the Hub has no /status of its own.
                //
                // Identity, not role. Holding Controller used to mean being
                // the Hub, and since a node can claim the role (decision 9)
                // it no longer does — filtering on the role made a turntable
                // that took charge stop being watched at all.
                //
                // A device under OTA is skipped entirely. Polling it is
                // useless — it is writing flash and about to reboot — and on
                // a node whose control server is short of stack it is worse
                // than useless: it is the load that kills the download.
                var targets = _registry.Snapshot()
                    .Where(d => d.Online && d.Id != _config.Id && !_registry.IsHushed(d.Id))
                    .ToList();

                // In parallel: one slow node must not delay the readings of
                // every node behind it in the list.
                await Task.WhenAll(targets.Select(d => PollAsync(d, stoppingToken)));
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PollAsync(DeviceRecord device, CancellationToken stoppingToken)
    {
        var uri = $"http://{device.Address}:{device.ControlPort}/status";
        try
        {
            var client = _clients.CreateClient(nameof(DeviceStatusService));
            var status = await client.GetFromJsonAsync<StatusResponse>(uri, stoppingToken);
            if (status is null)
            {
                return;
            }

            _registry.UpdateStatus(device.Id, new DeviceStatus
            {
                UptimeSeconds = status.UptimeSeconds,
                FreeHeapBytes = status.FreeHeapBytes,
                Joined = status.Wifi?.Joined ?? false,
                Ssid = status.Wifi?.Ssid,
                Bssid = status.Wifi?.Bssid,
                Channel = status.Wifi?.Channel,
                Rssi = status.Wifi?.Rssi,
                AudioChannel = status.AudioChannel,
                Output = status.Output,
                OutputReady = status.OutputReady,
                Volume = status.Volume,
                /*
                 * Read here as well as declared on DeviceStatus.
                 *
                 * This service does not deserialise into DeviceStatus; it
                 * builds one field by field from a DTO. So adding a
                 * property to the record does nothing on its own, which is
                 * how delayMs came to be always null: the node reported it,
                 * the record had a place for it, and nothing carried it
                 * across. The Delay dialog opened blank on a node that was
                 * holding 50 ms, which reads as "not set" and is worse than
                 * showing nothing at all.
                 */
                DelayMs = status.DelayMs,
                Input = status.Input is { } input
                    ? new InputLevel(input.LeftDb, input.RightDb, input.Hz, input.ReadErrors)
                    : null,
                // Null means the node did not say, which older firmware
                // does not. That is a different thing from having looked
                // and found nobody, and showing them alike would report a
                // fault that is really a version.
                Controller = status.Controller?.Who switch
                {
                    "self" => "self",
                    "peer" => status.Controller.Name,
                    "none" => "none",
                    _ => null,
                },
                JoinStatus = status.Join?.Asked == true ? status.Join.Status : null,
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException)
        {
            // Expected while a node reboots or drops off; the last reading
            // stays, timestamped, rather than being replaced with nothing.
            _logger.LogDebug(ex, "Status poll of {Device} failed", device.Name);
        }
    }

    private sealed record StatusResponse
    {
        [JsonPropertyName("uptimeS")]
        public long UptimeSeconds { get; init; }

        [JsonPropertyName("heapFree")]
        public long? FreeHeapBytes { get; init; }

        [JsonPropertyName("wifi")]
        public WifiStatus? Wifi { get; init; }

        /// <summary>Named to avoid colliding with the Wi-Fi channel below.</summary>
        [JsonPropertyName("channel")]
        public string? AudioChannel { get; init; }

        [JsonPropertyName("volume")]
        public int? Volume { get; init; }

        /// <summary>
        /// Extra playout delay this node holds, or null from firmware that
        /// predates it.
        /// </summary>
        [JsonPropertyName("delayMs")]
        public int? DelayMs { get; init; }

        /// <summary>How audio leaves the board: "i2s" or "usb".</summary>
        [JsonPropertyName("output")]
        public string? Output { get; init; }

        /// <summary>
        /// Whether that output stage can currently take samples. Always true
        /// for I2S; false on a USB node with no dongle plugged in — which
        /// receives, buffers and plays nothing, and looks entirely healthy
        /// from every other counter.
        /// </summary>
        [JsonPropertyName("outputReady")]
        public bool? OutputReady { get; init; }

        [JsonPropertyName("input")]
        public InputStatus? Input { get; init; }

        [JsonPropertyName("controller")]
        public ControllerStatus? Controller { get; init; }

        [JsonPropertyName("join")]
        public JoinStatus? Join { get; init; }
    }

    private sealed record InputStatus
    {
        [JsonPropertyName("leftDb")]
        public int LeftDb { get; init; }

        [JsonPropertyName("rightDb")]
        public int RightDb { get; init; }

        [JsonPropertyName("hz")]
        public int Hz { get; init; }

        [JsonPropertyName("readErrors")]
        public int ReadErrors { get; init; }
    }

    private sealed record ControllerStatus
    {
        [JsonPropertyName("who")]
        public string? Who { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed record JoinStatus
    {
        [JsonPropertyName("asked")]
        public bool Asked { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }

    private sealed record WifiStatus
    {
        [JsonPropertyName("joined")]
        public bool Joined { get; init; }

        [JsonPropertyName("ssid")]
        public string? Ssid { get; init; }

        [JsonPropertyName("bssid")]
        public string? Bssid { get; init; }

        [JsonPropertyName("channel")]
        public int? Channel { get; init; }

        [JsonPropertyName("rssi")]
        public int? Rssi { get; init; }
    }
}
