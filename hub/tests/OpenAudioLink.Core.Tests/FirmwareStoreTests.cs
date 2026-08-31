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

    private static async Task Save(FirmwareStore store, string file, string version)
    {
        using var content = new MemoryStream(FakeImage.Application(version: version));
        await store.SaveAsync(file, content, CancellationToken.None);
    }

    [Fact]
    public async Task Saving_prunes_all_but_the_newest_few()
    {
        var store = new FirmwareStore(_dir);
        for (var minor = 1; minor <= FirmwareStore.KeepImages + 3; minor++)
        {
            await Save(store, $"node-0.{minor}.0.bin", $"0.{minor}.0");
        }

        var kept = store.List();
        Assert.Equal(FirmwareStore.KeepImages, kept.Count);
        // Newest by the version inside the image, not by when it was written.
        Assert.Equal("0.8.0", kept[0].Descriptor?.Version);
        Assert.Equal("0.4.0", kept[^1].Descriptor?.Version);
    }

    [Fact]
    public async Task Pruning_goes_by_version_not_by_upload_order()
    {
        var store = new FirmwareStore(_dir);
        // Uploaded newest first, so file order and version order disagree.
        foreach (var minor in new[] { 9, 8, 7, 6, 5, 4, 3 })
        {
            await Save(store, $"node-0.{minor}.0.bin", $"0.{minor}.0");
        }

        var kept = store.List().Select(i => i.Descriptor?.Version).ToList();
        Assert.Equal(new[] { "0.9.0", "0.8.0", "0.7.0", "0.6.0", "0.5.0" }, kept);
    }

    [Fact]
    public async Task Pruning_at_startup_tidies_a_directory_that_predates_it()
    {
        // Written straight to disk: a store that has been collecting images
        // since before anything pruned them.
        Directory.CreateDirectory(Path.Combine(_dir, "firmware"));
        for (var minor = 1; minor <= 9; minor++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(_dir, "firmware", $"node-0.{minor}.0.bin"),
                FakeImage.Application(version: $"0.{minor}.0"));
        }

        Assert.Equal(FirmwareStore.KeepImages, new FirmwareStore(_dir).List().Count);
    }

    [Fact]
    public void Pruning_refuses_to_keep_nothing()
    {
        var store = new FirmwareStore(_dir);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Prune(0));
    }

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

    /// <summary>
    /// The first entry is what the page offers by default, and pressing
    /// Update on the wrong one silently downgrades a speaker.
    /// </summary>
    [Fact]
    public async Task The_newest_version_is_listed_first()
    {
        var store = new FirmwareStore(_dir);
        await Save(store, "old.bin", "0.9.0");
        await Save(store, "new.bin", "0.13.0");
        await Save(store, "mid.bin", "0.12.0");

        var listed = store.List();

        Assert.Equal("0.13.0", listed[0].Descriptor!.Version);
        Assert.Equal("0.12.0", listed[1].Descriptor!.Version);
        Assert.Equal("0.9.0", listed[2].Descriptor!.Version);
    }

    /// <summary>
    /// The case file time gets wrong. 0.13.0 was fetched, then an older
    /// image was uploaded by hand — so the old one is the most recently
    /// written file and would sort first by timestamp.
    /// </summary>
    [Fact]
    public async Task An_image_uploaded_later_does_not_outrank_a_newer_version()
    {
        var store = new FirmwareStore(_dir);
        await Save(store, "fetched.bin", "0.13.0");
        await Task.Delay(20);
        await Save(store, "uploaded.bin", "0.12.0");

        Assert.Equal("0.13.0", store.List()[0].Descriptor!.Version);
    }

    /// <summary>
    /// Ten is after nine, which string ordering gets backwards — and this is
    /// a project whose firmware is on 0.13 with 0.9 still in the folder.
    /// </summary>
    [Fact]
    public async Task Versions_are_compared_as_numbers_not_as_text()
    {
        var store = new FirmwareStore(_dir);
        await Save(store, "nine.bin", "0.9.0");
        await Save(store, "thirteen.bin", "0.13.0");

        Assert.Equal("0.13.0", store.List()[0].Descriptor!.Version);
    }

    /// <summary>
    /// A hand-built image from before version.txt carries a git hash where a
    /// version should be. It still installs, so it must still be listed —
    /// just never as the default choice.
    /// </summary>
    [Fact]
    public async Task An_unreadable_version_is_listed_but_never_first()
    {
        var store = new FirmwareStore(_dir);
        await Save(store, "hash.bin", "6c79b3e");
        await Save(store, "numbered.bin", "0.9.0");

        var listed = store.List();

        Assert.Equal(2, listed.Count);
        Assert.Equal("0.9.0", listed[0].Descriptor!.Version);
        Assert.Equal("6c79b3e", listed[1].Descriptor!.Version);
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
