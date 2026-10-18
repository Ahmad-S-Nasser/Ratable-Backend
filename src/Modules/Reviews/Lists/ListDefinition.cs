using MongoDB.Bson.Serialization.Attributes;
using Ratable.Shared.Data;

namespace Ratable.Modules.Reviews.Lists;

/// <summary>
/// A curated list such as "Top compounds" (CLAUDE.md rule 13). It is configuration, not code: a title in Arabic and English, a slug, a category, an optional
/// area, the fewest verified reviews a place needs, and a limit. Which places appear and in what order is decided only by the verified score, never by
/// the list's owner or by payment.
/// </summary>
public sealed class ListDefinition
{
    /// <summary>A short stable id that is part of the address: /{lang}/lists/{id}-{slug}. It never changes.</summary>
    [BsonId] public string Id { get; set; } = "";

    public LocalizedText Title { get; set; } = new();
    public LocalizedText Slug { get; set; } = new();
    public string CategoryId { get; set; } = "";

    /// <summary>A city or area name in both languages. A place is in the list when its city or address mentions it. Empty means everywhere.</summary>
    public LocalizedText? Area { get; set; }

    public int MinReviews { get; set; } = 1;
    public int Limit { get; set; } = 10;
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
