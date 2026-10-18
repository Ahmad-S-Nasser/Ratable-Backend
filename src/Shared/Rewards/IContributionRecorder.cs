namespace Ratable.Shared.Rewards;

/// <summary>The kinds of effort and usefulness that earn points (CLAUDE.md rule 16). Nothing about being positive or negative is on this list.</summary>
public static class ContributionActions
{
    public const string WrittenReview = "written_review";
    public const string Photo = "photo";
    public const string Video = "video";
    public const string VerifiedVisit = "verified_visit";
    public const string FirstReviewOfPlace = "first_review";
    public const string HelpfulVote = "helpful_vote";
}

/// <summary>
/// Implemented by the Contributions module and called by the modules where the effort happens (Reviews, Media). It only writes to the append-only points
/// ledger. Points are never an input to any score, verdict, ranking or removal, and no module reads them to decide one.
/// </summary>
public interface IContributionRecorder
{
    /// <summary>
    /// Earns the points for an action once: the same (user, action, reference) is never paid twice. <paramref name="rootReference"/> is the review the effort
    /// belongs to, so that if the review is hidden, deleted or found to be a duplicate every point it earned can be cancelled together.
    /// </summary>
    Task RecordAsync(string userId, string action, string reference, string rootReference, string? entityId = null, int? textLength = null, CancellationToken cancellationToken = default);

    /// <summary>Cancels the points earned by one reference (a media item, a verification, a vote). Nothing happens if there are none.</summary>
    Task ReverseReferenceAsync(string reference, string reason, CancellationToken cancellationToken = default);

    /// <summary>Cancels every point earned under a review (the review was hidden, deleted or is a duplicate or a paid review).</summary>
    Task ReverseRootAsync(string rootReference, string reason, CancellationToken cancellationToken = default);
}
