using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Services;
using Ratable.Shared.Catalog;
using Ratable.Shared.Location;
using Ratable.Shared.Data;
using Ratable.Shared.Moderation;

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
        services.AddSingleton<AreaRepository>();
        services.AddIndexInitializer<AreasIndexInitializer>();
        services.AddSeeder<AreaSeeder>();
        services.AddSingleton<ILocationProvider, SeededLocationProvider>();

        services.AddSingleton<DuplicateJudge>();
        services.AddSingleton<EntityService>();
        services.AddSingleton<EntityActivityRepository>();
        services.AddIndexInitializer<EntityActivityIndexInitializer>();
        services.AddSingleton<ContactRateLimiter>();
        services.AddSingleton<EntityContacts>();
        services.AddSingleton<Ratable.Shared.Businesses.IEntityContacts>(sp => sp.GetRequiredService<EntityContacts>());
        services.AddSingleton<Ratable.Shared.Businesses.IEntityActivity>(sp => sp.GetRequiredService<EntityContacts>());
        services.AddSingleton<PublicEntityBuilder>();
        services.AddSingleton<Import.SeedImporter>();
        services.AddSingleton<IEntityCatalog, EntityCatalog>();
        services.AddSingleton<IQueueCounter, EntityQueueCounter>();
        return services;
    }
}
