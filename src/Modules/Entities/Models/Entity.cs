using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Shared.Data;

namespace Ratable.Modules.Entities.Models;

public enum EntityStatus { Active, Hidden, Merged }

/// <summary>A place or product that can be reviewed (compound, school, hotel, car model, appliance, restaurant ...).</summary>
public sealed class Entity
{
    /// <summary>Stable id used in URLs /{lang}/{category}/{id}-{slug}. Never changes.</summary>
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string CategoryId { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    public List<string> AlternateNames { get; set; } = [];
    /// <summary>Slug per language ("ar", "en"). A changed slug redirects to the current one.</summary>
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
    /// <summary>Normalized names for search; filled by the Arabic normalizer.</summary>
    public string SearchTerms { get; set; } = "";
    public EntityStatus Status { get; set; } = EntityStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Used as lastmod in sitemaps.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Short generated summary; every statement is traceable to the reviews it came from.</summary>
public sealed class EntitySummary
{
    public LocalizedText Text { get; set; } = new();
    public List<string> SourceReviewIds { get; set; } = [];
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
