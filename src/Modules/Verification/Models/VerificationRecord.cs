using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Ratable.Modules.Verification.Models;

public enum VerificationType { Phone, Proof }

public enum VerificationResult
{
    /// <summary>Uploaded, being checked.</summary>
    Pending,
    /// <summary>The automatic checks found something (or could not run): a person decides.</summary>
    Flagged,
    Approved,
    Rejected,
    /// <summary>Kept for old data; the AI was capped. New records use Flagged for this.</summary>
    Deferred,
}

public enum RetentionMode { Delete, Keep }

public sealed class ProofRetention
{
    public RetentionMode Mode { get; set; } = RetentionMode.Delete;
    /// <summary>When a kept proof must be deleted anyway.</summary>
    public DateTime? ExpiresAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

/// <summary>What the AI read from the document. Short facts only, never the document text.</summary>
public sealed class AiReading
{
    public string? DocumentType { get; set; }
    public string? EntityName { get; set; }
    public string? DocumentDate { get; set; }
    public double? Confidence { get; set; }
    public string? Model { get; set; }
}

/// <summary>
/// The record of a verification. Only the proof type, date, result and a one-way hash are kept for good (rule 5). The file itself lives in private
/// storage under StorageKey and is deleted after the decision unless the author chose to keep it.
/// </summary>
public sealed class VerificationRecord
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string? ReviewId { get; set; }
    [BsonRepresentation(BsonType.ObjectId)] public string? EntityId { get; set; }
    public VerificationType Type { get; set; }
    public string? ProofType { get; set; }
    public VerificationResult Result { get; set; } = VerificationResult.Pending;
    public DateTime? DecidedAt { get; set; }
    /// <summary>"auto" or the moderator's account id.</summary>
    public string? DecidedBy { get; set; }
    /// <summary>One-way SHA-256 of the file, used to spot the same proof reused. Never shown to anyone.</summary>
    public string? ProofHash { get; set; }
    public string? StorageKey { get; set; }
    public string? ContentType { get; set; }
    public ProofRetention Retention { get; set; } = new();

    /// <summary>Why the automatic checks flagged it (codes such as duplicate_file, date_out_of_range). Moderators only.</summary>
    public List<string> Reasons { get; set; } = [];
    public AiReading? Reading { get; set; }
    /// <summary>The message the author is shown when a person rejects their proof.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>The author declared no relationship with the business and no payment or reward.</summary>
    public DateTime? DeclaredAt { get; set; }

    /// <summary>A random share of automatically approved proofs is also checked by a person. True while that check is still to be done.</summary>
    public bool AuditSampled { get; set; }
    public bool AuditPending { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The author's separate, explicit consent to keep a proof file (PDPL). Withdrawn when the author deletes the file or the review.</summary>
public sealed class ConsentRecord
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string VerificationId { get; set; } = "";
    public string Kind { get; set; } = "keep_proof";
    /// <summary>Which wording of the consent screen the author agreed to.</summary>
    public string TextVersion { get; set; } = "keep-proof-v1";
    public DateTime GrantedAt { get; set; }
    public DateTime? WithdrawnAt { get; set; }
}
