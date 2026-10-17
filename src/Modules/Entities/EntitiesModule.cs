using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Services;
using Ratable.Shared.Catalog;
using Ratable.Shared.Data;

namespace Ratable.Modules.Entities;

public static class EntitiesModule
{
    public static IServiceCollection AddEntitiesModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EntitiesOptions>(configuration.GetSection(EntitiesOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<CategoryRepository>();
        services.AddSingleton<EntityRepository>();
        services.AddIndexInitializer<EntitiesIndexInitializer>();
        services.AddSeeder<CategorySeeder>();

        services.AddSingleton<DuplicateJudge>();
        services.AddSingleton<EntityService>();
        services.AddSingleton<IEntityCatalog, EntityCatalog>();
        return services;
    }
}
