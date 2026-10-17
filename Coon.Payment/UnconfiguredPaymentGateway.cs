namespace Coon.Payment;

/// <summary>
/// The gateway used when no provider is configured.
/// </summary>
/// <remarks>
/// A null object rather than a null reference, so that a system with no payment credentials
/// still resolves <see cref="IPaymentGateway"/> and still starts. That matters more than it
/// sounds: the alternative is either a nullable dependency that every call site has to remember
/// to check, or a startup failure that makes payment configuration a prerequisite for running
/// the app at all - including in development, and including for the parts of it that have
/// nothing to do with money.
///
/// It reports <see cref="IsConfigured"/> false and refuses to invent a checkout. Every method
/// fails closed: the two verifiers reject rather than accept, so the absence of a provider can
/// never be mistaken for a valid payment.
/// </remarks>
public sealed class UnconfiguredPaymentGateway : IPaymentGateway
{
    public string Name => "none";

    public bool IsConfigured => false;

    public Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct = default) =>
        throw new PaymentConfigurationException(
            "No payment provider is configured. Set Payments:Provider and the matching credentials.");

    public PaymentNotification ReadNotification(IReadOnlyDictionary<string, string> headers, string rawBody) =>
        PaymentNotification.Reject("No payment provider is configured.");

    public PaymentNotification ReadRedirect(IReadOnlyDictionary<string, string> query) =>
        PaymentNotification.Reject("No payment provider is configured.");
}
