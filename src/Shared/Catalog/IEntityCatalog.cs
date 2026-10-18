using Ratable.Shared.Data;

namespace Ratable.Shared.Catalog;

/// <summary>A public entity. The names are public data, used for example to tell an AI which place a proof should be about.</summary>
public sealed record EntityRef(string Id, string CategoryId, IReadOnlyList<string> Names);

/// <summary>What a listing needs to show about a public entity.</summary>
public sealed record CatalogEntry(
    string Id, string CategoryId, LocalizedText Name, IReadOnlyDictionary<string, string> Slugs, string? City, string? Address, DateTime UpdatedAt,
    IReadOnlyDictionary<string, string>? Fields = null, double? Longitude = null, double? Latitude = null);

public enum CriterionKind { Rating5, Choice, YesNo }

/// <summary>One answer choice and the 1 to 5 score it counts as.</summary>
public sealed record OptionInfo(string Value, int Score);

public sealed record CriterionInfo(string Id, CriterionKind Kind, IReadOnlyList<OptionInfo> Options);

public sealed record CategoryInfo(
    string Id, bool AnonymousByDefault, IReadOnlyList<CriterionInfo> Criteria, IReadOnlyList<string> AcceptedProofTypes,
    bool RatesIndividuals = false, bool ContactGated = false, bool VerdictEnabled = true);

/// <summary>
/// What other modules (Reviews first) may know about entities and categories, without touching the Entities module's collections.
/// Implemented by the Entities module.
/// </summary>
public interface IEntityCatalog
{
    /// <summary>The entity, only if it is public (Active). Pending, rejected, hidden and merged entities return null.</summary>
    Task<EntityRef?> GetActiveEntityAsync(string entityId, CancellationToken cancellationToken = default);

    /// <summary>Public entities of an active category, for listings. Empty when the category is unknown or inactive.</summary>
    Task<IReadOnlyList<CatalogEntry>> ListActiveByCategoryAsync(string categoryId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Public entities by id, for listings. Ids that are not public are left out.</summary>
    Task<IReadOnlyList<CatalogEntry>> GetActiveEntriesAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken = default);

    /// <summary>Public entities changed since a moment, for telling search engines which pages changed.</summary>
    Task<IReadOnlyList<CatalogEntry>> ListUpdatedSinceAsync(DateTime since, int limit, CancellationToken cancellationToken = default);

    /// <summary>Public entities within a distance of a point, nearest first, for the nearby list. The point is used for this call only.</summary>
    Task<IReadOnlyList<CatalogEntry>> ListNearAsync(double latitude, double longitude, double radiusMeters, string? categoryId, int limit, CancellationToken cancellationToken = default);

    /// <summary>The category's criteria, only if the category is active.</summary>
    Task<CategoryInfo?> GetCategoryAsync(string categoryId, CancellationToken cancellationToken = default);
}
