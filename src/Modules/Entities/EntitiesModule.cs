using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Modules.Entities.Data;
using Ratable.Shared.Data;

namespace Ratable.Modules.Entities;

public static class EntitiesModule
{
    public static IServiceCollection AddEntitiesModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<CategoryRepository>();
        services.AddSingleton<EntityRepository>();
        services.AddIndexInitializer<EntitiesIndexInitializer>();
        services.AddSeeder<CategorySeeder>();
        return services;
    }
}
