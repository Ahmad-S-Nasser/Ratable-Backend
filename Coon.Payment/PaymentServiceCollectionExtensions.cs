using Coon.Payment.Kashier;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Coon.Payment;

/// <summary>Wires a payment provider from configuration.</summary>
public static class PaymentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the provider named by "Payments:Provider".
    /// </summary>
    /// <remarks>
    /// Provider selection is configuration, not code, so a second system reusing this library
    /// switches gateway by editing a setting. An unrecognised or absent value resolves to
    /// <see cref="UnconfiguredPaymentGateway"/> and logs it - loudly, because "payments quietly
    /// did nothing" is the failure mode worth spending a warning on.
    ///
    /// Registered as a singleton to match the host's lifetime conventions; every implementation
    /// in this library is stateless and thread-safe, which is a requirement of
    /// <see cref="IPaymentGateway"/> rather than an accident of these two.
    /// </remarks>
    public static IServiceCollection AddPayments(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KashierOptions>(configuration.GetSection(KashierOptions.SectionName));

        var provider = configuration["Payments:Provider"];

        services.TryAddSingleton<IPaymentGateway>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Coon.Payment");

            switch ((provider ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "kashier":
                    var gateway = new KashierGateway(
                        sp.GetRequiredService<IOptions<KashierOptions>>(),
                        sp.GetRequiredService<ILogger<KashierGateway>>());

                    if (!gateway.IsConfigured)
                    {
                        // Named as the provider but missing credentials. Deliberately NOT swapped
                        // for the null object: silently downgrading to "no payments" is how a
                        // production deploy with a typo'd key looks healthy while taking no money.
                        logger.LogError(
                            "Payments:Provider is 'kashier' but MerchantId or PaymentApiKey is missing. " +
                            "Checkout will refuse every request until they are set.");
                    }
                    else
                    {
                        logger.LogInformation(
                            "Payments: Kashier gateway active in {Mode} mode.",
                            sp.GetRequiredService<IOptions<KashierOptions>>().Value.IsLive ? "LIVE" : "test");
                    }

                    return gateway;

                case "":
                case "none":
                    logger.LogInformation(
                        "Payments: no provider configured. Checkout is unavailable; " +
                        "the host should fall back to whatever manual path it offers.");
                    return new UnconfiguredPaymentGateway();

                default:
                    logger.LogError(
                        "Payments: unknown provider '{Provider}'. Checkout is unavailable.", provider);
                    return new UnconfiguredPaymentGateway();
            }
        });

        return services;
    }
}
