using MongoDB.Driver;
using Ratable.Modules.Moderation.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Moderation.Data;

public sealed class ReportRepository(IMongoDatabase database) : MongoRepository<Report>(database, Collections.Reports)
{
    public Task InsertAsync(Report report, CancellationToken ct = default) => Collection.InsertOneAsync(report, cancellationToken: ct);

    public Task ReplaceAsync(Report report, CancellationToken ct = default) =>
        Collection.ReplaceOneAsync(r => r.Id == report.Id, report, cancellationToken: ct);

    public async Task<List<Report>> ListOpenAsync(int limit = 100, CancellationToken ct = default) =>
        await Collection.Find(r => r.Status == ReportStatus.Open).SortBy(r => r.CreatedAt).Limit(limit).ToListAsync(ct);
}

/// <summary>Append and read only. There is deliberately no update or delete.</summary>
public sealed class AuditLogRepository(IMongoDatabase database) : MongoRepository<AuditLogEntry>(database, Collections.AuditLog)
{
    public Task AppendAsync(AuditLogEntry entry, CancellationToken ct = default) => Collection.InsertOneAsync(entry, cancellationToken: ct);

    public async Task<List<AuditLogEntry>> ListForTargetAsync(string targetType, string targetId, CancellationToken ct = default) =>
        await Collection.Find(e => e.TargetType == targetType && e.TargetId == targetId).SortBy(e => e.At).ToListAsync(ct);
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
        ], ct);
    }
}
