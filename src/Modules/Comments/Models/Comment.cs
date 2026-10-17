using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Ratable.Modules.Comments.Models;

public enum CommentStatus { PendingModeration, Published, Hidden, Deleted }

/// <summary>A comment on a review, one level deep. Comments never change scores.</summary>
public sealed class Comment
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string ReviewId { get; set; } = "";
    /// <summary>Set when this is a reply to another comment; replies to replies are not allowed.</summary>
    [BsonRepresentation(BsonType.ObjectId)] public string? ParentId { get; set; }
    /// <summary>The real author, always stored (rule 1).</summary>
    [BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = "";
    public bool IsAnonymous { get; set; }
    public string Text { get; set; } = "";
    public CommentStatus Status { get; set; } = CommentStatus.PendingModeration;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Set when the text was changed after posting. Readers see an "edited" label.</summary>
    public DateTime? EditedAt { get; set; }
}
