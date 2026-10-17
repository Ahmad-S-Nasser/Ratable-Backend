using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Ratable.Modules.Verification.Models;

public enum VerificationType { Phone, Proof }

public enum VerificationResult { Pending, Approved, Rejected, Deferred }

public enum RetentionMode { Delete, Keep }

public sealed class ProofRetention
{
    public RetentionMode Mode { get; set; } = RetentionMode.Delete;
    /// <summary>When a kept proof must be deleted anyway.</summary>
    public DateTime? ExpiresAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

/// <summary>
/// The record of a verification. Only the proof type, date, result and a one-way hash are stored (rule 5).
/// The file itself lives in private storage under StorageKey and is deleted after the decision unless the author chose to keep it.
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
    public string? ProofHash { get; set; }
    public string? StorageKey { get; set; }
    public ProofRetention Retention { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
