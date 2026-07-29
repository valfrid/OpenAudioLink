using System.Text;
using OpenAudioLink.Hub.Services;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// Builds byte patterns that stand in for real firmware images.
/// </summary>
internal static class FakeImage
{
    /// <summary>
    /// An ESP-IDF application image: 0xE9 image magic, and an
    /// <c>esp_app_desc_t</c> at offset 0x20 whose magic word is 0xABCD5432.
    /// </summary>
    public static byte[] Application(
        string version = "0.2.1",
        string projectName = "oal_testnode",
        string date = "Jul 29 2026",
        string time = "13:23:34",
        string idfVersion = "v5.3.1",
        int size = FirmwareStore.HeaderBytes)
    {
        var image = new byte[size];
        image[0] = 0xE9;
        image[0x20] = 0x32;
        image[0x21] = 0x54;
        image[0x22] = 0xCD;
        image[0x23] = 0xAB;
        Put(image, 0x20 + 16, version);
        Put(image, 0x20 + 48, projectName);
        Put(image, 0x20 + 80, time);
        Put(image, 0x20 + 96, date);
        Put(image, 0x20 + 112, idfVersion);
        return image;
    }

    /// <summary>Writes NUL-terminated ASCII, as the C struct holds it.</summary>
    private static void Put(byte[] image, int offset, string value)
    {
        if (offset + value.Length <= image.Length)
        {
            Encoding.ASCII.GetBytes(value).CopyTo(image, offset);
        }
    }

    /// <summary>
    /// A merged flash image: the same 0xE9 magic, because it begins with
    /// the bootloader, but no application descriptor.
    /// </summary>
    public static byte[] MergedFlash(int size = FirmwareStore.HeaderBytes)
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
        Assert.Equal(FirmwareStore.HeaderBytes, saved.Size);
        Assert.Equal(64, saved.Sha256.Length);
        Assert.True(store.Exists("testnode.bin"));

        var listed = Assert.Single(store.List());
        Assert.Equal(saved.Sha256, listed.Sha256);
    }

    /// <summary>
    /// Seeing the version an image contains is what tells you an update
    /// would reinstall the version already running.
    /// </summary>
    [Fact]
    public async Task Stored_image_reports_the_version_it_contains()
    {
        var store = new FirmwareStore(_dir);
        using var content = new MemoryStream(FakeImage.Application(version: "0.2.1"));

        var saved = await store.SaveAsync("testnode.bin", content, CancellationToken.None);

        Assert.Equal("0.2.1", saved!.Descriptor!.Version);
        Assert.Equal("0.2.1", Assert.Single(store.List()).Descriptor!.Version);
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

    [Fact]
    public void Descriptor_reports_every_field()
    {
        var descriptor = FirmwareStore.ReadDescriptor(FakeImage.Application(
            version: "0.2.1", projectName: "oal_testnode",
            date: "Jul 29 2026", time: "13:23:34", idfVersion: "v5.3.1"));

        Assert.NotNull(descriptor);
        Assert.Equal("0.2.1", descriptor.Version);
        Assert.Equal("oal_testnode", descriptor.ProjectName);
        Assert.Equal("Jul 29 2026 13:23:34", descriptor.BuiltAt);
        Assert.Equal("v5.3.1", descriptor.IdfVersion);
    }

    /// <summary>
    /// A field shorter than its fixed width is NUL-padded; the padding must
    /// not end up in the string.
    /// </summary>
    [Fact]
    public void Descriptor_strings_stop_at_the_terminator()
    {
        var descriptor = FirmwareStore.ReadDescriptor(FakeImage.Application(version: "1.0"));

        Assert.Equal("1.0", descriptor!.Version);
        Assert.Equal(3, descriptor.Version.Length);
    }

    /// <summary>
    /// Before version.txt existed, IDF stamped a git description into the
    /// version field. Images built then are still installable.
    /// </summary>
    [Fact]
    public void Git_description_version_is_read_as_written()
    {
        Assert.Equal("6c79b3e", FirmwareStore.ReadDescriptor(
            FakeImage.Application(version: "6c79b3e"))!.Version);
    }

    [Fact]
    public void Merged_flash_image_has_no_descriptor()
    {
        Assert.Null(FirmwareStore.ReadDescriptor(FakeImage.MergedFlash()));
    }

    /// <summary>
    /// A file that ends inside the descriptor must not be read past its end.
    /// </summary>
    [Fact]
    public void Descriptor_of_a_truncated_image_is_null()
    {
        var truncated = FakeImage.Application()[..(FirmwareStore.HeaderBytes - 1)];

        Assert.True(FirmwareStore.LooksLikeApplicationImage(truncated));
        Assert.Null(FirmwareStore.ReadDescriptor(truncated));
    }
}
