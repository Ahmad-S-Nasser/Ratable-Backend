namespace Coon.Payment.Kashier;

/// <summary>
/// Credentials and endpoints for one Kashier merchant account.
/// </summary>
/// <remarks>
/// Bound from configuration under "Payments:Kashier". Keep the keys OUT of appsettings.json in
/// source control - user-secrets in development, environment variables in production. A payment
/// API key in a repository is a key that has to be rotated the day someone clones it.
/// </remarks>
public sealed class KashierOptions
{
    public const string SectionName = "Payments:Kashier";

    /// <summary>Merchant id from the Kashier dashboard, in the form "MID-xxxx-xxx".</summary>
    public string? MerchantId { get; set; }

    /// <summary>
    /// The Payment API key, used to sign the checkout hash.
    /// </summary>
    /// <remarks>
    /// Kashier issues more than one credential and they are not interchangeable. This is the one
    /// labelled for the payment/iframe integration, not the account's secret key.
    /// </remarks>
    public string? PaymentApiKey { get; set; }

    /// <summary>
    /// Key used to verify webhook signatures. Falls back to <see cref="PaymentApiKey"/>.
    /// </summary>
    /// <remarks>
    /// Separate setting because it is separately rotatable, and because an account configured to
    /// sign webhooks with a distinct secret would otherwise silently fail every verification.
    /// </remarks>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Optional shared-secret gate for the webhook: the name of a custom header (e.g. "X-Webhook-Token") that the Kashier
    /// dashboard webhook is configured to send. Set together with <see cref="WebhookHeaderValue"/>; setting only one of
    /// the two fails closed (every delivery is rejected). Values come from user-secrets/env, never from git.
    /// </summary>
    public string? WebhookHeaderName { get; set; }

    /// <summary>The secret the <see cref="WebhookHeaderName"/> header must carry. Never logged.</summary>
    public string? WebhookHeaderValue { get; set; }

    /// <summary>
    /// The account's SECRET key (Kashier dashboard, API keys), used only to issue refunds
    /// (<c>Authorization: &lt;secret&gt;</c>, raw, no "Bearer"). Not interchangeable with <see cref="PaymentApiKey"/>.
    /// Its role must carry the refund permission and it must match <see cref="Mode"/>. Blank = automatic refunds are not
    /// configured. Never logged, never returned in an error. user-secrets / environment variable only.
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>Optional override of the refund API base URL; default depends on <see cref="Mode"/>.</summary>
    public string? RefundApiUrl { get; set; }

    /// <summary>"test" or "live". Anything other than "live" is treated as test.</summary>
    public string Mode { get; set; } = "test";

    /// <summary>Hosted checkout base URL. Overridable so a sandbox host can be pointed at.</summary>
    public string CheckoutUrl { get; set; } = "https://checkout.kashier.io/";

    /// <summary>Default ISO 4217 currency when a request does not name one.</summary>
    public string DefaultCurrency { get; set; } = "EGP";

    /// <summary>Display language passed to the hosted page: "en" or "ar".</summary>
    public string DisplayLanguage { get; set; } = "en";

    public bool IsLive => string.Equals(Mode, "live", StringComparison.OrdinalIgnoreCase);

    /// <summary>Everything needed to start a payment is present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(MerchantId) &&
        !string.IsNullOrWhiteSpace(PaymentApiKey) &&
        !string.IsNullOrWhiteSpace(CheckoutUrl);

    /// <summary>The refund API base URL: the override, else <c>https://fep.kashier.io/</c> (live) or <c>https://test-fep.kashier.io/</c> (test).</summary>
    public string EffectiveRefundApiUrl =>
        !string.IsNullOrWhiteSpace(RefundApiUrl) ? RefundApiUrl.Trim()
        : IsLive ? "https://fep.kashier.io/" : "https://test-fep.kashier.io/";

    /// <summary>The key webhook signatures are verified with.</summary>
    public string? EffectiveWebhookSecret =>
        string.IsNullOrWhiteSpace(WebhookSecret) ? PaymentApiKey : WebhookSecret;
}
