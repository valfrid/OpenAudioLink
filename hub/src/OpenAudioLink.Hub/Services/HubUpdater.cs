using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace OpenAudioLink.Hub.Services;

/// <summary>What an update check found.</summary>
/// <param name="Installed">The version running now.</param>
/// <param name="Available">The newest published version, or null if unknown.</param>
/// <param name="Asset">The package that would be installed.</param>
/// <param name="CanUpdate">Whether this Hub is able to update itself here.</param>
/// <param name="Reason">Why it cannot, or why the check failed.</param>
public readonly record struct HubUpdateCheck(
    string Installed, string? Available, string? Asset, bool CanUpdate, string? Reason)
{
    /// <summary>True when something newer than the running version exists.</summary>
    public bool Newer =>
        Version.TryParse(Available, out var there)
        && Version.TryParse(Installed, out var here)
        && there > here;
}

/// <summary>
/// Lets the Hub update itself from its own web interface.
/// </summary>
/// <remarks>
/// The PowerShell one-liner works, and it asks for more than it looks like:
/// an elevated prompt, the right path, and knowing that the path is right.
/// Getting any of the three wrong produces an error about something else —
/// a missing file, a mark-of-the-web block, an installer that cannot find
/// its sibling. A button that is already on the page the operator is
/// looking at removes the whole class of problem.
///
/// **This does not reimplement the update.** It reads the published version
/// to show what is available, and starts <c>update-hub.ps1</c> for the
/// install itself. That script stops the service, replaces the files and
/// starts it again, and it is the path that has actually been exercised;
/// a second implementation would be a second thing to get wrong.
/// </remarks>
public sealed class HubUpdater
{
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<HubUpdater> _logger;
    private readonly string _repository;

    public HubUpdater(
        IHttpClientFactory clients, IConfiguration configuration, ILogger<HubUpdater> logger)
    {
        _clients = clients;
        _logger = logger;
        _repository = configuration["Hub:FirmwareRepository"] ?? "valfrid/OpenAudioLink";
    }

    /// <summary>
    /// The updater script, beside the running Hub. Null when it is not
    /// there — a Hub started from a build directory rather than installed.
    /// </summary>
    public static string? ScriptPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "scripts", "update-hub.ps1");
            return File.Exists(path) ? path : null;
        }
    }

    public async Task<HubUpdateCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var script = ScriptPath;
        var canUpdate = script is not null && OperatingSystem.IsWindows();
        var blocked = !OperatingSystem.IsWindows()
            ? "updating from here is a Windows service operation"
            : script is null
                ? "this Hub was not installed by install-service.ps1, so there is no updater beside it"
                : null;

        List<Release>? releases;
        try
        {
            releases = await _clients.CreateClient(nameof(FirmwareFetcher))
                .GetFromJsonAsync<List<Release>>(
                    $"https://api.github.com/repos/{_repository}/releases?per_page=20",
                    cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException)
        {
            return new HubUpdateCheck(
                HubInfo.Version, null, null, canUpdate, $"could not reach GitHub: {ex.Message}");
        }

        /*
         * The highest version across every release, not the first asset
         * found. A rolling release is meant to hold one build, but an asset
         * whose name carries a version does not replace one carrying a
         * different version, and the API returns them in upload order — so
         * the first is the oldest. That mistake once offered a Hub 0.7.0
         * while 0.9.2 sat beside it, and it is worth not making twice.
         */
        string? asset = null;
        Version? newest = null;
        foreach (var release in releases ?? [])
        {
            foreach (var candidate in release.Assets ?? [])
            {
                var version = VersionFrom(candidate.Name);
                if (version is null || !Version.TryParse(version, out var parsed))
                {
                    continue;
                }
                if (newest is null || parsed > newest)
                {
                    newest = parsed;
                    asset = candidate.Name;
                }
            }
        }

        return new HubUpdateCheck(
            HubInfo.Version, newest?.ToString(3), asset, canUpdate,
            blocked ?? (newest is null ? $"no Hub package published by {_repository} yet" : null));
    }

    /// <summary>Pulls 0.10.7 out of "OpenAudioLink-Hub-win-x64-0.10.7.zip".</summary>
    private static string? VersionFrom(string? assetName)
    {
        const string Prefix = "OpenAudioLink-Hub-win-x64-";
        if (assetName is null
            || !assetName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return assetName[Prefix.Length..^4];
    }

    /// <summary>
    /// Starts the updater and returns; the answer arrives as the service
    /// going away and coming back.
    /// </summary>
    /// <remarks>
    /// Detached deliberately. The script's first act is to stop this
    /// service, so a child process in the Hub's own job would be killed
    /// halfway through replacing the Hub — which is the one outcome worse
    /// than not updating at all. Launching through the shell gives it its
    /// own lifetime, and the script's own logging is the record of what
    /// happened, since nothing here survives to write any.
    /// </remarks>
    public bool Start(out string? error)
    {
        var script = ScriptPath;
        if (script is null || !OperatingSystem.IsWindows())
        {
            error = "there is no updater script beside this Hub";
            return false;
        }

        try
        {
            _logger.LogWarning(
                "Update requested from the web interface; running {Script}. "
                + "This service is about to stop and restart.", script);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                // -File rather than -Command: the path may contain spaces,
                // and Program Files always does.
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            error = null;
            return process is not null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start the updater");
            error = ex.Message;
            return false;
        }
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
    }
}
