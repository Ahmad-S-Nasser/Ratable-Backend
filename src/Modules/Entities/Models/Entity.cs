using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Shared.Data;

namespace Ratable.Modules.Entities.Models;

public enum EntityStatus
{
    /// <summary>Submitted by a user and waiting in the moderator queue. Never public.</summary>
    PendingModeration,
    Active,
    Hidden,
    /// <summary>A duplicate folded into another entity. Its id redirects to <see cref="Entity.MergedIntoId"/>.</summary>
    Merged,
    Rejected,
}

public enum DuplicateSuggestion { None, New, Duplicate, Unsure }

/// <summary>A place or product that can be reviewed (compound, school, hotel, car model, appliance, restaurant ...).</summary>
public sealed class Entity
{
    /// <summary>Stable id used in URLs /{lang}/{category}/{id}-{slug}. Never changes.</summary>
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string CategoryId { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    public List<string> AlternateNames { get; set; } = [];
    /// <summary>Slug per language ("ar", "en"), set when the entity is approved. A changed slug redirects to the current one.</summary>
    public Dictionary<string, string> Slugs { get; set; } = [];
    /// <summary>GeoJSON point, longitude then latitude. Indexed with 2dsphere.</summary>
    public GeoJsonPoint<GeoJson2DGeographicCoordinates>? Location { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? GooglePlaceId { get; set; }
    /// <summary>Other systems' ids, for example wikidata or albab.</summary>
    public Dictionary<string, string> ExternalIds { get; set; } = [];
    public List<string> SameAs { get; set; } = [];
    /// <summary>Values for the category's own entity fields.</summary>
    public Dictionary<string, string> Fields { get; set; } = [];
    public EntitySummary? Summary { get; set; }
    /// <summary>How to reach the place. Never part of a public response of a category that gates contact (rule 19); read through IEntityContacts.</summary>
    public EntityContact? Contact { get; set; }

    /// <summary>Loaded by the seed importer (P11) rather than submitted by a reader.</summary>
    public bool Seeded { get; set; }
    /// <summary>The name of the import run that created it.</summary>
    public string? SeedBatch { get; set; }
    /// <summary>Normalized names (Arabic normalizer) used for search and matching. Rebuilt whenever a name changes.</summary>
    public string SearchTerms { get; set; } = "";
    public EntityStatus Status { get; set; } = EntityStatus.Active;

    /// <summary>The account that submitted it. Internal only: never part of a public response.</summary>
    [BsonRepresentation(BsonType.ObjectId)] public string? SubmittedByUserId { get; set; }
    public DuplicateCheck? DuplicateCheck { get; set; }
    public ModerationDecision? Decision { get; set; }
    /// <summary>Set when Status is Merged.</summary>
    [BsonRepresentation(BsonType.ObjectId)] public string? MergedIntoId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Used as lastmod in sitemaps.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Phone, WhatsApp and links of a place. Owner-entered or imported.</summary>
public sealed class EntityContact
{
    public string? Phone { get; set; }
    public string? WhatsApp { get; set; }
    public List<string> Links { get; set; } = [];
    /// <summary>owner or import.</summary>
    public string Source { get; set; } = "owner";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Short generated summary; every statement is traceable to the reviews it came from.</summary>
public sealed class EntitySummary
{
    public LocalizedText Text { get; set; } = new();
    public List<string> SourceReviewIds { get; set; } = [];
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>What the duplicate check found when the entity was submitted. A moderator reads it; nothing here decides anything.</summary>
public sealed class DuplicateCheck
{
    public List<DuplicateCandidate> Candidates { get; set; } = [];
    public DuplicateSuggestion Suggestion { get; set; } = DuplicateSuggestion.None;
    [BsonRepresentation(BsonType.ObjectId)] public string? SuggestedDuplicateOfId { get; set; }
    public double? Confidence { get; set; }
    /// <summary>not_needed (no candidates), completed, deferred (daily cap) or failed. Deferred and failed mean a human decides with no AI help.</summary>
    public string AiStatus { get; set; } = "not_needed";
    public string? AiModel { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

public sealed class DuplicateCandidate
{
    [BsonRepresentation(BsonType.ObjectId)] public string EntityId { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    public double NameScore { get; set; }
    public double? DistanceMeters { get; set; }
    public EntityStatus Status { get; set; }
}

public sealed class ModerationDecision
{
    [BsonRepresentation(BsonType.ObjectId)] public string DecidedBy { get; set; } = "";
    public DateTime DecidedAt { get; set; }
    public string? Reason { get; set; }
}
