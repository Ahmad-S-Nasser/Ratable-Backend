using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Ratable.Shared.Data;

public static class DataLayerExtensions
{
    public static IServiceCollection AddIndexInitializer<T>(this IServiceCollection services) where T : class, IIndexInitializer =>
        services.AddSingleton<IIndexInitializer, T>();

    public static IServiceCollection AddSeeder<T>(this IServiceCollection services) where T : class, ISeeder =>
        services.AddSingleton<ISeeder, T>();

    /// <summary>Runs index initializers and seeders at startup. A failure is logged and does not stop the API, so /health still answers.</summary>
    public static IServiceCollection AddDataBootstrap(this IServiceCollection services) =>
        services.AddHostedService<DataBootstrapService>();
}

internal sealed class DataBootstrapService(
    IMongoDatabase database,
    IEnumerable<IIndexInitializer> initializers,
    IEnumerable<ISeeder> seeders,
    IOptions<MongoOptions> options,
    ILogger<DataBootstrapService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.BootstrapOnStartup) return;
        try
        {
            foreach (var initializer in initializers)
                await initializer.EnsureIndexesAsync(database, cancellationToken);
            foreach (var seeder in seeders)
                await seeder.SeedAsync(dryRun: false, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Data bootstrap (indexes and seed data) failed; the API keeps running.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
