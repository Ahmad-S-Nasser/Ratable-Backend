namespace Ratable.Shared.Moderation;

/// <summary>Each module that has a human queue (new entities, flagged proofs ...) reports how many items wait, for the moderator overview.</summary>
public interface IQueueCounter
{
    string Name { get; }

    Task<long> CountAsync(CancellationToken cancellationToken = default);
}
