using MongoDB.Driver;
using Ratable.Modules.Comments.Data;
using Ratable.Modules.Contributions.Data;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Identity.Data;
using Ratable.Modules.Moderation.Data;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Verification.Data;
using Ratable.Shared.Data;

namespace Ratable.Api.Tests.Data;

/// <summary>Runs a test only when RATABLE_TEST_MONGO points at a local MongoDB (no containers are used).</summary>
public sealed class MongoFactAttribute : FactAttribute
{
    public MongoFactAttribute()
    {
        if (MongoFixture.ConnectionString is null)
            Skip = "Set RATABLE_TEST_MONGO (for example mongodb://localhost:27017) to run MongoDB tests.";
    }
}

/// <summary>A throwaway database with every module's indexes created. Dropped when the tests are done.</summary>
public sealed class MongoFixture : IAsyncLifetime
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("RATABLE_TEST_MONGO");

    private MongoClient? _client;
    public IMongoDatabase Database { get; private set; } = null!;

    public static IIndexInitializer[] Initializers { get; } =
    [
        new EntitiesIndexInitializer(), new ReviewsIndexInitializer(), new IdentityIndexInitializer(), new VerificationIndexInitializer(),
        new CommentsIndexInitializer(), new ContributionsIndexInitializer(), new ModerationIndexInitializer(),
    ];

    public async Task InitializeAsync()
    {
        if (ConnectionString is null) return;
        MongoConventions.Register();
        _client = new MongoClient(ConnectionString);
        Database = _client.GetDatabase($"ratable_tests_{Guid.NewGuid():N}");
        foreach (var initializer in Initializers)
            await initializer.EnsureIndexesAsync(Database);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName);
    }
}

[CollectionDefinition(Name)]
public sealed class MongoCollection : ICollectionFixture<MongoFixture>
{
    public const string Name = "mongo";
}
