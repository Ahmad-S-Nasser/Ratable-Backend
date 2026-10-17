using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Coon.Payment.Kashier;

/// <summary>
/// Every byte Kashier signs or verifies, in one place.
/// </summary>
/// <remarks>
/// ---------------------------------------------------------------------------------------------
/// VERIFY THE TWO FORMATS BELOW AGAINST YOUR MERCHANT DOCUMENTATION BEFORE GOING LIVE.
///
/// They follow Kashier's published hosted-payment-page integration, but signing formats are the
/// one part of a gateway integration that cannot be validated without real credentials, and a
/// wrong byte fails closed and identically to a wrong key. Both formats are single expressions
/// with nothing else depending on their shape, so a correction is a one-line change here and
/// nowhere else. That isolation is the entire reason this class exists.
///
/// Test both paths in Kashier's TEST mode before switching Mode to "live".
/// ---------------------------------------------------------------------------------------------
///
/// Amounts are formatted with <see cref="CultureInfo.InvariantCulture"/> throughout. On an
/// Arabic or European system locale the default formatter produces a decimal comma, which
/// changes the signed bytes and fails every payment on the server but not on the developer's
/// machine.
/// </remarks>
public static class KashierSignature
{
    /// <summary>Amount format used both in the signature and on the wire. They must agree.</summary>
    public static string FormatAmount(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// The checkout hash: HMAC-SHA256 of "/?payment={mid}.{order}.{amount}.{currency}".
    /// </summary>
    public static string ForCheckout(string merchantId, string orderId, decimal amount, string currency, string apiKey)
    {
        var path = $"/?payment={merchantId}.{orderId}.{FormatAmount(amount)}.{currency}";
        return HmacSha256Hex(path, apiKey);
    }

    /// <summary>
    /// The notification signature: HMAC-SHA256 of the named fields, sorted alphabetically, as
    /// "k=v&amp;k=v" with each value URL-encoded.
    /// </summary>
    /// <remarks>
    /// Confirmed directly against Kashier's published webhook documentation
    /// (developers.kashier.io/payment/webhook/): "order only elements of the signatureKeys array
    /// in data payload alphabetically... generate signature payload by URL-encoding the selected
    /// key-value pairs." An earlier version of this method signed the fields in payload order,
    /// unencoded, on the mistaken assumption that order was itself part of the signed message -
    /// every real webhook would have failed verification.
    /// </remarks>
    /// <param name="orderedKeys">
    /// Field names from the payload's own signatureKeys list - which fields are signed comes from
    /// the payload rather than a list hardcoded here, so that a provider-side change to which
    /// fields are signed does not silently break verification. The order they arrive in does not
    /// matter: they are sorted here regardless, per Kashier's documented behaviour.
    /// </param>
    public static string ForNotification(
        IEnumerable<string> orderedKeys,
        IReadOnlyDictionary<string, string> values,
        string secret)
    {
        var message = string.Join("&", orderedKeys
            .Where(k => !string.IsNullOrEmpty(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => $"{k}={Uri.EscapeDataString(values.TryGetValue(k, out var v) ? v : string.Empty)}"));

        return HmacSha256Hex(message, secret);
    }

    /// <summary>Lowercase hex HMAC-SHA256.</summary>
    public static string HmacSha256Hex(string message, string key)
    {
        using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = mac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Constant-time comparison of two hex signatures.
    /// </summary>
    /// <remarks>
    /// Ordinary string equality returns as soon as two characters differ, and that timing
    /// difference is enough to recover a signature byte by byte given enough attempts. Length is
    /// compared first and separately because <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// requires equal spans - that leaks only the length, which is fixed and public anyway.
    /// </remarks>
    public static bool Matches(string? expected, string? received)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(received)) return false;
        if (expected.Length != received.Length) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(received.ToLowerInvariant()));
    }
}
