using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Ratable.Modules.Identity.Models;

public enum UserRole { User, Moderator, Admin, BusinessOwner }

public enum UserStatus { Active, Suspended }

public sealed class UserProfile
{
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
}

public sealed class ExternalLogin
{
    /// <summary>google or apple.</summary>
    public string Provider { get; set; } = "";
    public string Subject { get; set; } = "";
}

/// <summary>An account. The public profile is separate from the account and optional.</summary>
public sealed class User
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public List<UserRole> Roles { get; set; } = [UserRole.User];
    public UserProfile Profile { get; set; } = new();
    public List<ExternalLogin> Logins { get; set; } = [];
    /// <summary>Phone number encrypted at rest (rule 18). Never shown, never returned by an API.</summary>
    public string? PhoneEncrypted { get; set; }
    /// <summary>One-way hash of the normalized phone number, used for the one-account-per-number rule.</summary>
    public string? PhoneHash { get; set; }
    public bool PhoneVerified { get; set; }
    public UserStatus Status { get; set; } = UserStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
