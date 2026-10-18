using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ratable.Modules.Ai.Data;
using Ratable.Modules.Ai.Providers;
using Ratable.Shared.Ai;
using Ratable.Shared.Data;

namespace Ratable.Modules.Ai;

public static class AiModule
{
    public static IServiceCollection AddAiModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<AiCallRepository>();
        services.AddSingleton<AiCacheRepository>();
        services.AddIndexInitializer<AiIndexInitializer>();

        // The Ratable AI microservice, when RatableAi:BaseUrl and RatableAi:ServiceKey (environment) are set.
        services.Configure<RatableAiOptions>(configuration.GetSection(RatableAiOptions.Section));
        var aiService = configuration.GetSection(RatableAiOptions.Section).Get<RatableAiOptions>() ?? new RatableAiOptions();
        if (aiService.Configured)
        {
            services.AddHttpClient<IAiProvider, RatableAiProvider>((sp, client) => RatableAiProvider.Configure(client, sp.GetRequiredService<IOptions<RatableAiOptions>>().Value))
                .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<IHostEnvironment>().IsDevelopment() && aiService.AllowUntrustedCertInDev
                    ? new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator }
                    : new HttpClientHandler());
        }

        // Any other IAiProvider registered before this call wins. Until one exists every call fails visibly.
        services.TryAddSingleton<IAiProvider, NoAiProvider>();

        // A single instance, because it tracks the cost of calls that are still running.
        services.TryAddSingleton<IAiClient, AiClient>();
        return services;
    }
}
