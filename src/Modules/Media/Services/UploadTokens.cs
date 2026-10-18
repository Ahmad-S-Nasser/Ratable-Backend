using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Ratable.Modules.Media.Services;

/// <summary>
/// The signed address of an upload: it names one item and an expiry, and is signed with Media:SigningKey. Whoever holds it can add bytes to that one
/// upload until it expires, nothing else, so the file can go straight to storage without the session cookie (the same idea as a signed object-store URL).
/// </summary>
public sealed class UploadTokens(IOptions<MediaOptions> options, TimeProvider time)
{
    private byte[] Key
    {
        get
        {
            var value = options.Value.SigningKey;
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Media:SigningKey is not set. Set the Media__SigningKey environment variable to 32 random bytes, base64.");
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length != 32) throw new InvalidOperationException("Media:SigningKey must be exactly 32 bytes.");
            return bytes;
        }
    }

    public string Create(string itemId, DateTime expiresAtUtc)
    {
        var payload = $"{itemId}.{new DateTimeOffset(expiresAtUtc).ToUnixTimeSeconds()}";
        return $"{Base64Url(Encoding.UTF8.GetBytes(payload))}.{Base64Url(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(payload)))}";
    }

    /// <summary>The item id when the token is genuine and not expired; otherwise null.</summary>
    public string? Validate(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        try
        {
            var payload = Encoding.UTF8.GetString(FromBase64Url(parts[0]));
            var expected = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(payload));
            if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[1]))) return null;
            var pieces = payload.Split('.');
            if (pieces.Length != 2 || !long.TryParse(pieces[1], out var expires)) return null;
            return time.GetUtcNow().ToUnixTimeSeconds() <= expires ? pieces[0] : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
