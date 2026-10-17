using MongoDB.Driver;
using Ratable.Modules.Verification.Models;
using Ratable.Shared.Data;
using Ratable.Shared.Merge;

namespace Ratable.Modules.Verification.Data;

/// <summary>Points verification records at the surviving entity after a merge. Never a reason to refuse one.</summary>
public sealed class VerificationMergeParticipant(IMongoDatabase database) : IEntityMergeParticipant
{
    private readonly IMongoCollection<VerificationRecord> _records = database.GetCollection<VerificationRecord>(Collections.Verifications);

    public Task<IReadOnlyList<MergeConflict>> FindConflictsAsync(string sourceEntityId, string targetEntityId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MergeConflict>>([]);

    public async Task MoveAsync(string sourceEntityId, string targetEntityId, CancellationToken ct = default) =>
        await _records.UpdateManyAsync(v => v.EntityId == sourceEntityId,
            Builders<VerificationRecord>.Update.Set(v => v.EntityId, targetEntityId), cancellationToken: ct);
}
