using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratable.Modules.Ai.Data;
using Ratable.Modules.Ai.Providers;
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

        // One real adapter, chosen by configuration. Without one, every call fails visibly.
        if (string.Equals(configuration[$"{AiOptions.Section}:{nameof(AiOptions.Provider)}"], "anthropic", StringComparison.OrdinalIgnoreCase))
            services.TryAddSingleton<IAiProvider, AnthropicProvider>();
        services.TryAddSingleton<IAiProvider, NoAiProvider>();

        // A single instance, because it tracks the cost of calls that are still running.
        services.TryAddSingleton<IAiClient, AiClient>();
        return services;
    }
}
