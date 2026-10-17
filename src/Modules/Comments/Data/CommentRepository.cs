using MongoDB.Driver;
using Ratable.Modules.Comments.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Comments.Data;

public sealed class CommentRepository(IMongoDatabase database) : MongoRepository<Comment>(database, Collections.Comments)
{
    public Task InsertAsync(Comment comment, CancellationToken ct = default) => Collection.InsertOneAsync(comment, cancellationToken: ct);

    public async Task<Comment?> GetAsync(string id, CancellationToken ct = default) =>
        await Collection.Find(c => c.Id == id).FirstOrDefaultAsync(ct);

    public Task ReplaceAsync(Comment comment, CancellationToken ct = default) =>
        Collection.ReplaceOneAsync(c => c.Id == comment.Id, comment, cancellationToken: ct);

    public async Task<List<Comment>> ListPublishedAsync(string reviewId, CancellationToken ct = default) =>
        await Collection.Find(c => c.ReviewId == reviewId && c.Status == CommentStatus.Published).SortBy(c => c.CreatedAt).ToListAsync(ct);
}

public sealed class CommentsIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var comments = database.GetCollection<Comment>(Collections.Comments);
        var k = Builders<Comment>.IndexKeys;
        await comments.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Comment>(k.Ascending(c => c.ReviewId).Ascending(c => c.CreatedAt), new CreateIndexOptions { Name = "review_created" }),
            new CreateIndexModel<Comment>(k.Ascending(c => c.UserId).Descending(c => c.CreatedAt), new CreateIndexOptions { Name = "user_created" }),
        ], ct);
    }
}
