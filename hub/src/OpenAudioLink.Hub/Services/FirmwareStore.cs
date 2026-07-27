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
