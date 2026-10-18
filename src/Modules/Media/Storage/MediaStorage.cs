using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ratable.Modules.Media.Services;

namespace Ratable.Modules.Media.Storage;

/// <summary>
/// Private storage for uploads and processed media. Nothing here is served directly: the API streams a file only while its item is approved and its
/// review is public (or to a moderator, with an audit entry). Every item has its own random folder; no file name from the user is ever used.
/// </summary>
public interface IMediaStorage
{
    string NewKey();

    /// <summary>The folder of an item, for tools such as ffmpeg that need a real path. Created if it does not exist.</summary>
    string WorkDirectory(string key);

    /// <summary>Adds bytes to the incoming upload at the given offset. The offset must equal the current length (no gaps, no overwrites). Returns the new length.</summary>
    Task<long> AppendUploadAsync(string key, long offset, Stream content, long maxBytes, CancellationToken cancellationToken = default);

    long UploadLength(string key);

    /// <summary>Opens a file inside the item's folder (a relative path made of safe names), or null when it is not there.</summary>
    Stream? OpenRead(string key, string relativePath);

    long? FileLength(string key, string relativePath);

    Task DeleteFileAsync(string key, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Deletes the whole folder. Deleting what is already gone is fine.</summary>
    Task DeleteAllAsync(string key, CancellationToken cancellationToken = default);
}

public sealed partial class LocalDiskMediaStorage(IOptions<MediaOptions> options) : IMediaStorage
{
    public const string UploadFile = "upload.part";

    [GeneratedRegex("^[a-f0-9]{32}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9]+)?(/[A-Za-z0-9_\-]+(\.[A-Za-z0-9]+)?)*$")]
    private static partial Regex RelativePattern();

    public string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private string Folder(string key)
    {
        if (!KeyPattern().IsMatch(key)) throw new ArgumentException("Invalid storage key.", nameof(key));
        return Path.Combine(Path.GetFullPath(options.Value.StoragePath), key[..2], key);
    }

    private string FilePath(string key, string relativePath)
    {
        if (!RelativePattern().IsMatch(relativePath) || relativePath.Contains("..")) throw new ArgumentException("Invalid file path.", nameof(relativePath));
        var folder = Folder(key);
        var full = Path.GetFullPath(Path.Combine(folder, relativePath));
        if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("Invalid file path.", nameof(relativePath));
        return full;
    }

    public string WorkDirectory(string key)
    {
        var folder = Folder(key);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public async Task<long> AppendUploadAsync(string key, long offset, Stream content, long maxBytes, CancellationToken ct = default)
    {
        var path = FilePath(key, UploadFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var current = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (offset != current) throw new InvalidOperationException("offset_mismatch");

        await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            written += read;
            if (current + written > maxBytes) throw new InvalidOperationException("too_large");
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return current + written;
    }

    public long UploadLength(string key)
    {
        var path = FilePath(key, UploadFile);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public Stream? OpenRead(string key, string relativePath)
    {
        var path = FilePath(key, relativePath);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public long? FileLength(string key, string relativePath)
    {
        var path = FilePath(key, relativePath);
        return File.Exists(path) ? new FileInfo(path).Length : null;
    }

    public Task DeleteFileAsync(string key, string relativePath, CancellationToken ct = default)
    {
        var path = FilePath(key, relativePath);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task DeleteAllAsync(string key, CancellationToken ct = default)
    {
        var folder = Folder(key);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        return Task.CompletedTask;
    }
}
