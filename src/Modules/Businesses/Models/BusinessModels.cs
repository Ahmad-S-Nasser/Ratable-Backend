using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Ratable.Modules.Businesses.Models;

public enum ClaimStatus { Draft, Pending, Approved, Rejected }

/// <summary>A request to run a place's page. A draft has no document yet and is not shown to moderators.</summary>
public sealed class BusinessClaim
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string EntityId { get; set; } = "";
    public string CompanyEmail { get; set; } = "";
    public string EmailDomain { get; set; } = "";
    /// <summary>owner, manager or representative.</summary>
    public string Role { get; set; } = "owner";
    public string? Website { get; set; }
    public string? Note { get; set; }

    /// <summary>commercial_register, tax_card, license or other.</summary>
    public string? DocumentType { get; set; }
    /// <summary>Key in private storage. Cleared when the document is deleted after the decision (rule 5).</summary>
    public string? DocumentKey { get; set; }
    /// <summary>One-way hash of the document, kept after the file is deleted.</summary>
    public string? DocumentHash { get; set; }
    public DateTime? DocumentUploadedAt { get; set; }

    /// <summary>The email domain looks like a free mail service. A moderator sees this flag; it does not decide anything.</summary>
    /// <summary>The company email was confirmed with a one-time code sent to it. Required before the document is uploaded.</summary>
    public bool EmailVerified { get; set; }
    /// <summary>One-way hash of the current code. Cleared once it is used.</summary>
    public string? EmailCodeHash { get; set; }
    public DateTime? EmailCodeExpiresAt { get; set; }
    public int EmailAttempts { get; set; }
    public int EmailSends { get; set; }
    public DateTime? EmailLastSentAt { get; set; }

    public bool FreeEmail { get; set; }
    /// <summary>The email domain appears in the place's website or links. Shown to the moderator as a hint.</summary>
    public bool DomainMatchesPlace { get; set; }

    public ClaimStatus Status { get; set; } = ClaimStatus.Draft;
    public string? DecisionReason { get; set; }
    [BsonRepresentation(BsonType.ObjectId)] public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The account that runs a place. At most one account per place.</summary>
public sealed class BusinessOwnership
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string EntityId { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string ClaimId { get; set; } = "";
    public DateTime ApprovedAt { get; set; } = DateTime.UtcNow;
    /// <summary>The last time the owner opened the dashboard; new reviews are counted from here.</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

public enum ReplyStatus { PendingModeration, Published, Hidden }

/// <summary>The owner's public reply under a review. One per review. Never holds anything about the reviewer. Every reply and every edit is moderated first.</summary>
public sealed class OwnerReply
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string ReviewId { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string EntityId { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = "";
    public string Text { get; set; } = "";
    public ReplyStatus Status { get; set; } = ReplyStatus.PendingModeration;
    /// <summary>Counts every version of the text, so each one is screened once.</summary>
    public int Revision { get; set; } = 1;
    /// <summary>Why a moderator hid it. Shown to the owner, never to the public.</summary>
    public string? ModerationReason { get; set; }
    public bool Deleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }
}
