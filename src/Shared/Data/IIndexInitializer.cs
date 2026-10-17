using MongoDB.Driver;

namespace Ratable.Shared.Data;

/// <summary>Each module registers one of these for the collections it owns. Creating an index twice is a no-op.</summary>
public interface IIndexInitializer
{
    Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken = default);
}
