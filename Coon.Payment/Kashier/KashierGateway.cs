using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Coon.Payment.Kashier;

/// <summary>
/// Kashier, via its hosted payment page.
/// </summary>
/// <remarks>
/// Hosted checkout rather than an embedded card form, deliberately: the card number then never
/// touches this server, which is the difference between a PCI SAQ-A questionnaire and a far
/// heavier one. Starting a payment is therefore a pure URL construction with no outbound call,
/// which is also why every method here is synchronous underneath.
///
/// Stateless and thread-safe. Nothing is cached and no field is written after construction.
/// </remarks>
public sealed class KashierGateway : IPaymentGateway
{
    /// <summary>Header Kashier signs its webhooks with.</summary>
    private const string SignatureHeader = "x-kashier-signature";

    private readonly KashierOptions _options;
    private readonly ILogger<KashierGateway> _logger;

    public KashierGateway(IOptions<KashierOptions> options, ILogger<KashierGateway> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "kashier";

    public bool IsConfigured => _options.IsConfigured;

    public Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new PaymentConfigurationException(
                "Kashier is not configured. Set Payments:Kashier:MerchantId and PaymentApiKey.");
        }

        if (string.IsNullOrWhiteSpace(request.OrderId))
        {
            throw new ArgumentException("OrderId is required.", nameof(request));
        }

        // A zero or negative charge is a bug in the caller, not a free order. Sending it would
        // either be rejected by the gateway or - worse - accepted and settled as nothing while
        // the calling system waits for a webhook that never comes.
        if (request.Amount <= 0)
        {
            throw new ArgumentException("Amount must be greater than zero.", nameof(request));
        }

        var currency = (string.IsNullOrWhiteSpace(request.Currency)
            ? _options.DefaultCurrency
            : request.Currency).ToUpperInvariant();

        var hash = KashierSignature.ForCheckout(
            _options.MerchantId!, request.OrderId, request.Amount, currency, _options.PaymentApiKey!);

        var query = new Dictionary<string, string?>
        {
            ["merchantId"] = _options.MerchantId,
            ["orderId"] = request.OrderId,
            ["amount"] = KashierSignature.FormatAmount(request.Amount),
            ["currency"] = currency,
            ["hash"] = hash,
            ["mode"] = _options.IsLive ? "live" : "test",
            ["display"] = string.IsNullOrWhiteSpace(request.Language)
                ? _options.DisplayLanguage
                : request.Language,
            ["merchantRedirect"] = request.SuccessUrl,
            ["failureRedirect"] = request.FailureUrl ?? request.SuccessUrl,
            ["serverWebhook"] = request.WebhookUrl,
            ["customerEmail"] = request.CustomerEmail,
            ["description"] = request.Description,
        };

        // Metadata is passed as JSON in a single field. Kept small on purpose: the join key is
        // OrderId, and anything important enough to fulfil on must already be in the caller's own
        // record rather than round-tripping through the payer's browser.
        if (request.Metadata is { Count: > 0 })
        {
            query["metaData"] = JsonSerializer.Serialize(request.Metadata);
        }

        var url = Build(_options.CheckoutUrl, query);

        _logger.LogInformation(
            "Kashier checkout built for order {OrderId} ({Amount} {Currency}, {Mode} mode).",
            request.OrderId, KashierSignature.FormatAmount(request.Amount), currency,
            _options.IsLive ? "live" : "test");

        return Task.FromResult(new CheckoutSession
        {
            Provider = Name,
            OrderId = request.OrderId,
            RedirectUrl = url,
        });
    }

    public PaymentNotification ReadNotification(IReadOnlyDictionary<string, string> headers, string rawBody)
    {
        var secret = _options.EffectiveWebhookSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            // Fail closed. An unconfigured verifier that returned "authentic" would turn the
            // webhook into an unauthenticated endpoint that grants paid features on request.
            return PaymentNotification.Reject("Kashier webhook secret is not configured.");
        }

        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return PaymentNotification.Reject("Empty webhook body.");
        }

        var received = Header(headers, SignatureHeader);
        if (string.IsNullOrWhiteSpace(received))
        {
            return PaymentNotification.Reject($"Missing {SignatureHeader} header.");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(rawBody).RootElement;
        }
        catch (JsonException ex)
        {
            return PaymentNotification.Reject($"Webhook body is not valid JSON: {ex.Message}");
        }

        // The payload nests the payment under "data" and carries the signed field list beside it.
        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            ? d
            : root;

        var values = Flatten(data);

        var signedKeys = ReadSignatureKeys(root, data);
        if (signedKeys.Count == 0)
        {
            return PaymentNotification.Reject("Webhook did not declare which fields are signed.");
        }

        var expected = KashierSignature.ForNotification(signedKeys, values, secret!);

        if (!KashierSignature.Matches(expected, received))
        {
            // The signature is not logged. It is the only secret-derived value in the request,
            // and logs are read by more people and systems than the payment path is.
            _logger.LogWarning(
                "Rejected a Kashier webhook with a bad signature for order {OrderId}.",
                Value(values, "merchantOrderId", "orderId") ?? "(none)");

            return PaymentNotification.Reject("Signature mismatch.");
        }

        return new PaymentNotification
        {
            IsAuthentic = true,
            OrderId = Value(values, "merchantOrderId", "orderId"),
            ProviderReference = Value(values, "transactionId", "kashierOrderId", "orderReference"),
            Status = MapStatus(Value(values, "status", "orderStatus", "paymentStatus")),
            Amount = ParseAmount(Value(values, "amount", "totalAmount")),
            Currency = Value(values, "currency"),
            Raw = values,
        };
    }

    public PaymentNotification ReadRedirect(IReadOnlyDictionary<string, string> query)
    {
        var secret = _options.EffectiveWebhookSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            return PaymentNotification.Reject("Kashier webhook secret is not configured.");
        }

        var received = Value(query, "signature", "kashierSignature");
        if (string.IsNullOrWhiteSpace(received))
        {
            return PaymentNotification.Reject("Redirect carried no signature.");
        }

        // On the redirect Kashier names the signed fields in a comma-separated query parameter
        // rather than a JSON array.
        var signedKeys = (Value(query, "signatureKeys") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (signedKeys.Count == 0)
        {
            return PaymentNotification.Reject("Redirect did not declare which fields are signed.");
        }

        var expected = KashierSignature.ForNotification(signedKeys, query, secret!);

        if (!KashierSignature.Matches(expected, received))
        {
            return PaymentNotification.Reject("Signature mismatch.");
        }

        return new PaymentNotification
        {
            IsAuthentic = true,
            OrderId = Value(query, "merchantOrderId", "orderId"),
            ProviderReference = Value(query, "transactionId", "kashierOrderId"),
            Status = MapStatus(Value(query, "paymentStatus", "status", "orderStatus")),
            Amount = ParseAmount(Value(query, "amount")),
            Currency = Value(query, "currency"),
            Raw = new Dictionary<string, string>(query, StringComparer.OrdinalIgnoreCase),
        };
    }

    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Maps the provider's wording onto <see cref="PaymentStatus"/>.
    /// </summary>
    /// <remarks>
    /// Anything unrecognised becomes <see cref="PaymentStatus.Unknown"/>, never Paid. A status
    /// vocabulary can gain a value in a provider release, and the safe reading of a word we do
    /// not know is "do not grant anything".
    /// </remarks>
    private static PaymentStatus MapStatus(string? status) =>
        (status ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "SUCCESS" or "PAID" or "CAPTURED" or "SUCCEEDED" or "APPROVED" => PaymentStatus.Paid,
            "FAILURE" or "FAILED" or "DECLINED" or "CANCELLED" or "CANCELED" or "EXPIRED" => PaymentStatus.Failed,
            "PENDING" or "INITIATED" or "PROCESSING" => PaymentStatus.Pending,
            "REFUNDED" or "REFUND" or "VOIDED" or "CHARGEBACK" => PaymentStatus.Refunded,
            _ => PaymentStatus.Unknown,
        };

    private static decimal? ParseAmount(string? raw) =>
        decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>The signed field list, from the JSON array the payload carries.</summary>
    private static List<string> ReadSignatureKeys(JsonElement root, JsonElement data)
    {
        foreach (var source in new[] { root, data })
        {
            if (source.ValueKind == JsonValueKind.Object &&
                source.TryGetProperty("signatureKeys", out var keys) &&
                keys.ValueKind == JsonValueKind.Array)
            {
                return keys.EnumerateArray()
                    .Where(k => k.ValueKind == JsonValueKind.String)
                    .Select(k => k.GetString()!)
                    .ToList();
            }
        }

        return new List<string>();
    }

    /// <summary>
    /// One level of the payload as strings.
    /// </summary>
    /// <remarks>
    /// Values are taken as raw JSON text for numbers rather than parsed and re-rendered, because
    /// they are about to be re-signed: parsing 100.00 into a decimal and formatting it back
    /// yields "100.00" here and "100" elsewhere, and either way it is no longer the bytes that
    /// were signed. Nested objects are skipped - Kashier does not sign over them.
    /// </remarks>
    private static Dictionary<string, string> Flatten(JsonElement element)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object) return map;

        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                JsonValueKind.Object or JsonValueKind.Array => property.Value.GetRawText(),
                _ => property.Value.GetRawText(),
            };
        }

        return map;
    }

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : null;

    private static string? Value(IReadOnlyDictionary<string, string> map, params string[] names)
    {
        foreach (var name in names)
        {
            if (map.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string Build(string baseUrl, Dictionary<string, string?> query)
    {
        var pairs = query
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}");

        var separator = baseUrl.Contains('?') ? "&" : "?";
        return baseUrl.TrimEnd('&', '?') + separator + string.Join("&", pairs);
    }
}
