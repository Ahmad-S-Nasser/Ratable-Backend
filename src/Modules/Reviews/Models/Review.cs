using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Ratable.Modules.Reviews.Models;

public enum ReviewStatus { PendingModeration, Published, Hidden, Deleted }

public enum VerificationLevel { Unverified, Verified }

public sealed class ReviewAnswer
{
    public string CriterionId { get; set; } = "";
    /// <summary>The chosen option value for Choice and YesNo criteria; empty for a 1 to 5 tap.</summary>
    public string? OptionValue { get; set; }
    /// <summary>The 1 to 5 score. Scores are averaged from these.</summary>
    public int Score { get; set; }
}

public sealed class ReviewVerification
{
    public VerificationLevel Level { get; set; } = VerificationLevel.Unverified;
    /// <summary>For example "receipt", "booking" or "live_video". Empty when unverified.</summary>
    public string? Method { get; set; }
    public DateTime? VerifiedAt { get; set; }
}

/// <summary>The current version of a review. Earlier versions are in review_revisions.</summary>
public sealed class Review
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string EntityId { get; set; } = "";
    public string CategoryId { get; set; } = "";
    /// <summary>The real author, always stored (CLAUDE.md rule 1). Whether it is shown is decided when a response is built.</summary>
    [BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = "";
    public bool IsAnonymous { get; set; }
    public List<ReviewAnswer> Answers { get; set; } = [];
    public string? Text { get; set; }
    public string Language { get; set; } = "ar";

    /// <summary>The optional verdict the reviewer chose: worth, not_worth or hidden_gem. It feeds the place's verdict sticker (verified reviews only) and never the score.</summary>
    public string? Verdict { get; set; }

    public ReviewVerification Verification { get; set; } = new();
    public ReviewStatus Status { get; set; } = ReviewStatus.PendingModeration;
    public int RevisionNumber { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    /// <summary>A business disputed this review. It stays visible with an "under review" label unless a moderator finds it breaks a rule.</summary>
    public bool UnderReview { get; set; }

    /// <summary>Why a moderator hid it. Shown to the author, never to the public.</summary>
    public string? ModerationReason { get; set; }
    public DateTime? ModeratedAt { get; set; }
}

/// <summary>An earlier version of a review. Written once, never changed, kept even when the review is deleted.</summary>
public sealed class ReviewRevision
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string ReviewId { get; set; } = "";
    public int RevisionNumber { get; set; }
    public List<ReviewAnswer> Answers { get; set; } = [];
    public string? Text { get; set; }
    public string Language { get; set; } = "ar";
    public string? Verdict { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    [BsonRepresentation(BsonType.ObjectId)] public string ChangedBy { get; set; } = "";
    /// <summary>When a newer version replaced this one. The edited-on date shown to readers.</summary>
    public DateTime SupersededAt { get; set; }
    /// <summary>Whether this version was public when it was replaced. Only versions that were public appear in the public history.</summary>
    public ReviewStatus StatusWhenSuperseded { get; set; }
    /// <summary>The moderation verdict for this version (filled by the Moderation flow).</summary>
    public string? ModerationVerdict { get; set; }
}
