using System.Reflection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Ratable.Modules.Entities.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Entities.Data;

/// <summary>
/// Loads category configuration from the embedded Categories/*.json files. Inserts only categories that are missing,
/// so edits made later (by an admin or a newer seed file) are never overwritten.
/// </summary>
public sealed class CategorySeeder(CategoryRepository categories, ILogger<CategorySeeder> logger) : ISeeder
{
    public static IEnumerable<Category> LoadEmbedded()
    {
        MongoConventions.Register();
        var assembly = typeof(CategorySeeder).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Categories.") && n.EndsWith(".json")).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return BsonSerializer.Deserialize<Category>(BsonDocument.Parse(reader.ReadToEnd()));
        }
    }

    public async Task SeedAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        foreach (var category in LoadEmbedded())
        {
            if (dryRun)
            {
                var exists = await categories.ExistsAsync(category.Id, cancellationToken);
                logger.LogInformation("Dry run: category {Category} {Action}", category.Id, exists ? "exists, skipped" : "would be inserted");
                continue;
            }
            var inserted = await categories.InsertIfMissingAsync(category, cancellationToken);
            logger.LogInformation("Category {Category} {Action}", category.Id, inserted ? "inserted" : "already present");
        }
    }
}
