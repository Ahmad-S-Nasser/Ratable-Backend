using MongoDB.Driver;
using Ratable.Modules.Comments.Models;
using Ratable.Shared.Content;
using Ratable.Shared.Data;

namespace Ratable.Modules.Comments.Data;

public sealed class CommentRepository(IMongoDatabase database) : MongoRepository<Comment>(database, Collections.Comments)
{
    public Task InsertAsync(Comment comment, CancellationToken ct = default) => Collection.InsertOneAsync(comment, cancellationToken: ct);

    public async Task<Comment?> GetAsync(string id, CancellationToken ct = default) =>
        MongoDB.Bson.ObjectId.TryParse(id, out _) ? await Collection.Find(c => c.Id == id).FirstOrDefaultAsync(ct) : null;

    public Task ReplaceAsync(Comment comment, CancellationToken ct = default) =>
        Collection.ReplaceOneAsync(c => c.Id == comment.Id, comment, cancellationToken: ct);

    /// <summary>Public comments of a review, oldest first (top-level and replies together; the caller nests them).</summary>
    public async Task<List<Comment>> ListPublishedAsync(string reviewId, CancellationToken ct = default) =>
        await Collection.Find(c => c.ReviewId == reviewId && c.Status == CommentStatus.Published).SortBy(c => c.CreatedAt).ThenBy(c => c.Id).Limit(500).ToListAsync(ct);

    public async Task<List<Comment>> ListByUserAsync(string userId, string? reviewId, CancellationToken ct = default) =>
        await Collection.Find(c => c.UserId == userId && (reviewId == null || c.ReviewId == reviewId)).SortByDescending(c => c.CreatedAt).Limit(100).ToListAsync(ct);

    public Task<long> CountByUserSinceAsync(string userId, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(c => c.UserId == userId && c.CreatedAt >= since, cancellationToken: ct);
}

/// <summary>Lets the Moderation module see the little it needs about a comment (reports, the audited anonymous-author look-up).</summary>
public sealed class CommentContentDirectory(CommentRepository comments) : IContentDirectory
{
    public ContentKind Kind => ContentKind.Comment;

    public async Task<ContentInfo?> GetAsync(string id, CancellationToken ct = default)
    {
        var comment = await comments.GetAsync(id, ct);
        return comment is null ? null : new ContentInfo(comment.Id, comment.UserId, comment.IsAnonymous, comment.Status == CommentStatus.Published, null);
    }
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
