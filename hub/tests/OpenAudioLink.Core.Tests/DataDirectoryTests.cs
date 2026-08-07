using OpenAudioLink.Hub.Configuration;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// Where the Hub's data lives, which is the one decision that can quietly
/// lose a house's setup.
/// </summary>
public class DataDirectoryTests
{
    /// <summary>
    /// A configured path wins outright. This is the escape hatch for anyone
    /// who wants the old portable arrangement, and an escape hatch that gets
    /// second-guessed is not one.
    /// </summary>
    [Fact]
    public void An_absolute_path_is_used_as_written()
    {
        var wanted = Path.Combine(Path.GetTempPath(), "oal-explicit");

        Assert.Equal(wanted, DataDirectory.Resolve(wanted, out var note));
        Assert.Null(note);
    }

    /// <summary>
    /// The value somebody actually types when they want the data beside the
    /// program — and the one that would otherwise mean three different
    /// folders: the download folder from a double-click, the shell's folder
    /// from a console, and C:\Windows\System32 as a Windows service.
    /// </summary>
    [Theory]
    [InlineData("data")]
    [InlineData("  data  ")]
    [InlineData("./data")]
    public void A_relative_path_is_resolved_against_the_executable(string configured)
    {
        var resolved = DataDirectory.Resolve(configured, out _);

        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data")),
            resolved);
    }

    /// <summary>
    /// Empty means "decide for me", which is what ships in appsettings.json
    /// and is therefore the path almost every Hub actually takes.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Nothing_configured_lands_somewhere_fixed_and_absolute(string? configured)
    {
        var resolved = DataDirectory.Resolve(configured, out _);

        Assert.True(Path.IsPathFullyQualified(resolved));
        // Not beside the executable: the whole point is that replacing the
        // program folder must not replace the data.
        Assert.NotEqual(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data")),
            resolved);
    }

    /// <summary>
    /// Twice in a row is the same answer. Obvious, and worth pinning: the
    /// legacy-copy branch has side effects, and a resolver that drifted
    /// between calls would put the identity in one place and the cast points
    /// in another.
    /// </summary>
    [Fact]
    public void Resolving_twice_gives_the_same_directory() =>
        Assert.Equal(
            DataDirectory.Resolve(null, out _),
            DataDirectory.Resolve(null, out _));
}
