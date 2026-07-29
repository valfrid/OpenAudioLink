using System.Security.Cryptography;

namespace OpenAudioLink.Hub.Services;

public sealed record FirmwareImage(string File, long Size, string Sha256, DateTimeOffset ModifiedAt);

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

    public IReadOnlyList<FirmwareImage> List() =>
        new DirectoryInfo(_directory)
            .EnumerateFiles("*.bin")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new FirmwareImage(f.Name, f.Length, ComputeSha256(f.FullName), f.LastWriteTimeUtc))
            .ToList();

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

    public static bool LooksLikeApplicationImage(ReadOnlySpan<byte> head) =>
        head.Length >= AppDescriptorOffset + AppDescriptorMagic.Length
        && head[0] == 0xE9
        && head.Slice(AppDescriptorOffset, AppDescriptorMagic.Length).SequenceEqual(AppDescriptorMagic);

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

        var head = new byte[64];
        await using (var written = File.OpenRead(path))
        {
            int read = await written.ReadAsync(head, cancellationToken);
            if (!LooksLikeApplicationImage(head.AsSpan(0, read)))
            {
                File.Delete(path);
                throw new InvalidFirmwareImageException(
                    $"'{file}' is not an ESP-IDF application image. Upload the '-ota.bin' " +
                    "file, not the merged '-flash.bin' used for USB flashing.");
            }
        }

        var info = new FileInfo(path);
        return new FirmwareImage(info.Name, info.Length, ComputeSha256(path), info.LastWriteTimeUtc);
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
