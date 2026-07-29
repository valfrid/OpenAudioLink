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

public class FirmwareImageValidationTests
{
    private static byte[] Image(byte first, bool withAppDescriptor)
    {
        var image = new byte[64];
        image[0] = first;
        if (withAppDescriptor)
        {
            // esp_app_desc_t magic word 0xABCD5432, little-endian, at 0x20.
            image[0x20] = 0x32;
            image[0x21] = 0x54;
            image[0x22] = 0xCD;
            image[0x23] = 0xAB;
        }
        return image;
    }

    [Fact]
    public void Application_image_is_accepted()
    {
        Assert.True(FirmwareStore.LooksLikeApplicationImage(Image(0xE9, withAppDescriptor: true)));
    }

    /// <summary>
    /// A merged flash image starts with the bootloader, so it shares the
    /// 0xE9 magic but carries no application descriptor. Writing one into an
    /// OTA slot cannot boot, so it must be refused at upload.
    /// </summary>
    [Fact]
    public void Merged_flash_image_is_rejected()
    {
        Assert.False(FirmwareStore.LooksLikeApplicationImage(Image(0xE9, withAppDescriptor: false)));
    }

    [Fact]
    public void Arbitrary_file_is_rejected()
    {
        Assert.False(FirmwareStore.LooksLikeApplicationImage(Image(0x50, withAppDescriptor: true)));
    }

    [Fact]
    public void Truncated_file_is_rejected()
    {
        Assert.False(FirmwareStore.LooksLikeApplicationImage(new byte[] { 0xE9, 0x00 }));
    }
}
