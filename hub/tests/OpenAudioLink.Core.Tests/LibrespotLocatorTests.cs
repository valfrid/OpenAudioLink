using OpenAudioLink.Core.Audio;
using OpenAudioLink.Hub.Configuration;
using OpenAudioLink.Hub.Services;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class LibrespotLocatorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "oal-librespot-" + Guid.NewGuid().ToString("N"));

    public LibrespotLocatorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    private string Place(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "not really a binary");
        return path;
    }

    private static string ExecutableName =>
        OperatingSystem.IsWindows() ? "librespot.exe" : "librespot";

    [Fact]
    public void Finds_the_binary_beside_the_hub()
    {
        var expected = Place(ExecutableName);

        Assert.Equal(expected, LibrespotLocator.Find(null, _directory));
    }

    /// <summary>
    /// Nothing installed is the ordinary case, not a fault. The Hub must
    /// carry on being a Hub.
    /// </summary>
    [Fact]
    public void Finds_nothing_when_there_is_nothing()
    {
        Assert.Null(LibrespotLocator.Find(null, _directory));
    }

    [Fact]
    public void Uses_a_configured_path_as_written()
    {
        var placed = Place("librespot-custom" + (OperatingSystem.IsWindows() ? ".exe" : ""));

        Assert.Equal(Path.GetFullPath(placed), LibrespotLocator.Find(placed, _directory));
    }

    /// <summary>
    /// A path that was typed and is wrong must fail rather than fall back to
    /// searching, or the operator ends up running a binary they did not
    /// name and cannot find in the log.
    /// </summary>
    [Fact]
    public void A_configured_path_that_does_not_exist_is_not_searched_for()
    {
        Place(ExecutableName);
        var missing = Path.Combine(_directory, "nested", "librespot");

        Assert.Null(LibrespotLocator.Find(missing, _directory));
    }
}

public class LibrespotOptionsTests
{
    [Fact]
    public void Defaults_to_the_format_librespot_decodes_to()
    {
        var options = new LibrespotOptions();

        Assert.True(options.HasUsableFormat);
        Assert.Equal(PcmSampleFormat.F32, options.SampleFormat);
        Assert.Equal(44100, LibrespotOptions.SourceSampleRate);
    }

    /// <summary>
    /// F64 is a format librespot will emit and this Hub does not read.
    /// Saying so at startup is the difference between a clear message and a
    /// room full of noise.
    /// </summary>
    [Fact]
    public void Reports_a_format_it_cannot_decode()
    {
        Assert.False(new LibrespotOptions { Format = "F64" }.HasUsableFormat);
    }
}
