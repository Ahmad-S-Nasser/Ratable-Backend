namespace Ratable.Shared.Merge;

/// <summary>A reason the merge cannot go ahead, for a moderator to resolve by hand. Carries counts, never names or ids of users.</summary>
public sealed record MergeConflict(string Module, string Code, int Count);

/// <summary>
/// Implemented by every module that keeps data pointing at an entity (reviews, verifications ...). When a duplicate entity is merged into
/// another one, the Entities module asks each participant first whether the merge is possible, then to move its data.
/// <see cref="MoveAsync"/> must be safe to run twice, because a merge has no transaction and is simply run again if it stopped half way.
/// </summary>
public interface IEntityMergeParticipant
{
    Task<IReadOnlyList<MergeConflict>> FindConflictsAsync(string sourceEntityId, string targetEntityId, CancellationToken cancellationToken = default);

    Task MoveAsync(string sourceEntityId, string targetEntityId, CancellationToken cancellationToken = default);
}
