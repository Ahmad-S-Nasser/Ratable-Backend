using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Modules.Moderation.Models;
using Ratable.Shared.Content;
using Ratable.Shared.Data;

namespace Ratable.Modules.Moderation.Data;

public sealed class ReportRepository(IMongoDatabase database) : MongoRepository<Report>(database, Collections.Reports)
{
    public Task InsertAsync(Report report, CancellationToken ct = default) => Collection.InsertOneAsync(report, cancellationToken: ct);

    public Task ReplaceAsync(Report report, CancellationToken ct = default) =>
        Collection.ReplaceOneAsync(r => r.Id == report.Id, report, cancellationToken: ct);

    public Task<long> CountByReporterSinceAsync(string reporterId, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(r => r.ReporterId == reporterId && r.CreatedAt >= since, cancellationToken: ct);

    public async Task<Report?> GetAsync(string id, CancellationToken ct = default) =>
        ObjectId.TryParse(id, out _) ? await Collection.Find(r => r.Id == id).FirstOrDefaultAsync(ct) : null;

    public async Task<List<Report>> ListOpenAsync(int limit = 500, CancellationToken ct = default) =>
        await Collection.Find(r => r.Status == ReportStatus.Open).SortBy(r => r.CreatedAt).Limit(limit).ToListAsync(ct);

    public async Task<List<Report>> ListOpenForTargetAsync(ReportTarget type, string targetId, CancellationToken ct = default) =>
        await Collection.Find(r => r.Status == ReportStatus.Open && r.TargetType == type && r.TargetId == targetId).ToListAsync(ct);

    public Task<long> CountOpenAsync(CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(r => r.Status == ReportStatus.Open, cancellationToken: ct);
}

/// <summary>Append and read only. There is deliberately no update or delete.</summary>
public sealed class AuditLogRepository(IMongoDatabase database) : MongoRepository<AuditLogEntry>(database, Collections.AuditLog)
{
    public Task AppendAsync(AuditLogEntry entry, CancellationToken ct = default) => Collection.InsertOneAsync(entry, cancellationToken: ct);

    public async Task<List<AuditLogEntry>> ListForTargetAsync(string targetType, string targetId, CancellationToken ct = default) =>
        await Collection.Find(e => e.TargetType == targetType && e.TargetId == targetId).SortBy(e => e.At).ToListAsync(ct);

    /// <summary>Newest first, with optional filters, for the admin audit view.</summary>
    public async Task<(List<AuditLogEntry> Items, long Total)> SearchAsync(
        string? action, string? targetType, string? actor, DateTime? from, DateTime? to, int skip, int limit, CancellationToken ct = default)
    {
        var f = Builders<AuditLogEntry>.Filter;
        var filter = f.Empty;
        if (!string.IsNullOrWhiteSpace(action)) filter &= f.Regex(e => e.Action, new BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(action)));
        if (!string.IsNullOrWhiteSpace(targetType)) filter &= f.Eq(e => e.TargetType, targetType);
        if (!string.IsNullOrWhiteSpace(actor)) filter &= f.Eq(e => e.Actor, actor);
        if (from is not null) filter &= f.Gte(e => e.At, from.Value);
        if (to is not null) filter &= f.Lt(e => e.At, to.Value);
        var total = await Collection.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await Collection.Find(filter).SortByDescending(e => e.At).ThenByDescending(e => e.Id).Skip(skip).Limit(limit).ToListAsync(ct);
        return (items, total);
    }
}

public sealed class ModerationCaseRepository(IMongoDatabase database) : MongoRepository<ModerationCase>(database, Collections.ModerationCases)
{
    /// <summary>Returns false when this version already has a case (so the same version is never screened twice).</summary>
    public async Task<bool> TryInsertAsync(ModerationCase c, CancellationToken ct = default)
    {
        try
        {
            await Collection.InsertOneAsync(c, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<ModerationCase?> GetAsync(string id, CancellationToken ct = default) =>
        ObjectId.TryParse(id, out _) ? await Collection.Find(c => c.Id == id).FirstOrDefaultAsync(ct) : null;

    public Task ReplaceAsync(ModerationCase c, CancellationToken ct = default) => Collection.ReplaceOneAsync(x => x.Id == c.Id, c, cancellationToken: ct);

    public async Task<List<ModerationCase>> ListOpenAsync(ContentKind? kind, int skip, int limit, CancellationToken ct = default)
    {
        var f = Builders<ModerationCase>.Filter;
        var filter = f.Eq(c => c.State, CaseState.Open);
        if (kind is not null) filter &= f.Eq(c => c.Kind, kind.Value);
        return await Collection.Find(filter).SortBy(c => c.CreatedAt).ThenBy(c => c.Id).Skip(skip).Limit(limit).ToListAsync(ct);
    }

    public Task<long> CountOpenAsync(ContentKind? kind, CancellationToken ct = default) =>
        kind is null ? Collection.CountDocumentsAsync(c => c.State == CaseState.Open, cancellationToken: ct)
                     : Collection.CountDocumentsAsync(c => c.State == CaseState.Open && c.Kind == kind, cancellationToken: ct);

    public async Task<DateTime?> OldestOpenAsync(CancellationToken ct = default) =>
        (await Collection.Find(c => c.State == CaseState.Open).SortBy(c => c.CreatedAt).Limit(1).FirstOrDefaultAsync(ct))?.CreatedAt;

    public async Task<ModerationCase?> GetOpenForContentAsync(ContentKind kind, string contentId, CancellationToken ct = default) =>
        await Collection.Find(c => c.Kind == kind && c.ContentId == contentId && c.State == CaseState.Open).SortByDescending(c => c.RevisionNumber).FirstOrDefaultAsync(ct);

    public Task<bool> ExistsAsync(ContentKind kind, string contentId, int revision, CancellationToken ct = default) =>
        Collection.Find(c => c.Kind == kind && c.ContentId == contentId && c.RevisionNumber == revision).AnyAsync(ct);

    /// <summary>Older versions of the same content that are still waiting are replaced by the newer one.</summary>
    public Task SupersedeOlderAsync(ContentKind kind, string contentId, int revision, CancellationToken ct = default) =>
        Collection.UpdateManyAsync(c => c.Kind == kind && c.ContentId == contentId && c.State == CaseState.Open && c.RevisionNumber < revision,
            Builders<ModerationCase>.Update.Set(c => c.State, CaseState.Superseded), cancellationToken: ct);

    // ---- signals ----

    /// <summary>The same text posted on another review or comment, recently.</summary>
    public Task<bool> TextSeenElsewhereAsync(string textHash, string exceptContentId, DateTime since, CancellationToken ct = default) =>
        Collection.Find(c => c.TextHash == textHash && c.ContentId != exceptContentId && c.CreatedAt >= since).AnyAsync(ct);

    /// <summary>How many versions of reviews for this entity were submitted in the window, by anyone.</summary>
    public Task<long> CountForEntitySinceAsync(string entityId, DateTime since, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(c => c.Kind == ContentKind.Review && c.EntityId == entityId && c.RevisionNumber == 1 && c.CreatedAt >= since, cancellationToken: ct);

    /// <summary>Other accounts that reviewed the same entity from the same network or device.</summary>
    public async Task<int> OtherAccountsOnSameDeviceAsync(string entityId, string authorId, string? ipHash, string? deviceHash, DateTime since, CancellationToken ct = default)
    {
        if (ipHash is null && deviceHash is null) return 0;
        var f = Builders<ModerationCase>.Filter;
        var same = new List<FilterDefinition<ModerationCase>>();
        if (ipHash is not null) same.Add(f.Eq(c => c.IpHash, ipHash));
        if (deviceHash is not null) same.Add(f.Eq(c => c.DeviceHash, deviceHash));
        var filter = f.And(f.Eq(c => c.Kind, ContentKind.Review), f.Eq(c => c.EntityId, entityId), f.Ne(c => c.AuthorId, authorId), f.Gte(c => c.CreatedAt, since), f.Or(same));
        return (await Collection.Distinct(c => c.AuthorId, filter, cancellationToken: ct).ToListAsync(ct)).Count;
    }

    public Task<long> CountDecidedSinceAsync(DateTime since, CaseRoute route, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(c => c.State == CaseState.Decided && c.Route == route && c.DecidedAt >= since, cancellationToken: ct);
}

public sealed class DisputeRepository(IMongoDatabase database) : MongoRepository<Dispute>(database, Collections.Disputes)
{
    public Task InsertAsync(Dispute d, CancellationToken ct = default) => Collection.InsertOneAsync(d, cancellationToken: ct);

    public async Task<Dispute?> GetAsync(string id, CancellationToken ct = default) =>
        ObjectId.TryParse(id, out _) ? await Collection.Find(d => d.Id == id).FirstOrDefaultAsync(ct) : null;

    public Task ReplaceAsync(Dispute d, CancellationToken ct = default) => Collection.ReplaceOneAsync(x => x.Id == d.Id, d, cancellationToken: ct);

    public async Task<List<Dispute>> ListOpenAsync(int limit = 200, CancellationToken ct = default) =>
        await Collection.Find(d => d.State == DisputeState.Open).SortBy(d => d.CreatedAt).Limit(limit).ToListAsync(ct);

    public Task<long> CountOpenAsync(CancellationToken ct = default) => Collection.CountDocumentsAsync(d => d.State == DisputeState.Open, cancellationToken: ct);

    public Task<bool> HasOpenForReviewAsync(string reviewId, CancellationToken ct = default) =>
        Collection.Find(d => d.ReviewId == reviewId && d.State == DisputeState.Open).AnyAsync(ct);

    public async Task<List<Dispute>> ListOpenForReviewAsync(string reviewId, CancellationToken ct = default) =>
        await Collection.Find(d => d.ReviewId == reviewId && d.State == DisputeState.Open).ToListAsync(ct);
}

public sealed class ModerationIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var reports = database.GetCollection<Report>(Collections.Reports);
        var r = Builders<Report>.IndexKeys;
        await reports.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Report>(r.Ascending(x => x.Status).Ascending(x => x.CreatedAt), new CreateIndexOptions { Name = "queue" }),
            new CreateIndexModel<Report>(r.Ascending(x => x.TargetType).Ascending(x => x.TargetId), new CreateIndexOptions { Name = "target" }),
            // A user can report the same item only once.
            new CreateIndexModel<Report>(r.Ascending(x => x.ReporterId).Ascending(x => x.TargetType).Ascending(x => x.TargetId),
                new CreateIndexOptions { Name = "one_report_per_user_per_target", Unique = true }),
        ], ct);

        var audit = database.GetCollection<AuditLogEntry>(Collections.AuditLog);
        var a = Builders<AuditLogEntry>.IndexKeys;
        await audit.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<AuditLogEntry>(a.Ascending(x => x.TargetType).Ascending(x => x.TargetId).Ascending(x => x.At), new CreateIndexOptions { Name = "target_at" }),
            new CreateIndexModel<AuditLogEntry>(a.Descending(x => x.At), new CreateIndexOptions { Name = "at" }),
            new CreateIndexModel<AuditLogEntry>(a.Ascending(x => x.Action).Descending(x => x.At), new CreateIndexOptions { Name = "action_at" }),
        ], ct);

        var cases = database.GetCollection<ModerationCase>(Collections.ModerationCases);
        var c = Builders<ModerationCase>.IndexKeys;
        await cases.Indexes.CreateManyAsync(
        [
            // Each version of a review or comment is screened once.
            new CreateIndexModel<ModerationCase>(c.Ascending(x => x.Kind).Ascending(x => x.ContentId).Ascending(x => x.RevisionNumber),
                new CreateIndexOptions { Name = "one_case_per_version", Unique = true }),
            new CreateIndexModel<ModerationCase>(c.Ascending(x => x.State).Ascending(x => x.CreatedAt), new CreateIndexOptions { Name = "queue" }),
            new CreateIndexModel<ModerationCase>(c.Ascending(x => x.TextHash), new CreateIndexOptions { Name = "text_hash" }),
            new CreateIndexModel<ModerationCase>(c.Ascending(x => x.EntityId).Descending(x => x.CreatedAt), new CreateIndexOptions { Name = "entity_created" }),
            new CreateIndexModel<ModerationCase>(c.Ascending(x => x.IpHash), new CreateIndexOptions { Name = "ip_hash" }),
            new CreateIndexModel<ModerationCase>(c.Ascending(x => x.DeviceHash), new CreateIndexOptions { Name = "device_hash" }),
        ], ct);

        var disputes = database.GetCollection<Dispute>(Collections.Disputes);
        var d = Builders<Dispute>.IndexKeys;
        await disputes.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Dispute>(d.Ascending(x => x.State).Ascending(x => x.CreatedAt), new CreateIndexOptions { Name = "queue" }),
            new CreateIndexModel<Dispute>(d.Ascending(x => x.ReviewId).Ascending(x => x.State), new CreateIndexOptions { Name = "review_state" }),
        ], ct);
    }
}
