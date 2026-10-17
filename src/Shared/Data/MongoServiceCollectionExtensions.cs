using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Ratable.Shared.Data;

public static class MongoServiceCollectionExtensions
{
    public static IServiceCollection AddRatableMongo(this IServiceCollection services, IConfiguration configuration)
    {
        MongoConventions.Register();
        services.AddOptions<MongoOptions>()
            .Bind(configuration.GetSection(MongoOptions.Section))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "Mongo:ConnectionString is not set.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Database), "Mongo:Database is not set.");

        // The client is created lazily so the process can start (and /health can answer) before MongoDB is reachable.
        services.AddSingleton<IMongoClient>(sp =>
            new MongoClient(sp.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString));
        services.AddSingleton(sp =>
            sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<MongoOptions>>().Value.Database));

        services.AddDataBootstrap();
        services.AddHealthChecks().AddCheck<MongoHealthCheck>("mongo", tags: ["ready"]);
        return services;
    }
}
