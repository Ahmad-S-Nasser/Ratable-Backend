using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratable.Modules.Businesses.Data;
using Ratable.Modules.Businesses.Services;
using Ratable.Shared.Businesses;
using Ratable.Shared.Data;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Businesses;

public static class BusinessesModule
{
    public static IServiceCollection AddBusinessesModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BusinessesOptions>(configuration.GetSection(BusinessesOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<ClaimRepository>();
        services.AddSingleton<OwnershipRepository>();
        services.AddSingleton<OwnerReplyRepository>();
        services.AddIndexInitializer<BusinessesIndexInitializer>();

        services.AddSingleton<IEntityOwnership, EntityOwnership>();
        services.AddSingleton<IOwnerReplyDirectory, OwnerReplyDirectory>();
        services.AddSingleton<IQueueCounter, ClaimQueueCounter>();
        services.AddSingleton<ClaimService>();
        services.AddSingleton<OwnerReplyService>();
        services.AddSingleton<DashboardService>();
        return services;
    }
}
