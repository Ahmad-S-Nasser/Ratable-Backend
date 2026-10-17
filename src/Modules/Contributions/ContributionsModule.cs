using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Modules.Contributions.Data;
using Ratable.Shared.Data;

namespace Ratable.Modules.Contributions;

public static class ContributionsModule
{
    public static IServiceCollection AddContributionsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ContributionRepository>();
        services.AddIndexInitializer<ContributionsIndexInitializer>();
        return services;
    }
}
