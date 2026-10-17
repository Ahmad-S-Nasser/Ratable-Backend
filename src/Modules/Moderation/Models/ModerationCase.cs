using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Ratable.Shared.Content;

namespace Ratable.Modules.Moderation.Models;

public enum CaseState
{
    /// <summary>Waiting for a moderator.</summary>
    Open,
    /// <summary>Published or hidden, by the routing rules or by a moderator.</summary>
    Decided,
    /// <summary>The author edited the content: the newer version has its own case.</summary>
    Superseded,
}

public enum CaseRoute { Human, Auto }

public enum CaseDecision { Published, Hidden }

public sealed class FlagResult
{
    public bool Flagged { get; set; }
    public double Confidence { get; set; }
}

/// <summary>What the AI pre-screen said about a text. Logged with the model name; the text itself is never stored here.</summary>
public sealed class PrescreenResult
{
    public FlagResult Abuse { get; set; } = new();
    public FlagResult PersonalData { get; set; } = new();
    public FlagResult Spam { get; set; } = new();
    public FlagResult SuspectedFake { get; set; } = new();
    /// <summary>For an anonymous author: the text contains details that could identify them.</summary>
    public FlagResult IdentifyingDetails { get; set; } = new();
    public string? Model { get; set; }
}

/// <summary>
/// One review or comment version waiting for, or having had, moderation. The queue is the open cases. The case keeps why it went to a person
/// (Reasons), what the AI said (Prescreen) and who decided what and why. It holds no author identity and no text.
/// </summary>
public sealed class ModerationCase
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public ContentKind Kind { get; set; }
    [BsonRepresentation(BsonType.ObjectId)] public string ContentId { get; set; } = "";
    public int RevisionNumber { get; set; }
    [BsonRepresentation(BsonType.ObjectId)] public string? EntityId { get; set; }
    public string? CategoryId { get; set; }

    public CaseState State { get; set; } = CaseState.Open;
    public CaseRoute Route { get; set; } = CaseRoute.Human;
    /// <summary>Why a person has to decide: ai_deferred, ai_unavailable, abuse, personal_data, spam, suspected_fake, identifying_details, copied_text, review_burst,
    /// shared_device_or_network, individual, no_proof_unverified_account.</summary>
    public List<string> Reasons { get; set; } = [];
    public string AiStatus { get; set; } = "not_run";
    public PrescreenResult? Prescreen { get; set; }

    public bool AuthorPhoneVerified { get; set; }
    public bool HasVerifiedProof { get; set; }
    public bool IsAnonymous { get; set; }

    /// <summary>Hash of the normalized text, to find the same text posted again. Not reversible.</summary>
    public string? TextHash { get; set; }
    /// <summary>Hashes of the author's network and device at sign-in, to find several accounts on one device. Not the identity.</summary>
    public string? IpHash { get; set; }
    public string? DeviceHash { get; set; }
    /// <summary>The account that wrote it, only so signals can tell two accounts apart. Never returned by any endpoint.</summary>
    [BsonRepresentation(BsonType.ObjectId)] public string AuthorId { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    /// <summary>"auto" or the moderator's account id.</summary>
    public string? DecidedBy { get; set; }
    public CaseDecision? Decision { get; set; }
    public string? DecisionReason { get; set; }
}

public enum DisputeState { Open, Upheld, Dismissed }

/// <summary>
/// A business disputes a review. The review stays visible with an "under review" label while the dispute is open, unless a moderator finds it breaks a
/// rule. Upheld means the review breaks a rule and is hidden; dismissed means the label is removed and the review stays.
/// </summary>
public sealed class Dispute
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string ReviewId { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string? EntityId { get; set; }
    [BsonRepresentation(BsonType.ObjectId)] public string RaisedBy { get; set; } = "";
    public string Reason { get; set; } = "";
    public DisputeState State { get; set; } = DisputeState.Open;
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    [BsonRepresentation(BsonType.ObjectId)] public string? DecidedBy { get; set; }
    public string? DecisionReason { get; set; }
}
