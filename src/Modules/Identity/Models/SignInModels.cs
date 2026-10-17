using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Ratable.Modules.Identity.Models;

/// <summary>A one-time sign-in code sent to a phone number. Only a keyed hash of the code is stored.</summary>
public sealed class OtpChallenge
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string PhoneHash { get; set; } = "";
    /// <summary>Keyed hash of the code; the code itself is never stored.</summary>
    public string CodeHash { get; set; } = "";
    /// <summary>Keyed hash of the requester's IP address, for rate limits only.</summary>
    public string IpHash { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

public enum SessionKind { Web, App }

/// <summary>A signed-in session. The token is shown once; only its SHA-256 hash is stored, so a database leak gives no usable token.</summary>
public sealed class Session
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string TokenHash { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = "";
    public SessionKind Kind { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
