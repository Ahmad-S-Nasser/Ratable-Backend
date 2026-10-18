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
    /// <summary>A short code for why (the same as the action, or a reversal reason such as "review_hidden"). The web app turns it into words in the reader's language.</summary>
    public string Reason { get; set; } = "";
    /// <summary>The media item, verification or vote that earned the points.</summary>
    public string? Reference { get; set; }
    /// <summary>The review everything came from, so one review being hidden or deleted can cancel all of it.</summary>
    public string? RootReference { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Set on a reversal entry: the id of the entry it cancels.</summary>
    [BsonRepresentation(BsonType.ObjectId)] public string? ReversesEventId { get; set; }
}

/// <summary>The rank a person last saw, so the rank-up moment is shown once.</summary>
public sealed class RankSeen
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = "";
    public string RankId { get; set; } = "";
}
