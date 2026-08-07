using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace OpenAudioLink.Hub.Services;

/// <summary>What a fetch found, or did not.</summary>
/// <param name="File">The image now in the store, or null when nothing was taken.</param>
/// <param name="Version">The firmware version, read from the asset's name.</param>
/// <param name="AlreadyHad">True when the newest published image was already here.</param>
public readonly record struct FirmwareFetchResult(
    string? File, string? Version, bool AlreadyHad, string? Message);

/// <summary>
/// Downloads the published node firmware into the Hub's own store, so
/// updating a speaker is one button rather than a trip through a laptop.
/// </summary>
/// <remarks>
/// The Hub already fetches itself from a release
/// (<c>scripts/update-hub.ps1</c>) and this is the same reasoning applied
/// to the thing it hands out: CI artifacts need a GitHub login and expire
/// after ninety days, so an image can only reach a Hub through a person
/// with a browser. A release asset is a plain public URL.
///
/// **Fetching is not installing.** This puts the image where the Hub can
/// serve it; pressing Update on a node stays a deliberate act. An
/// automatic download is a convenience, and an automatic flash of every
/// speaker in a house is a way to lose an evening — the node has two OTA
/// slots and a rollback, but a bad image still costs a reboot cycle per
/// speaker.
/// </remarks>
public sealed class FirmwareFetcher
{
    private readonly FirmwareStore _store;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<FirmwareFetcher> _logger;
    private readonly string _repository;

    public FirmwareFetcher(
        FirmwareStore store, IHttpClientFactory clients, IConfiguration configuration,
        ILogger<FirmwareFetcher> logger)
    {
        _store = store;
        _clients = clients;
        _logger = logger;
        _repository = configuration["Hub:FirmwareRepository"] ?? "valfrid/OpenAudioLink";
    }

    public async Task<FirmwareFetchResult> FetchLatestAsync(CancellationToken cancellationToken)
    {
        var client = _clients.CreateClient(nameof(FirmwareFetcher));

        List<Release>? releases;
        try
        {
            releases = await client.GetFromJsonAsync<List<Release>>(
                $"https://api.github.com/repos/{_repository}/releases?per_page=20", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException)
        {
            return new FirmwareFetchResult(null, null, false,
                $"could not reach GitHub: {ex.Message}");
        }

        /*
         * The OTA image, not the flash image. The merged -flash.bin is for
         * USB and the store refuses it by inspecting the header — a good
         * refusal to have, and a poor one to rely on when the right file is
         * sitting beside it under a name that says which is which.
         *
         * Releases come back newest first, so the first one carrying an
         * image wins whether it is a tagged release or the rolling build.
         */
        Asset? asset = null;
        foreach (var release in releases ?? [])
        {
            asset = (release.Assets ?? [])
                .FirstOrDefault(a => a.Name is not null
                                     && a.Name.StartsWith("testnode-", StringComparison.OrdinalIgnoreCase)
                                     && a.Name.EndsWith("-ota.bin", StringComparison.OrdinalIgnoreCase));
            if (asset is not null)
            {
                break;
            }
        }

        if (asset?.Name is null || asset.DownloadUrl is null)
        {
            return new FirmwareFetchResult(null, null, false,
                $"no node firmware published by {_repository} yet");
        }

        var version = VersionFrom(asset.Name);

        // Named by version, so having it already is a filename comparison
        // rather than a download and a hash.
        if (_store.Exists(asset.Name))
        {
            return new FirmwareFetchResult(asset.Name, version, true, null);
        }

        try
        {
            using var response = await client.GetAsync(
                asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            var saved = await _store.SaveAsync(asset.Name, content, cancellationToken);
            if (saved is null)
            {
                return new FirmwareFetchResult(null, version, false,
                    $"'{asset.Name}' is not a name this Hub will store");
            }

            _logger.LogInformation(
                "Fetched node firmware {File} ({Size} bytes, sha256 {Hash})",
                saved.File, saved.Bytes, saved.Sha256);
            return new FirmwareFetchResult(saved.File, version, false, null);
        }
        catch (InvalidFirmwareImageException ex)
        {
            return new FirmwareFetchResult(null, version, false, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new FirmwareFetchResult(null, version, false, $"download failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pulls 0.13.0 out of "testnode-esp32s3-0.13.0-ota.bin".
    /// </summary>
    /// <remarks>
    /// From the name rather than from the image, because the version inside
    /// an ESP-IDF header is what the build was told to call itself and the
    /// name is what the release says it is. When those disagree the release
    /// is what somebody is choosing from.
    /// </remarks>
    private static string? VersionFrom(string assetName)
    {
        var parts = assetName.Split('-');
        // testnode | esp32s3 | 0.13.0 | ota.bin
        return parts.Length >= 4 && parts[^2].Length > 0 && char.IsDigit(parts[^2][0])
            ? parts[^2]
            : null;
    }

    private sealed record Release
    {
        [JsonPropertyName("assets")]
        public List<Asset>? Assets { get; init; }
    }

    private sealed record Asset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; init; }
    }
}
