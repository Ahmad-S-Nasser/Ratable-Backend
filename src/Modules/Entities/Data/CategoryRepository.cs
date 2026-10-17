using MongoDB.Driver;
using Ratable.Modules.Entities.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Entities.Data;

public sealed class CategoryRepository(IMongoDatabase database) : MongoRepository<Category>(database, Collections.Categories)
{
    public async Task<Category?> GetAsync(string id, CancellationToken ct = default) =>
        await Collection.Find(c => c.Id == id).FirstOrDefaultAsync(ct);

    public async Task<List<Category>> ListActiveAsync(CancellationToken ct = default) =>
        await Collection.Find(c => c.Active).ToListAsync(ct);

    public Task ReplaceAsync(Category category, CancellationToken ct = default) =>
        Collection.ReplaceOneAsync(c => c.Id == category.Id, category, new ReplaceOptions { IsUpsert = true }, ct);

    /// <summary>Inserts only when absent, in one atomic step. Returns false when the category already exists.</summary>
    public async Task<bool> InsertIfMissingAsync(Category category, CancellationToken ct = default)
    {
        try
        {
            await Collection.InsertOneAsync(category, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> ExistsAsync(string id, CancellationToken ct = default) =>
        await Collection.CountDocumentsAsync(c => c.Id == id, cancellationToken: ct) > 0;
}
