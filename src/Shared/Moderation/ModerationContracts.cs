using Ratable.Shared.Content;

namespace Ratable.Shared.Moderation;

/// <summary>A new or changed review or comment, handed to the Moderation module. It carries no author name, only the facts moderation needs.</summary>
public sealed record ModerationSubmission(
    ContentKind Kind,
    string ContentId,
    string AuthorId,
    string? EntityId,
    string? CategoryId,
    string? Text,
    string Language,
    int RevisionNumber,
    bool IsAnonymous,
    bool HasVerifiedProof);

/// <summary>
/// Implemented by the Moderation module, called by Reviews and Comments whenever a review or comment is created, edited or restored (rule 2 and the
/// plan: "every new or edited review and comment" is screened). The content stays PendingModeration until the module publishes it itself (the automatic
/// checks passed and the routing rules allow it) or a moderator does.
/// </summary>
public interface IModerationIntake
{
    Task SubmitAsync(ModerationSubmission submission, CancellationToken cancellationToken = default);

    /// <summary>A review just became Verified (a proof was accepted). A held review whose only reason was "no proof" can now be published.</summary>
    Task OnReviewVerifiedAsync(string reviewId, CancellationToken cancellationToken = default);
}

public enum ModerationOutcome { Publish, Hide }

/// <summary>What a moderator sees of a review or comment. There is deliberately no author in it: moderators decide on the content, and only an admin can look an anonymous author up, with a reason.</summary>
public sealed record ModerationContentView(
    string Id, ContentKind Kind, string? Text, string Language, int RevisionNumber, string? PreviousText, string? EntityId, string? CategoryId,
    string Status, bool VerifiedProof, bool UnderReview, double? Overall, bool IsAnonymous, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>A review or comment waiting for moderation. Used to make sure nothing waits without a case in the queue.</summary>
public sealed record PendingContent(string Id, int RevisionNumber);

/// <summary>Implemented by Reviews and Comments: how the Moderation module publishes, hides and reads their content without touching their collections.</summary>
public interface IContentModerator
{
    ContentKind Kind { get; }

    /// <summary>Publishes or hides the content. A hide reason is kept and shown to the author. Returns false when it does not exist.</summary>
    Task<bool> ApplyAsync(string contentId, ModerationOutcome outcome, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Marks a review as disputed ("under review" label) or clears it. Comments have no such label and return false.</summary>
    Task<bool> SetUnderReviewAsync(string contentId, bool underReview, CancellationToken cancellationToken = default);

    Task<ModerationContentView?> GetViewAsync(string contentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingContent>> ListPendingAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>Facts about an account that moderation weighs, never its identity. Implemented by the Identity module.</summary>
public sealed record AccountSignals(bool PhoneVerified, string? IpHash, string? DeviceHash);

public interface IAccountSignals
{
    Task<AccountSignals> GetAsync(string userId, CancellationToken cancellationToken = default);
}
