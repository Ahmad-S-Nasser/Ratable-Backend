using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Ratable.Modules.Identity.Services;

/// <summary>Encrypts phone numbers for storage and makes the keyed one-way hashes used for lookups and limits (rule 18).</summary>
public sealed class PhoneProtector(IOptions<IdentityOptions> options)
{
    private byte[] EncryptionKey => ReadKey(options.Value.PhoneEncryptionKey, 32, nameof(IdentityOptions.PhoneEncryptionKey), exact: true);
    private byte[] HashKey => ReadKey(options.Value.PhoneHashKey, 32, nameof(IdentityOptions.PhoneHashKey), exact: false);

    /// <summary>Keyed one-way hash (HMAC-SHA256, hex). The same input always gives the same hash, so it can be indexed.</summary>
    public string Hash(string value) => Convert.ToHexString(HMACSHA256.HashData(HashKey, Encoding.UTF8.GetBytes(value)));

    public string Encrypt(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(EncryptionKey, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Decrypt(string stored)
    {
        var all = Convert.FromBase64String(stored);
        var nonce = all[..12];
        var tag = all[12..28];
        var cipher = all[28..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(EncryptionKey, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] ReadKey(string value, int length, string name, bool exact)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Identity:{name} is not set. Set the Identity__{name} environment variable to a base64 key.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException) { throw new InvalidOperationException($"Identity:{name} is not valid base64."); }
        if (exact ? bytes.Length != length : bytes.Length < length)
            throw new InvalidOperationException($"Identity:{name} must be {(exact ? "exactly" : "at least")} {length} bytes.");
        return bytes;
    }
}
