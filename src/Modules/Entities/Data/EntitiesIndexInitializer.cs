using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Modules.Entities.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Entities.Data;

public sealed class EntitiesIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var entities = database.GetCollection<Entity>(Collections.Entities);
        var k = Builders<Entity>.IndexKeys;
        await entities.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Entity>(k.Geo2DSphere(e => e.Location), new CreateIndexOptions { Name = "location_2dsphere" }),
            // No language-specific stemming: the text is Arabic and English, normalized by our own matcher.
            new CreateIndexModel<Entity>(
                k.Text("name.ar").Text("name.en").Text(e => e.AlternateNames).Text(e => e.SearchTerms),
                new CreateIndexOptions { Name = "names_text", DefaultLanguage = "none" }),
            new CreateIndexModel<Entity>(k.Ascending(e => e.CategoryId).Ascending(e => e.Status), new CreateIndexOptions { Name = "category_status" }),
            new CreateIndexModel<Entity>(k.Descending(e => e.UpdatedAt), new CreateIndexOptions { Name = "updated" }),
            new CreateIndexModel<Entity>(k.Ascending(e => e.Status).Ascending(e => e.CreatedAt), new CreateIndexOptions { Name = "status_created" }),
            new CreateIndexModel<Entity>(k.Ascending(e => e.SubmittedByUserId).Descending(e => e.CreatedAt), new CreateIndexOptions { Name = "submitter_created" }),
            new CreateIndexModel<Entity>(k.Ascending(e => e.GooglePlaceId),
                new CreateIndexOptions<Entity>
                {
                    Name = "google_place_id",
                    Unique = true,
                    PartialFilterExpression = Builders<Entity>.Filter.Type(e => e.GooglePlaceId, BsonType.String),
                }),
        ], ct);
    }
}
