using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Ratable.Modules.Contributions.Models;

/// <summary>
/// One line in the append-only points ledger. Entries are never edited or deleted; a reversal is a new entry with
/// negative points that points back at the original. Levels are computed from the ledger. Points never affect scores (rule 16).
/// </summary>
public sealed class ContributionEvent
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = "";
    /// <summary>For example written_review, photo, video, verified_visit, first_review, helpful_vote.</summary>
    public string Action { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string? EntityId { get; set; }
    public int Points { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>The review, comment or vote that earned the points.</summary>
    public string? Reference { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Set on a reversal entry: the id of the entry it cancels.</summary>
    [BsonRepresentation(BsonType.ObjectId)] public string? ReversesEventId { get; set; }
}
