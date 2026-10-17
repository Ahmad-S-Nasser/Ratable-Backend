using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;
using Ratable.Shared.Catalog;

namespace Ratable.Modules.Entities.Services;

public sealed class EntityCatalog(EntityRepository entities, CategoryRepository categories) : IEntityCatalog
{
    public async Task<EntityRef?> GetActiveEntityAsync(string entityId, CancellationToken ct = default)
    {
        if (!MongoDB.Bson.ObjectId.TryParse(entityId, out _)) return null;
        var entity = await entities.GetAsync(entityId, ct);
        return entity is { Status: EntityStatus.Active } ? new EntityRef(entity.Id, entity.CategoryId, [.. EntityService.AllNames(entity)]) : null;
    }

    public async Task<IReadOnlyList<CatalogEntry>> ListActiveByCategoryAsync(string categoryId, int limit, CancellationToken ct = default)
    {
        if (await categories.GetAsync(categoryId, ct) is not { Active: true }) return [];
        var found = await entities.ListByCategoryAsync(categoryId, limit, ct);
        return [.. found.Select(e => new CatalogEntry(e.Id, e.CategoryId, e.Name, e.Slugs, e.City, e.Address, e.UpdatedAt))];
    }

    public async Task<IReadOnlyList<CatalogEntry>> GetActiveEntriesAsync(IReadOnlyCollection<string> entityIds, CancellationToken ct = default)
    {
        var found = await entities.ListActiveByIdsAsync(entityIds, ct);
        return [.. found.Select(e => new CatalogEntry(e.Id, e.CategoryId, e.Name, e.Slugs, e.City, e.Address, e.UpdatedAt))];
    }

    public async Task<CategoryInfo?> GetCategoryAsync(string categoryId, CancellationToken ct = default)
    {
        var category = await categories.GetAsync(categoryId, ct);
        if (category is not { Active: true }) return null;
        return new CategoryInfo(category.Id, category.AnonymousByDefault, [.. category.Criteria.Select(c => new CriterionInfo(
            c.Id,
            c.AnswerType switch { AnswerType.Choice => CriterionKind.Choice, AnswerType.YesNo => CriterionKind.YesNo, _ => CriterionKind.Rating5 },
            [.. c.Options.Select(o => new OptionInfo(o.Value, o.Score))]))], [.. category.AcceptedProofTypes], category.RatesIndividuals, category.ContactGated);
    }
}
