using OpenAudioLink.Hub.Services;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// Builds byte patterns that stand in for real firmware images.
/// </summary>
internal static class FakeImage
{
    /// <summary>
    /// An ESP-IDF application image: 0xE9 image magic, and the
    /// <c>esp_app_desc_t</c> magic word 0xABCD5432 at offset 0x20.
    /// </summary>
    public static byte[] Application(int size = 64)
    {
        var image = new byte[size];
        image[0] = 0xE9;
        image[0x20] = 0x32;
        image[0x21] = 0x54;
        image[0x22] = 0xCD;
        image[0x23] = 0xAB;
        return image;
    }

    /// <summary>
    /// A merged flash image: the same 0xE9 magic, because it begins with
    /// the bootloader, but no application descriptor.
    /// </summary>
    public static byte[] MergedFlash(int size = 64)
    {
        var image = new byte[size];
        image[0] = 0xE9;
        return image;
    }
}

public class FirmwareStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oal-tests-" + Guid.NewGuid());

    [Fact]
    public async Task Save_then_list_reports_size_and_checksum()
    {
        var store = new FirmwareStore(_dir);
        using var content = new MemoryStream(FakeImage.Application());

        var saved = await store.SaveAsync("testnode.bin", content, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal("testnode.bin", saved.File);
        Assert.Equal(64, saved.Size);
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
        using var content = new MemoryStream(FakeImage.Application());

        Assert.Null(await store.SaveAsync(name, content, CancellationToken.None));
        Assert.False(store.Exists(name));
    }

    /// <summary>
    /// Uploading the merged USB image instead of the application image is
    /// the easy mistake, and it cannot boot from an OTA slot.
    /// </summary>
    [Fact]
    public async Task Merged_flash_image_is_refused_and_not_kept()
    {
        var store = new FirmwareStore(_dir);
        using var content = new MemoryStream(FakeImage.MergedFlash());

        await Assert.ThrowsAsync<InvalidFirmwareImageException>(
            () => store.SaveAsync("testnode-flash.bin", content, CancellationToken.None));

        Assert.False(store.Exists("testnode-flash.bin"));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Arbitrary_file_is_refused()
    {
        var store = new FirmwareStore(_dir);
        using var content = new MemoryStream("not firmware at all"u8.ToArray());

        await Assert.ThrowsAsync<InvalidFirmwareImageException>(
            () => store.SaveAsync("notes.bin", content, CancellationToken.None));

        Assert.Empty(store.List());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }
}

public class FirmwareImageValidationTests
{
    [Fact]
    public void Application_image_is_accepted()
    {
        Assert.True(FirmwareStore.LooksLikeApplicationImage(FakeImage.Application()));
    }

    [Fact]
    public void Merged_flash_image_is_rejected()
    {
        Assert.False(FirmwareStore.LooksLikeApplicationImage(FakeImage.MergedFlash()));
    }

    [Fact]
    public void Wrong_image_magic_is_rejected()
    {
        var image = FakeImage.Application();
        image[0] = 0x50;
        Assert.False(FirmwareStore.LooksLikeApplicationImage(image));
    }

    [Fact]
    public void Truncated_file_is_rejected()
    {
        Assert.False(FirmwareStore.LooksLikeApplicationImage(new byte[] { 0xE9, 0x00 }));
    }
}
