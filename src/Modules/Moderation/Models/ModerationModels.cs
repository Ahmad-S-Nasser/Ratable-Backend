using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Ratable.Modules.Moderation.Models;

public enum ReportTarget { Review, Comment, Entity, Video }

public enum ReportStatus { Open, Upheld, Dismissed }

public sealed class Report
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public ReportTarget TargetType { get; set; }
    [BsonRepresentation(BsonType.ObjectId)] public string TargetId { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string ReporterId { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? Details { get; set; }
    public ReportStatus Status { get; set; } = ReportStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    [BsonRepresentation(BsonType.ObjectId)] public string? ResolvedBy { get; set; }
}

/// <summary>Every removal, edit decision and admin action is logged here. Append-only.</summary>
public sealed class AuditLogEntry
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    /// <summary>The user or "system" (for example an automatic AI decision).</summary>
    public string Actor { get; set; } = "system";
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Reason { get; set; } = "";
    /// <summary>The AI model name and version when a model made or informed the decision.</summary>
    public string? Model { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}
