using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratable.Modules.Identity.Auth;
using Ratable.Modules.Identity.Data;
using Ratable.Modules.Identity.Services;
using Ratable.Shared.Authors;
using Ratable.Shared.Data;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdentityOptions>(configuration.GetSection(IdentityOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<UserRepository>();
        services.AddSingleton<OtpChallengeRepository>();
        services.AddSingleton<SessionRepository>();
        services.AddIndexInitializer<IdentityIndexInitializer>();

        services.AddSingleton<PhoneProtector>();
        services.AddSingleton<IdentityDirectory>();
        services.AddSingleton<IPublicProfiles>(sp => sp.GetRequiredService<IdentityDirectory>());
        services.AddSingleton<IAccountDirectory>(sp => sp.GetRequiredService<IdentityDirectory>());
        services.AddSingleton<IAccountSignals, AccountSignalsService>();
        services.AddSingleton<Ratable.Shared.Businesses.IBusinessRoleGranter, BusinessRoleGranter>();
        services.AddSingleton<OtpService>();

        // Verify via WhatsApp: the reverse route is the default, the code route is the alternative. The number-type check and the code sender have no real provider yet.
        services.AddSingleton<PhoneVerification.PhoneVerificationRepository>();
        services.AddIndexInitializer<PhoneVerification.PhoneVerificationIndexInitializer>();
        services.AddSingleton<PhoneVerification.PhoneVerificationService>();
        services.TryAddSingleton<PhoneVerification.INumberTypeChecker, PhoneVerification.PrefixNumberTypeChecker>();
        services.TryAddSingleton<PhoneVerification.IVerificationCodeSender, PhoneVerification.UnconfiguredVerificationCodeSender>();
        services.AddSingleton<PhoneVerification.IPhoneVerifier, PhoneVerification.ReverseWhatsAppVerifier>();
        services.AddSingleton<PhoneVerification.IPhoneVerifier, PhoneVerification.CodeSenderVerifier>();

        // Google and Apple sign-in: a provider is on only when its client id is set.
        var google = configuration[$"{IdentityOptions.Section}:{nameof(IdentityOptions.GoogleClientId)}"];
        if (!string.IsNullOrWhiteSpace(google))
            services.AddSingleton<IExternalIdTokenVerifier>(sp => new OidcIdTokenVerifier("google", ["https://accounts.google.com", "accounts.google.com"], google,
                new Microsoft.IdentityModel.Protocols.ConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(
                    "https://accounts.google.com/.well-known/openid-configuration", new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfigurationRetriever()),
                sp.GetRequiredService<TimeProvider>()));
        var apple = configuration[$"{IdentityOptions.Section}:{nameof(IdentityOptions.AppleClientId)}"];
        if (!string.IsNullOrWhiteSpace(apple))
            services.AddSingleton<IExternalIdTokenVerifier>(sp => new OidcIdTokenVerifier("apple", ["https://appleid.apple.com"], apple,
                new Microsoft.IdentityModel.Protocols.ConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(
                    "https://appleid.apple.com/.well-known/openid-configuration", new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfigurationRetriever()),
                sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<SessionService>();
        if (string.Equals(configuration[$"{IdentityOptions.Section}:{nameof(IdentityOptions.OtpSender)}"], "console", StringComparison.OrdinalIgnoreCase))
            services.TryAddSingleton<IOtpSender, ConsoleOtpSender>();
        services.TryAddSingleton<IOtpSender, UnconfiguredOtpSender>();

        services.AddAuthentication(RatableAuth.Scheme).AddScheme<AuthenticationSchemeOptions, RatableAuthHandler>(RatableAuth.Scheme, null);
        services.AddAuthorization(RatableAuth.AddPolicies);
        return services;
    }
}
