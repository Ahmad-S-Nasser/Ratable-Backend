using Microsoft.Extensions.Options;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Matching;
using Ratable.Modules.Entities.Models;
using Ratable.Modules.Entities.Services;
using Ratable.Shared.Audit;
using Ratable.Shared.Data;
using Ratable.Shared.Text;

namespace Ratable.Modules.Entities.Import;

public sealed record ImportItem(int Line, string Outcome, string? Name, string? EntityId, string? MatchedEntityId, double? Score, string? Reason, bool? Changed);

public sealed record ImportReport(bool DryRun, string Batch, int Total, int Created, int Merged, int NeedsReview, int Skipped, IReadOnlyList<ImportItem> Items);

/// <summary>
/// Loads places from a file (P11). Every row is checked, its names normalized, and matched against the places that exist and the rows already read. A row that is the same place
/// as an existing one (name score at or above <see cref="EntitiesOptions.ImportMergeThreshold"/> and inside the distance) is merged into it: only blanks are filled and the
/// existing data is never overwritten. A row that only looks similar becomes a pending place with its candidates, for a moderator. Everything else is created active and marked
/// seeded. No AI is used and nothing is public that a rule did not allow. A dry run does all the work except writing, so its report is exactly what a real run would do.
/// </summary>
public sealed class SeedImporter(EntityRepository entities, CategoryRepository categories, EntityService service, IAuditLog audit, IOptions<EntitiesOptions> options, TimeProvider time)
{
    private EntitiesOptions Opt => options.Value;

    public async Task<ImportReport> RunAsync(IReadOnlyList<ImportRow> rows, bool dryRun, string? batchName, string actorId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var batch = string.IsNullOrWhiteSpace(batchName) ? $"import-{now:yyyyMMdd-HHmm}" : batchName.Trim();
        var items = new List<ImportItem>();
        var categoryCache = new Dictionary<string, Category?>();
        var planned = new List<Entity>();   // created (or to be created) by this run, so two rows of one file are not both created

        foreach (var row in rows)
        {
            var label = row.NameEn ?? row.NameAr;
            var (entity, category, error) = await BuildAsync(row, categoryCache, ct);
            if (entity is null || category is null) { items.Add(Skip(row, label, error ?? "invalid")); continue; }

            // The same Google place or external id is the same place, whatever the name says.
            var keyMatch = await FindByKeyAsync(entity, planned, ct);
            var candidates = keyMatch is null ? await service.FindCandidatesAsync(entity, ct) : [];
            var plannedIds = planned.Select(p => p.Id).ToHashSet();
            var inRun = keyMatch is null ? CandidatesIn(entity, planned) : [];
            var all = candidates.Where(c => !plannedIds.Contains(c.EntityId)).Concat(inRun).OrderByDescending(c => c.NameScore).ThenBy(c => c.DistanceMeters ?? double.MaxValue).ToList();
            var best = all.FirstOrDefault();

            if (keyMatch is not null || (best is not null && best.NameScore >= Opt.ImportMergeThreshold))
            {
                var targetId = keyMatch?.Id ?? best!.EntityId;
                var target = planned.FirstOrDefault(p => p.Id == targetId) ?? await entities.GetAsync(targetId, ct);
                if (target is null) { items.Add(Skip(row, label, "match_missing")); continue; }
                var changed = FillBlanks(target, entity);
                if (changed && !dryRun && planned.All(p => p.Id != target.Id)) await entities.ReplaceAsync(target, now, ct);
                items.Add(new ImportItem(row.Line, "merged", label, null, target.Id, keyMatch is null ? best!.NameScore : 1.0, keyMatch is null ? "same_place" : "same_key", changed));
                continue;
            }

            entity.Seeded = true;
            entity.SeedBatch = batch;
            entity.CreatedAt = now;
            entity.UpdatedAt = now;
            if (all.Count > 0)
            {
                // Looks like a place that exists, but not enough to be sure: a person decides.
                entity.Status = EntityStatus.PendingModeration;
                entity.DuplicateCheck = new DuplicateCheck { Candidates = all.Take(Opt.MaxCandidates).ToList(), Suggestion = DuplicateSuggestion.Unsure, Confidence = best!.NameScore, AiStatus = "not_needed", CheckedAt = now };
                if (!dryRun) await entities.InsertAsync(entity, ct);
                planned.Add(entity);
                items.Add(new ImportItem(row.Line, "review", label, entity.Id, best.EntityId, best.NameScore, "similar_place", null));
                continue;
            }

            entity.Status = EntityStatus.Active;
            entity.Slugs = new Dictionary<string, string>
            {
                ["ar"] = Slugger.Make(entity.Name.Ar.Length > 0 ? entity.Name.Ar : entity.Name.En),
                ["en"] = Slugger.Make(entity.Name.En.Length > 0 ? entity.Name.En : entity.Name.Ar),
            };
            entity.Decision = new ModerationDecision { DecidedBy = actorId, DecidedAt = now, Reason = $"Seeded ({batch})" };
            if (!dryRun) await entities.InsertAsync(entity, ct);
            planned.Add(entity);
            items.Add(new ImportItem(row.Line, "created", label, entity.Id, null, null, null, null));
        }

        var report = new ImportReport(dryRun, batch, rows.Count, items.Count(i => i.Outcome == "created"), items.Count(i => i.Outcome == "merged"),
            items.Count(i => i.Outcome == "review"), items.Count(i => i.Outcome == "skipped"), items);
        if (!dryRun)
            await audit.AppendAsync(new AuditRecord(actorId, "entities.seed_import", "import", batch,
                $"{report.Created} created, {report.Merged} merged, {report.NeedsReview} for review, {report.Skipped} skipped of {report.Total}"), ct);
        return report;
    }

    private static ImportItem Skip(ImportRow row, string? label, string reason) => new(row.Line, "skipped", label, null, null, null, reason, null);

    /// <summary>Checks one row and makes the place it would become (not stored). The reason is a code: invalid_category, invalid_name, invalid_location, invalid_field:key ...</summary>
    private async Task<(Entity? Entity, Category? Category, string? Error)> BuildAsync(ImportRow row, Dictionary<string, Category?> cache, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(row.Category)) return (null, null, "invalid_category");
        if (!cache.TryGetValue(row.Category, out var category)) cache[row.Category] = category = await categories.GetAsync(row.Category, ct);
        if (category is not { Active: true }) return (null, null, "invalid_category");

        var nameAr = EntityService.Clean(row.NameAr);
        var nameEn = EntityService.Clean(row.NameEn);
        var alternates = row.AltNames.Select(EntityService.Clean).Where(n => n.Length > 0).Distinct().ToList();
        if ((nameAr.Length == 0 && nameEn.Length == 0) || new[] { nameAr, nameEn }.Concat(alternates).Any(n => n.Length > Opt.MaxNameLength || n.Any(char.IsControl)) || alternates.Count > Opt.MaxAlternateNames)
            return (null, null, "invalid_name");
        if (row.Longitude is not { } lon || row.Latitude is not { } lat || !double.IsFinite(lon) || !double.IsFinite(lat) || lon is < -180 or > 180 || lat is < -90 or > 90)
            return (null, null, "invalid_location");
        var fieldError = service.ValidateFields(category, row.Fields, out var fields);
        if (fieldError is not null) return (null, null, $"invalid_field:{fieldError}");

        var entity = new Entity
        {
            CategoryId = category.Id,
            Name = new LocalizedText(nameAr, nameEn),
            AlternateNames = alternates,
            Location = GeoJson.Point(GeoJson.Geographic(lon, lat)),
            Address = EntityService.Limit(row.Address),
            City = EntityService.Limit(row.City),
            Country = EntityService.Limit(row.Country),
            GooglePlaceId = EntityService.Limit(row.GooglePlaceId),
            ExternalIds = row.ExternalIds.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
            Fields = fields,
        };
        // A website is a link of the contact in a category that gates contact (hidden with it), and a normal "same as" link elsewhere.
        List<string> links = [];
        if (Uri.TryCreate(row.Website, UriKind.Absolute, out var site) && site.Scheme is "http" or "https")
        {
            if (category.ContactGated) links = [site.ToString()];
            else entity.SameAs = [site.ToString()];
        }
        if (!string.IsNullOrWhiteSpace(row.Phone) || !string.IsNullOrWhiteSpace(row.WhatsApp) || links.Count > 0)
            entity.Contact = new EntityContact { Phone = EntityService.Limit(row.Phone), WhatsApp = EntityService.Limit(row.WhatsApp), Links = links, Source = "import" };
        entity.SearchTerms = EntityService.BuildSearchTerms(entity);
        return (entity, category, null);
    }

    private async Task<Entity?> FindByKeyAsync(Entity candidate, List<Entity> planned, CancellationToken ct)
    {
        if (candidate.GooglePlaceId is { } placeId)
        {
            var inRun = planned.FirstOrDefault(p => p.GooglePlaceId == placeId);
            if (inRun is not null) return inRun;
            var existing = await entities.GetByGooglePlaceIdAsync(placeId, ct);
            if (existing is not null && existing.CategoryId == candidate.CategoryId && existing.Status is EntityStatus.Active or EntityStatus.PendingModeration) return existing;
        }
        foreach (var other in planned.Where(p => p.CategoryId == candidate.CategoryId))
            if (candidate.ExternalIds.Any(kv => other.ExternalIds.TryGetValue(kv.Key, out var v) && v == kv.Value)) return other;
        return null;
    }

    private List<DuplicateCandidate> CandidatesIn(Entity entity, List<Entity> planned)
    {
        var point = entity.Location!.Coordinates;
        var mine = EntityService.AllNames(entity).ToList();
        var result = new List<DuplicateCandidate>();
        foreach (var other in planned.Where(p => p.CategoryId == entity.CategoryId))
        {
            var score = NameMatcher.BestScore(mine, EntityService.AllNames(other));
            if (score < Opt.CandidateThreshold) continue;
            var distance = Geo.DistanceMeters(point.Longitude, point.Latitude, other.Location!.Coordinates.Longitude, other.Location.Coordinates.Latitude);
            if (distance > Opt.DuplicateRadiusMeters) continue;
            result.Add(new DuplicateCandidate { EntityId = other.Id, Name = other.Name, NameScore = Math.Round(score, 3), DistanceMeters = Math.Round(distance), Status = other.Status });
        }
        return result;
    }

    /// <summary>Fills what the target lacks from the new data. Nothing that is already there is changed. Returns whether anything changed.</summary>
    private bool FillBlanks(Entity target, Entity incoming)
    {
        var changed = false;
        var known = EntityService.AllNames(target).Select(ArabicNormalizer.Normalize).ToHashSet();
        foreach (var name in EntityService.AllNames(incoming))
            if (known.Add(ArabicNormalizer.Normalize(name)) && target.AlternateNames.Count < Opt.MaxAlternateNames) { target.AlternateNames.Add(name); changed = true; }
        foreach (var (k, v) in incoming.ExternalIds) changed |= target.ExternalIds.TryAdd(k, v);
        foreach (var (k, v) in incoming.Fields) changed |= target.Fields.TryAdd(k, v);
        foreach (var link in incoming.SameAs)
            if (!target.SameAs.Contains(link)) { target.SameAs.Add(link); changed = true; }
        if (target.Address is null && incoming.Address is not null) { target.Address = incoming.Address; changed = true; }
        if (target.City is null && incoming.City is not null) { target.City = incoming.City; changed = true; }
        if (target.Country is null && incoming.Country is not null) { target.Country = incoming.Country; changed = true; }
        if (target.GooglePlaceId is null && incoming.GooglePlaceId is not null) { target.GooglePlaceId = incoming.GooglePlaceId; changed = true; }
        // An owner's own contact details are never replaced by imported ones.
        if (target.Contact is null && incoming.Contact is not null) { target.Contact = incoming.Contact; changed = true; }
        if (changed) target.SearchTerms = EntityService.BuildSearchTerms(target);
        return changed;
    }
}
