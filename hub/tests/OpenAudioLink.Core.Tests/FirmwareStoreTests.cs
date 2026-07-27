using System.Text;
using OpenAudioLink.Hub.Services;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class FirmwareStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oal-tests-" + Guid.NewGuid());

    [Fact]
    public async Task Save_then_list_reports_size_and_checksum()
    {
        var store = new FirmwareStore(_dir);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("firmware!"));

        var saved = await store.SaveAsync("testnode.bin", content, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal("testnode.bin", saved.File);
        Assert.Equal(9, saved.Size);
        Assert.Equal(64, saved.Sha256.Length);
        Assert.True(store.Exists("testnode.bin"));

        var listed = Assert.Single(store.List());
        Assert.Equal(saved.Sha256, listed.Sha256);
    }

    [Theory]
    [InlineData("../evil.bin")]
    [InlineData("dir/evil.bin")]
    [InlineData(".hidden.bin")]
    [InlineData("not-a-binary.txt")]
    [InlineData("")]
    public async Task Unsafe_or_wrong_names_are_rejected(string name)
    {
        var store = new FirmwareStore(_dir);
        using var content = new MemoryStream([1, 2, 3]);

        Assert.Null(await store.SaveAsync(name, content, CancellationToken.None));
        Assert.False(store.Exists(name));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
