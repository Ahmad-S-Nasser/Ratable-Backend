using MongoDB.Driver;

namespace Ratable.Shared.Data;

/// <summary>Minimal base for module repositories. Modules add the queries they need; nothing outside a module touches its collections.</summary>
public abstract class MongoRepository<T>(IMongoDatabase database, string collectionName)
{
    protected IMongoCollection<T> Collection { get; } = database.GetCollection<T>(collectionName);
}
