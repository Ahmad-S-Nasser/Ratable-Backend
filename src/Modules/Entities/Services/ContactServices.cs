using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Endpoints;
using Ratable.Modules.Entities.Models;
using Ratable.Shared.Businesses;
using Ratable.Shared.Data;
using Ratable.Shared.Text;

namespace Ratable.Modules.Entities.Services;

/// <summary>Counters per place and day. There is no visitor in it: not an id, not a network address.</summary>
public sealed class EntityDailyStat
{
    [BsonId] public string Id { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string EntityId { get; set; } = "";
    /// <summary>The UTC day, yyyy-MM-dd, so days sort as text.</summary>
    public string Day { get; set; } = "";
    public int Views { get; set; }
    public int ContactTaps { get; set; }
}

public sealed class EntityActivityRepository(IMongoDatabase database) : MongoRepository<EntityDailyStat>(database, Collections.EntityDailyStats)
{
    private static string DayOf(DateTime utc) => utc.ToString("yyyy-MM-dd");

    private Task AddAsync(string entityId, DateTime now, string field, CancellationToken ct) =>
        Collection.UpdateOneAsync(
            s => s.Id == $"{entityId}:{DayOf(now)}",
            Builders<EntityDailyStat>.Update.SetOnInsert(s => s.EntityId, entityId).SetOnInsert(s => s.Day, DayOf(now)).Inc(field, 1),
            new UpdateOptions { IsUpsert = true }, ct);

    public Task AddViewAsync(string entityId, DateTime now, CancellationToken ct = default) => AddAsync(entityId, now, nameof(EntityDailyStat.Views), ct);

    public Task AddTapAsync(string entityId, DateTime now, CancellationToken ct = default) => AddAsync(entityId, now, nameof(EntityDailyStat.ContactTaps), ct);

    public async Task<ActivityTotals> TotalsAsync(string entityId, DateTime fromDayUtc, CancellationToken ct = default)
    {
        var from = DayOf(fromDayUtc);
        var days = await Collection.Find(s => s.EntityId == entityId && string.Compare(s.Day, from) >= 0).ToListAsync(ct);
        return new ActivityTotals(days.Sum(d => d.Views), days.Sum(d => d.ContactTaps));
    }
}

public sealed class EntityActivityIndexInitializer : IIndexInitializer
{
    public Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default) =>
        database.GetCollection<EntityDailyStat>(Collections.EntityDailyStats).Indexes.CreateOneAsync(
            new CreateIndexModel<EntityDailyStat>(Builders<EntityDailyStat>.IndexKeys.Ascending(s => s.EntityId).Descending(s => s.Day), new CreateIndexOptions { Name = "entity_day" }), cancellationToken: ct);
}

/// <summary>Contact details of places and the activity counters. Other modules reach them only through the Shared interfaces.</summary>
public sealed class EntityContacts(EntityRepository entities, EntityActivityRepository activity, TimeProvider time) : IEntityContacts, IEntityActivity
{
    public async Task<ContactDetails?> GetAsync(string entityId, CancellationToken ct = default)
    {
        var e = await entities.GetAsync(entityId, ct);
        return e?.Contact is { } c ? new ContactDetails(c.Phone, c.WhatsApp, c.Links) : null;
    }

    public async Task<bool> SetAsync(string entityId, ContactDetails details, ContactSource source, CancellationToken ct = default)
    {
        var e = await entities.GetAsync(entityId, ct);
        if (e is null) return false;
        e.Contact = details.IsEmpty ? null : new EntityContact { Phone = details.Phone, WhatsApp = details.WhatsApp, Links = [.. details.Links], Source = source.ToString().ToLowerInvariant(), UpdatedAt = time.GetUtcNow().UtcDateTime };
        await entities.ReplaceAsync(e, time.GetUtcNow().UtcDateTime, ct);
        return true;
    }

    public Task<ActivityTotals> GetTotalsAsync(string entityId, DateTime fromDayUtc, CancellationToken ct = default) => activity.TotalsAsync(entityId, fromDayUtc, ct);
}

/// <summary>A small sliding-window counter per key, kept in memory. Used for the public "view" and "show contact" calls, which have no account to count against.</summary>
public sealed class ContactRateLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new();

    /// <summary>True and counts the call when fewer than <paramref name="limit"/> calls happened in the last <paramref name="window"/>.</summary>
    public bool TryAcquire(string key, int limit, TimeSpan window)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var queue = _hits.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > window) queue.Dequeue();
            if (queue.Count >= limit) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}

/// <summary>
/// Builds the public view of places, and is the one place where contact is kept out of it (rule 19). In a category that gates contact: links are never in the public
/// response; phone and WhatsApp are never in it either (a subscribed profile shows them through the logged Show contact call); and for a locked profile every text of
/// the profile is stripped of contact details too. Without a Businesses module to ask, a gated profile counts as locked.
/// </summary>
public sealed class PublicEntityBuilder(CategoryRepository categories, IEnumerable<IContactAccess> access)
{
    public async Task<List<PublicEntity>> BuildAsync(IReadOnlyList<Entity> list, CancellationToken ct = default)
    {
        var gatedCategories = new Dictionary<string, bool>();
        foreach (var id in list.Select(e => e.CategoryId).Distinct())
            gatedCategories[id] = (await categories.GetAsync(id, ct))?.ContactGated ?? false;

        var gated = list.Where(e => gatedCategories[e.CategoryId]).Select(e => (e.Id, e.CategoryId)).ToList();
        IReadOnlySet<string> locked = new HashSet<string>(gated.Select(g => g.Id));   // fail closed
        var provider = access.FirstOrDefault();
        if (provider is not null && gated.Count > 0) locked = await provider.LockedAmongAsync(gated, ct);

        return [.. list.Select(e => Build(e, gatedCategories[e.CategoryId], locked.Contains(e.Id)))];
    }

    public async Task<PublicEntity> BuildOneAsync(Entity e, CancellationToken ct = default) => (await BuildAsync([e], ct))[0];

    private static PublicEntity Build(Entity e, bool gated, bool locked)
    {
        var has = e.Contact is { } c && !new ContactDetails(c.Phone, c.WhatsApp, c.Links).IsEmpty;
        string Clean(string value) => locked ? ContactStripper.Strip(value) : value;
        var text = e.Summary?.Text;
        if (locked && text is not null) text = new Ratable.Shared.Data.LocalizedText(ContactStripper.Strip(text.Ar), ContactStripper.Strip(text.En));

        return new PublicEntity(
            e.Id, e.CategoryId, e.Name, e.AlternateNames, e.Slugs,
            e.Location?.Coordinates.Longitude, e.Location?.Coordinates.Latitude,
            e.Address is null ? null : Clean(e.Address), e.City, e.Country,
            e.ExternalIds, gated ? [] : e.SameAs, e.Fields.ToDictionary(f => f.Key, f => Clean(f.Value)), text, e.UpdatedAt,
            ContactLocked: gated && locked,
            ContactAvailable: has && (!gated || !locked),
            Contact: !gated && has ? new PublicContact(e.Contact!.Phone, e.Contact.WhatsApp, e.Contact.Links) : null);
    }
}
