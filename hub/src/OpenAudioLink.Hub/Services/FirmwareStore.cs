using System.Security.Cryptography;
using System.Text;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// What an ESP-IDF application image says about itself, read from the
/// <c>esp_app_desc_t</c> in its header. Lets the operator see which version
/// an image contains before installing it — otherwise an update that
/// reinstalls the version already running looks exactly like a successful
/// one.
/// </summary>
public sealed record FirmwareDescriptor(
    string Version, string ProjectName, string BuiltAt, string IdfVersion);

public sealed record FirmwareImage(
    string File, long Size, string Sha256, DateTimeOffset ModifiedAt, FirmwareDescriptor? Descriptor);

/// <summary>
/// Firmware repository for OTA (Phase 2.6 foundation): stores uploaded
/// images in the data directory and exposes them with checksums. Devices
/// pull images over HTTP from /firmware/{file}.
/// </summary>
public sealed class FirmwareStore
{
    private readonly string _directory;

    public FirmwareStore(string dataDirectory)
    {
        _directory = Path.Combine(dataDirectory, "firmware");
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    /// <summary>
    /// Newest firmware first, by the version inside the image.
    /// </summary>
    /// <remarks>
    /// By version and not by file time, because the first entry is what the
    /// page offers by default and pressing Update on the wrong one silently
    /// downgrades a speaker.
    ///
    /// File time is the moment the bytes landed here, which is not the same
    /// as how new the build is: re-uploading last month's image makes it the
    /// most recently written file, and copying a directory or restoring a
    /// backup rewrites every timestamp at once. The version is in the image
    /// header, it is already read to display, and it is the thing being
    /// asked about.
    ///
    /// An image whose version cannot be read sorts last rather than first —
    /// one this cannot place is one it must not offer by default.
    /// </remarks>
    public IReadOnlyList<FirmwareImage> List() =>
        new DirectoryInfo(_directory)
            .EnumerateFiles("*.bin")
            .Select(f => new FirmwareImage(
                f.Name, f.Length, ComputeSha256(f.FullName), f.LastWriteTimeUtc, ReadDescriptor(f.FullName)))
            .OrderByDescending(image => ParsedVersion(image))
            .ThenByDescending(image => image.ModifiedAt)
            .ToList();

    /// <summary>
    /// The image's own version as something orderable, or null when it does
    /// not parse. Null sorts last under OrderByDescending, which is the
    /// intent: an unplaceable image must not become the default choice.
    /// </summary>
    private static Version? ParsedVersion(FirmwareImage image) =>
        Version.TryParse(image.Descriptor?.Version, out var version) ? version : null;

    public bool Exists(string file) =>
        TrySanitize(file, out var path) && File.Exists(path);

    /// <summary>
    /// An ESP-IDF application image carries an <c>esp_app_desc_t</c> whose
    /// magic word sits at offset 0x20. A merged flash image starts with the
    /// bootloader instead, so it fails this check — which matters because
    /// both begin with the same 0xE9 image magic and are otherwise easy to
    /// confuse. Writing a merged image into an OTA slot cannot boot.
    /// </summary>
    private const int AppDescriptorOffset = 0x20;
    private static readonly byte[] AppDescriptorMagic = [0x32, 0x54, 0xCD, 0xAB];

    // Field offsets within esp_app_desc_t, which is fixed by the image
    // format: magic, secure_version, 8 reserved bytes, then the strings.
    private const int VersionOffset = 16;
    private const int ProjectNameOffset = 48;
    private const int TimeOffset = 80;
    private const int DateOffset = 96;
    private const int IdfVersionOffset = 112;

    /// <summary>Bytes of the file needed to read the whole descriptor.</summary>
    public const int HeaderBytes = AppDescriptorOffset + IdfVersionOffset + 32;

    public static bool LooksLikeApplicationImage(ReadOnlySpan<byte> head) =>
        head.Length >= AppDescriptorOffset + AppDescriptorMagic.Length
        && head[0] == 0xE9
        && head.Slice(AppDescriptorOffset, AppDescriptorMagic.Length).SequenceEqual(AppDescriptorMagic);

    /// <summary>
    /// Reads the application descriptor, or null if <paramref name="head"/>
    /// is not an application image or is truncated before the descriptor
    /// ends.
    /// </summary>
    public static FirmwareDescriptor? ReadDescriptor(ReadOnlySpan<byte> head)
    {
        if (!LooksLikeApplicationImage(head) || head.Length < HeaderBytes)
        {
            return null;
        }

        var descriptor = head[AppDescriptorOffset..];
        var time = ReadFixedString(descriptor, TimeOffset, 16);
        var date = ReadFixedString(descriptor, DateOffset, 16);
        return new FirmwareDescriptor(
            ReadFixedString(descriptor, VersionOffset, 32),
            ReadFixedString(descriptor, ProjectNameOffset, 32),
            string.Join(' ', new[] { date, time }.Where(s => s.Length > 0)),
            ReadFixedString(descriptor, IdfVersionOffset, 32));
    }

    /// <summary>Fixed-width NUL-terminated ASCII, as C writes it.</summary>
    private static string ReadFixedString(ReadOnlySpan<byte> source, int offset, int length)
    {
        var field = source.Slice(offset, length);
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]).Trim();
    }

    private static FirmwareDescriptor? ReadDescriptor(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[HeaderBytes];
            return ReadDescriptor(head.AsSpan(0, stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<FirmwareImage?> SaveAsync(string file, Stream content, CancellationToken cancellationToken)
    {
        if (!TrySanitize(file, out var path))
        {
            return null;
        }

        await using (var target = File.Create(path))
        {
            await content.CopyToAsync(target, cancellationToken);
        }

        // The handle must be closed before deleting: Windows refuses to
        // delete a file that is still open, where Unix allows it.
        var head = new byte[HeaderBytes];
        int read;
        await using (var written = File.OpenRead(path))
        {
            read = await written.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        }

        if (!LooksLikeApplicationImage(head.AsSpan(0, read)))
        {
            File.Delete(path);
            throw new InvalidFirmwareImageException(
                $"'{file}' is not an ESP-IDF application image. Upload the '-ota.bin' " +
                "file, not the merged '-flash.bin' used for USB flashing.");
        }

        var info = new FileInfo(path);
        return new FirmwareImage(
            info.Name, info.Length, ComputeSha256(path), info.LastWriteTimeUtc,
            ReadDescriptor(head.AsSpan(0, read)));
    }

    /// <summary>
    /// Accepts plain .bin file names only — no path separators, no
    /// traversal — since names come from HTTP clients.
    /// </summary>
    private bool TrySanitize(string file, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(file)
            || file != Path.GetFileName(file)
            || file.StartsWith('.')
            || !file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = Path.Combine(_directory, file);
        return true;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>Thrown when an upload cannot be installed over the air.</summary>
public sealed class InvalidFirmwareImageException : Exception
{
    public InvalidFirmwareImageException(string message) : base(message)
    {
    }
}
