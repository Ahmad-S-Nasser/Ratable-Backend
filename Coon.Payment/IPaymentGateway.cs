namespace Coon.Payment;

/// <summary>
/// One payment provider.
/// </summary>
/// <remarks>
/// Three operations, because three is all a hosted-checkout integration needs:
/// start a payment, read the server-to-server notification, and read the browser redirect.
///
/// Implementations must be thread-safe and are registered as singletons. In particular an
/// implementation must never mutate shared state per request - a gateway that stashed the
/// current order on a field, or set headers on a shared HttpClient, would leak one customer's
/// payment into another's under concurrency.
/// </remarks>
public interface IPaymentGateway
{
    /// <summary>Stable provider key: "kashier", "stripe". Persist this on the order.</summary>
    string Name { get; }

    /// <summary>
    /// Whether credentials are present and checkout can actually be started.
    /// </summary>
    /// <remarks>
    /// Exposed rather than left to a thrown exception because the calling system needs to make a
    /// product decision from it - offer a checkout button, or offer whatever fallback it has -
    /// and it needs to make that decision when rendering, not when the customer clicks.
    /// </remarks>
    bool IsConfigured { get; }

    /// <summary>
    /// Builds the redirect that starts a payment.
    /// </summary>
    /// <exception cref="PaymentConfigurationException">
    /// Thrown when <see cref="IsConfigured"/> is false. Callers should check first.
    /// </exception>
    Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct = default);

    /// <summary>
    /// Verifies and parses a server-to-server webhook. This is the one that grants things.
    /// </summary>
    /// <param name="headers">Request headers, case-insensitive.</param>
    /// <param name="rawBody">The body EXACTLY as received - unparsed, unmodified.</param>
    /// <remarks>
    /// The raw body matters: signatures are computed over bytes on the wire, and a body that has
    /// been through a JSON round-trip has had key order, whitespace and number formatting
    /// changed underneath it. Deserialize after verifying, never before.
    /// </remarks>
    PaymentNotification ReadNotification(IReadOnlyDictionary<string, string> headers, string rawBody);

    /// <summary>
    /// Verifies and parses the browser redirect the payer lands on.
    /// </summary>
    /// <remarks>
    /// For showing the payer a result page, and nothing more. Even when the signature verifies,
    /// fulfilment belongs on <see cref="ReadNotification"/>: this one is missing entirely if the
    /// payer closes the tab, and arrives out of order often enough to matter.
    /// </remarks>
    PaymentNotification ReadRedirect(IReadOnlyDictionary<string, string> query);
}
