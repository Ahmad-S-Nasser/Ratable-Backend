using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // The external AI service registers its own IAiProvider before this call. Until then every call fails visibly.
        services.TryAddSingleton<IAiProvider, NoAiProvider>();

        // A single instance, because it tracks the cost of calls that are still running.
        services.TryAddSingleton<IAiClient, AiClient>();
        return services;
    }
}
