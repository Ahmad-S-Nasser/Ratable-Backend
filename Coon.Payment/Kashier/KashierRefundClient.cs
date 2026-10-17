using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Coon.Payment.Kashier;

/// <summary>What Kashier (or the network) said about a refund call.</summary>
public enum KashierRefundStatus
{
    /// <summary>Processed.</summary>
    Success,
    /// <summary>Initiated; awaiting Kashier's processor. Money movement has started.</summary>
    Pending,
    /// <summary>Kashier did not process it; the original payment is untouched. <see cref="KashierRefundResult.Reason"/> says why.</summary>
    Failure,
    /// <summary>HTTP 401/403: the key is recognised but may not issue refunds (or is for the other mode).</summary>
    NotPermitted,
    /// <summary>No secret key configured; no request was sent.</summary>
    NotConfigured,
    /// <summary>Timeout, network error, or an answer that cannot be read as success or failure: the refund may or may not exist.</summary>
    Unknown
}

public sealed record KashierRefundResult(KashierRefundStatus Status, string? TransactionId = null, string? Reason = null, int? HttpStatus = null);

/// <summary>Issues a refund against a Kashier order. Never throws for provider/network problems; they are reported in the result.</summary>
public interface IKashierRefundClient
{
    /// <param name="merchantOrderId">The same id sent as the hosted-page orderId at checkout.</param>
    /// <param name="amount">Null = refund everything; otherwise a partial amount (rounded to 2 decimals).</param>
    Task<KashierRefundResult> RefundAsync(string merchantOrderId, decimal? amount, string? reason, CancellationToken ct = default);
}

/// <summary>
/// <c>PUT {base}/v3/orders/{orderId}</c> with <c>Authorization: &lt;secret key&gt;</c> (raw, not "Bearer"). Typed HttpClient; register it with
/// a short timeout. Deliberately NO retries: a refund PUT is not idempotent at Kashier, so a retry could refund twice. A
/// timeout or network failure after sending is therefore reported as <see cref="KashierRefundStatus.Unknown"/>, never as failure.
/// The success/pending response body is not documented, so parsing is tolerant (see <see cref="Parse"/>).
/// </summary>
public sealed class KashierRefundClient : IKashierRefundClient
{
    private const int MaxReasonLength = 300;

    private readonly HttpClient _http;
    private readonly KashierOptions _options;
    private readonly ILogger<KashierRefundClient> _logger;

    public KashierRefundClient(HttpClient http, IOptions<KashierOptions> options, ILogger<KashierRefundClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<KashierRefundResult> RefundAsync(string merchantOrderId, decimal? amount, string? reason, CancellationToken ct = default)
    {
        var secret = _options.SecretKey?.Trim();
        if (string.IsNullOrEmpty(secret))
            return new KashierRefundResult(KashierRefundStatus.NotConfigured);
        if (string.IsNullOrWhiteSpace(merchantOrderId))
            return new KashierRefundResult(KashierRefundStatus.Failure, Reason: "The order has no Kashier reference.");

        var url = _options.EffectiveRefundApiUrl.TrimEnd('/') + "/v3/orders/" + Uri.EscapeDataString(merchantOrderId);
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(BuildBody(amount, reason), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", secret);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = Parse((int)response.StatusCode, body, secret);
            _logger.LogInformation("Kashier refund for {OrderId}: HTTP {StatusCode}, outcome {Outcome}.",
                merchantOrderId, (int)response.StatusCode, result.Status);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // The request may have been sent and processed; we simply never heard back.
            _logger.LogWarning("Kashier refund for {OrderId}: no answer ({Error}); the outcome is unknown.", merchantOrderId, ex.GetType().Name);
            return new KashierRefundResult(KashierRefundStatus.Unknown);
        }
    }

    /// <summary>The JSON body. The amount is a JSON number (invariant culture, at most 2 decimals, no trailing zeros); omitted = full refund.</summary>
    public static string BuildBody(decimal? amount, string? reason)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("apiOperation", "REFUND");
            if (!string.IsNullOrWhiteSpace(reason))
            {
                var r = reason.Trim();
                w.WriteString("reason", r.Length > 250 ? r[..250] : r);
            }
            if (amount is { } a)
            {
                w.WriteStartObject("transaction");
                // Dividing by 1.000...0 (28 places) strips trailing zeros: 150.00 -> 150, 12.50 -> 12.5.
                w.WriteNumber("amount", decimal.Round(a, 2, MidpointRounding.AwayFromZero) / 1.0000000000000000000000000000m);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Tolerant reading of Kashier's answer. Status: top-level <c>status</c>, else <c>response.status</c>, else <c>data.status</c>.
    /// A 2xx whose status cannot be read, and a 5xx that does not explicitly say FAILURE, are <see cref="KashierRefundStatus.Unknown"/>
    /// (the money may have moved); any other unreadable error is a failure. Public for direct unit testing.
    /// </summary>
    public static KashierRefundResult Parse(int httpStatus, string? body, string secret)
    {
        if (httpStatus is 401 or 403)
            return new KashierRefundResult(KashierRefundStatus.NotPermitted, HttpStatus: httpStatus);

        JsonElement root = default;
        var hasJson = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                root = doc.RootElement.Clone();
                hasJson = root.ValueKind == JsonValueKind.Object;
            }
        }
        catch (JsonException) { /* handled below: treated like an empty body */ }

        string? status = null, id = null, reason = null;
        if (hasJson)
        {
            status = FirstString(root, "status") ?? FirstString(root, "response", "status") ?? FirstString(root, "data", "status");
            id = FirstString(root, "transactionId") ?? FirstString(root, "response", "transactionId")
                 ?? FirstString(root, "data", "transactionId") ?? FirstString(root, "id");
            reason = FirstString(root, "messages", "en") ?? FirstString(root, "error", "explanation")
                     ?? FirstString(root, "error", "cause") ?? FirstString(root, "message");
        }
        reason = Sanitize(reason, secret);
        var ok = httpStatus is >= 200 and < 300;

        if (ok && string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            return new KashierRefundResult(KashierRefundStatus.Success, id, HttpStatus: httpStatus);
        if (ok && string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase))
            return new KashierRefundResult(KashierRefundStatus.Pending, id, HttpStatus: httpStatus);
        if (string.Equals(status, "FAILURE", StringComparison.OrdinalIgnoreCase))
            return new KashierRefundResult(KashierRefundStatus.Failure, Reason: reason ?? "Kashier did not process the refund.", HttpStatus: httpStatus);

        if (ok || httpStatus >= 500)
            return new KashierRefundResult(KashierRefundStatus.Unknown, Reason: reason, HttpStatus: httpStatus);
        return new KashierRefundResult(KashierRefundStatus.Failure,
            Reason: reason ?? $"Kashier answered HTTP {httpStatus} without a readable reason.", HttpStatus: httpStatus);
    }

    private static string? FirstString(JsonElement root, params string[] path)
    {
        var el = root;
        foreach (var key in path)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out el)) return null;
        }
        return el.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(el.GetString()) => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null
        };
    }

    /// <summary>Single-line, control characters removed, the secret masked, length-capped: safe to show an admin.</summary>
    private static string? Sanitize(string? text, string secret)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text;
        if (!string.IsNullOrEmpty(secret)) t = t.Replace(secret, "***", StringComparison.OrdinalIgnoreCase);
        t = Regex.Replace(t, @"[\p{Cc}\s]+", " ").Trim();
        if (t.Length > MaxReasonLength) t = t[..MaxReasonLength].TrimEnd() + "...";
        return t.Length == 0 ? null : t;
    }
}
