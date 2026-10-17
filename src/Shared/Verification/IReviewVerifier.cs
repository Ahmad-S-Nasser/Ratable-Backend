namespace Ratable.Shared.Verification;

/// <summary>
/// Implemented by the Reviews module. Lets the Verification module mark a review Verified (a proof was accepted) or take that back (a proof was rejected
/// on a human audit), without touching the reviews collection. Only proof changes this (CLAUDE.md rules 3 and 18). It does not publish or unpublish anything.
/// </summary>
public interface IReviewVerifier
{
    /// <summary>Returns false when the review does not exist.</summary>
    Task<bool> SetVerifiedAsync(string reviewId, string method, DateTime verifiedAt, CancellationToken cancellationToken = default);

    Task<bool> SetUnverifiedAsync(string reviewId, CancellationToken cancellationToken = default);
}

/// <summary>Implemented by the Verification module: when a review is deleted, any proof file kept for it is deleted too (rule 5).</summary>
public interface IReviewDeletedHandler
{
    Task OnReviewDeletedAsync(string reviewId, CancellationToken cancellationToken = default);
}
