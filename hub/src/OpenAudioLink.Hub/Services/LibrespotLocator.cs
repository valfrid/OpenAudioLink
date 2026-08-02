namespace OpenAudioLink.Hub.Services;

/// <summary>
/// Finds the librespot binary the operator supplied.
///
/// The Hub does not ship one (docs/CAST-POINTS.md), so this is the whole
/// installation procedure: drop the executable next to the Hub, or put it
/// on PATH, or name it in configuration. Anything else and the feature is
/// simply off.
/// </summary>
public static class LibrespotLocator
{
    private const string Name = "librespot";

    /// <summary>
    /// Resolves <paramref name="configured"/>, or searches when it is
    /// empty.
    /// </summary>
    /// <returns>A path that exists, or null.</returns>
    public static string? Find(string? configured) => Find(configured, AppContext.BaseDirectory);

    /// <summary>Overload taking the search directory, so it can be tested.</summary>
    public static string? Find(string? configured, string beside)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var named = configured.Trim();

            // A path the operator wrote is used as written and not searched
            // for: if they pointed at a file that is not there, saying so
            // beats silently running a different one.
            if (named.Contains(Path.DirectorySeparatorChar)
                || named.Contains(Path.AltDirectorySeparatorChar))
            {
                return File.Exists(named) ? Path.GetFullPath(named) : null;
            }

            return FindOnPath(named);
        }

        foreach (var candidate in Candidates(Name))
        {
            var local = Path.Combine(beside, candidate);
            if (File.Exists(local))
            {
                return local;
            }
        }

        return FindOnPath(Name);
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in Candidates(name))
            {
                string full;
                try
                {
                    full = Path.Combine(directory.Trim(), candidate);
                }
                catch (ArgumentException)
                {
                    // A PATH entry with characters no path may contain.
                    // Windows collects a few of these over the years.
                    break;
                }

                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The bare name on Unix, and with .exe on Windows — where a name
    /// without the extension is not a file, however much it looks like one.
    /// </summary>
    private static IEnumerable<string> Candidates(string name)
    {
        if (OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            yield return name + ".exe";
        }
        yield return name;
    }
}
