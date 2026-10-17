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
        services.AddSingleton<OtpService>();
        services.AddSingleton<SessionService>();
        if (string.Equals(configuration[$"{IdentityOptions.Section}:{nameof(IdentityOptions.OtpSender)}"], "console", StringComparison.OrdinalIgnoreCase))
            services.TryAddSingleton<IOtpSender, ConsoleOtpSender>();
        services.TryAddSingleton<IOtpSender, UnconfiguredOtpSender>();

        services.AddAuthentication(RatableAuth.Scheme).AddScheme<AuthenticationSchemeOptions, RatableAuthHandler>(RatableAuth.Scheme, null);
        services.AddAuthorization(RatableAuth.AddPolicies);
        return services;
    }
}
