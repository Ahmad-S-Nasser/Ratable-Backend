namespace Coon.Payment;

/// <summary>What a gateway says happened to a payment.</summary>
public enum PaymentStatus
{
    /// <summary>The gateway reported something this library does not recognise.</summary>
    Unknown = 0,

    /// <summary>Authorised and captured. This is the only value that should grant anything.</summary>
    Paid,

    /// <summary>Declined, cancelled, or abandoned. Terminal.</summary>
    Failed,

    /// <summary>Accepted but not settled - some methods clear asynchronously. Grant nothing yet.</summary>
    Pending,

    /// <summary>Money went back. Whatever was granted should be revoked.</summary>
    Refunded,
}

/// <summary>
/// A request to collect one payment.
/// </summary>
/// <remarks>
/// Note what is NOT here: no plan, no subscription, no line items, no tax engine. This library
/// collects a single amount against a single merchant reference, and that is the whole contract.
/// Everything about WHAT was bought stays in the calling system, carried through
/// <see cref="Metadata"/> and matched back by <see cref="OrderId"/>.
///
/// That restraint is what makes it reusable. The moment a payment library understands
/// "subscription" or "add-on" it belongs to one product and can only be reused by copy-paste.
/// </remarks>
public sealed class CheckoutRequest
{
    /// <summary>
    /// The calling system's own reference for this payment. Must be unique per attempt.
    /// </summary>
    /// <remarks>
    /// This is the join key for the entire flow: it goes out in the checkout, comes back in the
    /// webhook, and is how the caller finds the order to fulfil. Generate it before redirecting
    /// and store it - a payment whose reference was invented at redirect time and never
    /// persisted is a payment you cannot reconcile.
    /// </remarks>
    public required string OrderId { get; init; }

    /// <summary>Amount to charge, in major units (12.50 means twelve and a half pounds).</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 code, uppercase - "EGP", "USD".</summary>
    public required string Currency { get; init; }

    /// <summary>Shown to the payer on the gateway's page.</summary>
    public string? Description { get; init; }

    public string? CustomerEmail { get; init; }
    public string? CustomerName { get; init; }

    /// <summary>Where the payer lands after a successful payment.</summary>
    public string? SuccessUrl { get; init; }

    /// <summary>Where the payer lands after a failure. Falls back to SuccessUrl when unset.</summary>
    public string? FailureUrl { get; init; }

    /// <summary>
    /// Absolute URL the gateway should call server-to-server.
    /// </summary>
    /// <remarks>
    /// The redirect and the webhook are not interchangeable and the caller must fulfil on the
    /// webhook. A payer who closes the tab after paying never hits the redirect, and a redirect
    /// URL is trivially forged by typing it into a browser.
    /// </remarks>
    public string? WebhookUrl { get; init; }

    /// <summary>Two-letter display language hint, when the gateway supports one.</summary>
    public string? Language { get; init; }

    /// <summary>Opaque passthrough. Returned on the notification where the gateway supports it.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>Where to send the payer to pay.</summary>
public sealed class CheckoutSession
{
    public required string Provider { get; init; }

    /// <summary>Absolute URL to redirect the payer to.</summary>
    public required string RedirectUrl { get; init; }

    /// <summary>Echoed back so the caller can persist it alongside the redirect.</summary>
    public required string OrderId { get; init; }

    /// <summary>The gateway's own id for the session, when it issues one before payment.</summary>
    public string? ProviderReference { get; init; }
}

/// <summary>
/// A verified statement from the gateway about one payment.
/// </summary>
/// <remarks>
/// Produced by <see cref="IPaymentGateway.ReadNotification"/>, which verifies the signature
/// first. <see cref="IsAuthentic"/> false means the message did not come from the gateway and
/// must be discarded without touching any state - not logged as a failed payment, not used to
/// mark an order failed, discarded.
/// </remarks>
public sealed class PaymentNotification
{
    /// <summary>Whether the signature verified. Check this before anything else.</summary>
    public required bool IsAuthentic { get; init; }

    /// <summary>Why verification failed. Null when it succeeded.</summary>
    public string? Rejection { get; init; }

    public string? OrderId { get; init; }
    public PaymentStatus Status { get; init; }

    /// <summary>The gateway's transaction id, for support and reconciliation.</summary>
    public string? ProviderReference { get; init; }

    /// <summary>
    /// What the gateway says was actually charged.
    /// </summary>
    /// <remarks>
    /// The caller must compare this against the order it stored. A signature proves the message
    /// is authentic; it does not prove the amount is the one you asked for.
    /// </remarks>
    public decimal? Amount { get; init; }

    public string? Currency { get; init; }

    /// <summary>Everything the gateway sent, for the audit record.</summary>
    public IReadOnlyDictionary<string, string> Raw { get; init; } =
        new Dictionary<string, string>();

    public static PaymentNotification Reject(string reason) =>
        new() { IsAuthentic = false, Rejection = reason, Status = PaymentStatus.Unknown };
}

/// <summary>Raised when a gateway is asked to do something it is not configured for.</summary>
public class PaymentConfigurationException : Exception
{
    public PaymentConfigurationException(string message) : base(message) { }
}
