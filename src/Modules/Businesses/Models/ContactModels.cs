using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Ratable.Modules.Businesses.Models;

public enum SubscriptionStatus { Active, Ended }

/// <summary>
/// A paid plan on one place. It is activated only by a verified payment notification (CLAUDE.md rule 10) or, until payments exist, by an audited admin grant. What a plan
/// includes is read from configuration; no plan can change a score, a ranking or a removal.
/// </summary>
public sealed class BusinessSubscription
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string EntityId { get; set; } = "";
    public string PlanId { get; set; } = "";
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ValidUntil { get; set; }
    /// <summary>payment or admin.</summary>
    public string Source { get; set; } = "payment";
    [BsonRepresentation(BsonType.ObjectId)] public string? GrantedBy { get; set; }
}

public enum RequestStatus { Open, Replied, Closed }

/// <summary>
/// A short request from a signed-in reader to a locked provider. The reader chooses to be reached through Ratable (the provider answers here) or to share their WhatsApp number
/// with that provider only. The number is never stored on the request: it is read from the account when the provider that holds the plan opens it.
/// </summary>
public sealed class ContactRequest
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string EntityId { get; set; } = "";
    public string CategoryId { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string ReaderId { get; set; } = "";
    public string What { get; set; } = "";
    public string? When { get; set; }
    public bool ShareWhatsApp { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>An open request closes at this moment (7 days after it was sent, configurable).</summary>
    public DateTime ExpiresAt { get; set; }
    public string? ReplyText { get; set; }
    public DateTime? RepliedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}

/// <summary>One upgrade message that was made for an owner. At most one per trigger per week.</summary>
public sealed class NudgeInstance
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string EntityId { get; set; } = "";
    public string Trigger { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DismissedAt { get; set; }
}
