namespace OpenAudioLink.Hub.Configuration;

/// <summary>
/// Where the Hub keeps everything it must not lose: its identity, the cast
/// points, the firmware store and each cast point's Spotify credentials.
/// </summary>
/// <remarks>
/// This used to default to a <c>data</c> folder beside the executable, which
/// is convenient exactly once. Upgrading means putting a new build somewhere,
/// and a new build in a new folder has no data in it — so a house's rooms,
/// the Hub's identity and every sign-in quietly stayed behind in the old
/// download. Nothing failed loudly; the Hub simply started up as if it had
/// never run, which is a bad way to learn where your data lived.
///
/// So the default is a fixed per-machine location, and an upgrade becomes
/// what an operator expects it to be: replace the program, keep the setup.
/// <c>Hub:DataDirectory</c> still overrides it for anyone who wants the
/// portable arrangement back.
/// </remarks>
public static class DataDirectory
{
    /// <summary>
    /// Resolves the directory, copying a legacy one alongside the executable
    /// if this machine still has one and the new location is empty.
    /// </summary>
    /// <param name="configured">
    /// <c>Hub:DataDirectory</c>, honoured as-is when set.
    /// </param>
    /// <param name="note">
    /// What happened, for the log once logging exists — this runs before the
    /// host is built. Null when there was nothing worth saying.
    /// </param>
    public static string Resolve(string? configured, out string? note)
    {
        note = null;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var chosen = Default();
        var legacy = Path.Combine(AppContext.BaseDirectory, "data");

        // Only when the new home has nothing in it. A Hub that has already
        // run here owns its data, and an old download must never overwrite
        // a working installation because someone launched it twice.
        if (!Directory.Exists(legacy)
            || string.Equals(
                Path.TrimEndingDirectorySeparator(legacy),
                Path.TrimEndingDirectorySeparator(chosen),
                StringComparison.OrdinalIgnoreCase)
            || (Directory.Exists(chosen) && Directory.EnumerateFileSystemEntries(chosen).Any()))
        {
            return chosen;
        }

        try
        {
            // Copied rather than moved. If anything here is wrong, the
            // operator still has the original beside the old executable —
            // and this is the one directory in the system that cannot be
            // regenerated.
            Copy(legacy, chosen);
            note = $"Moved in from {legacy}; the old copy is still there and can be deleted once this looks right.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note = $"Found data at {legacy} but could not copy it here ({ex.Message}). "
                 + "Copy it by hand, or set Hub:DataDirectory to that path.";
        }

        return chosen;
    }

    /// <summary>
    /// <c>%ProgramData%\OpenAudioLink</c> on Windows: readable by the
    /// installing user and by the service account, which a folder under one
    /// user's profile is not.
    /// </summary>
    private static string Default()
    {
        var root = Environment.GetFolderPath(
            OperatingSystem.IsWindows()
                ? Environment.SpecialFolder.CommonApplicationData
                : Environment.SpecialFolder.LocalApplicationData);

        return string.IsNullOrEmpty(root)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(root, "OpenAudioLink");
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(from, to, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to, StringComparison.OrdinalIgnoreCase), overwrite: false);
        }
    }
}
