using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ratable.Modules.Verification.Services;

namespace Ratable.Modules.Verification.Storage;

/// <summary>
/// Private storage for proof files (CLAUDE.md rule 5). Nothing here is ever served to the public: only the moderator endpoint, which writes the
/// audit log first, reads a file. Production can swap this for object storage behind the same interface; the local-disk version is for development
/// and small installs.
/// </summary>
public interface IProofStorage
{
    Task SaveAsync(string key, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>The decrypted file, or null when it is not there (never stored, or already deleted).</summary>
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes the file. Deleting a file that is already gone is fine.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Files on local disk, outside any web root, encrypted at rest with AES-256-GCM (key from Verification__StorageKey, never in the repository).
/// File names are random keys, never anything the user sent.
/// </summary>
public sealed partial class LocalDiskProofStorage(IOptions<VerificationOptions> options) : IProofStorage
{
    [GeneratedRegex("^[a-f0-9]{32}$")]
    private static partial Regex KeyPattern();

    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private string PathFor(string key)
    {
        if (!KeyPattern().IsMatch(key)) throw new ArgumentException("Invalid storage key.", nameof(key)); // no path can be smuggled in
        var root = Path.GetFullPath(options.Value.StoragePath);
        return Path.Combine(root, key[..2], key);
    }

    private byte[] Key
    {
        get
        {
            var value = options.Value.StorageKey;
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Verification:StorageKey is not set. Set the Verification__StorageKey environment variable to 32 random bytes, base64.");
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length != 32) throw new InvalidOperationException("Verification:StorageKey must be exactly 32 bytes.");
            return bytes;
        }
    }

    public async Task SaveAsync(string key, byte[] content, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[content.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(nonce, content, cipher, tag);
        await File.WriteAllBytesAsync(path, [.. nonce, .. tag, .. cipher], ct);
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        var all = await File.ReadAllBytesAsync(path, ct);
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
        return plain;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(PathFor(key)));
}

/// <summary>Checks what a file really is from its first bytes, not from the name or the type the client claimed.</summary>
public static class ProofFileInspector
{
    /// <summary>The content type for a PDF, PNG, JPEG or WebP file; null for anything else.</summary>
    public static string? ContentTypeOf(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return null;
        if (data[..5].SequenceEqual("%PDF-"u8)) return "application/pdf";
        if (data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
