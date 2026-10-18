using System.Globalization;
using Microsoft.Extensions.Options;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Matching;
using Ratable.Modules.Entities.Models;
using Ratable.Shared.Audit;
using Ratable.Shared.Data;
using Ratable.Shared.Merge;
using Ratable.Shared.Text;

namespace Ratable.Modules.Entities.Services;

public sealed record SubmitEntityRequest(
    string? NameAr, string? NameEn, string? CategoryId, double? Longitude, double? Latitude,
    string? Address, string? City, string? Country, List<string>? AlternateNames, Dictionary<string, string>? Fields);

public enum SubmitFailure { None, InvalidCategory, InvalidName, InvalidLocation, InvalidField, RateLimited }

public sealed record SubmitResult(SubmitFailure Failure, Entity? Entity = null, string? Detail = null)
{
    public bool Ok => Failure == SubmitFailure.None;
}

public enum ActionOutcome { Ok, NotFound, WrongState, Invalid, CategoryMismatch, Conflict }

public sealed record ActionResult(ActionOutcome Outcome, Entity? Entity = null, IReadOnlyList<MergeConflict>? Conflicts = null)
{
    public bool Ok => Outcome == ActionOutcome.Ok;
}

/// <summary>
/// The add-an-entity flow and the moderator actions on it. A submission is checked for duplicates (normalized-name matching plus distance,
/// then an AI judge), but it always waits in the moderator queue: nothing here makes an entity public by itself (rules 2 and 6).
/// </summary>
public sealed class EntityService(
    EntityRepository entities,
    CategoryRepository categories,
    DuplicateJudge judge,
    IEnumerable<IEntityMergeParticipant> mergeParticipants,
    IAuditLog audit,
    IOptions<EntitiesOptions> options,
    TimeProvider time)
{
    private EntitiesOptions Opt => options.Value;

    // ---- submit ----

    public async Task<SubmitResult> SubmitAsync(string userId, SubmitEntityRequest r, CancellationToken ct = default)
    {
        var category = string.IsNullOrWhiteSpace(r.CategoryId) ? null : await categories.GetAsync(r.CategoryId, ct);
        if (category is not { Active: true }) return new(SubmitFailure.InvalidCategory);

        var nameAr = Clean(r.NameAr);
        var nameEn = Clean(r.NameEn);
        var alternates = (r.AlternateNames ?? []).Select(Clean).Where(n => n.Length > 0).Distinct().ToList();
        if ((nameAr.Length == 0 && nameEn.Length == 0) || new[] { nameAr, nameEn }.Concat(alternates).Any(n => n.Length > Opt.MaxNameLength || n.Any(char.IsControl)) ||
            alternates.Count > Opt.MaxAlternateNames)
            return new(SubmitFailure.InvalidName);

        if (r.Longitude is not { } lon || r.Latitude is not { } lat || !double.IsFinite(lon) || !double.IsFinite(lat) ||
            lon is < -180 or > 180 || lat is < -90 or > 90)
            return new(SubmitFailure.InvalidLocation);

        var fieldError = ValidateFields(category, r.Fields, out var fields);
        if (fieldError is not null) return new(SubmitFailure.InvalidField, Detail: fieldError);

        var address = Limit(r.Address);
        var city = Limit(r.City);
        var country = Limit(r.Country);
        if (address?.Length > Opt.MaxFieldLength || city?.Length > Opt.MaxFieldLength || country?.Length > Opt.MaxFieldLength)
            return new(SubmitFailure.InvalidField, Detail: "address");

        var now = time.GetUtcNow().UtcDateTime;
        if (await entities.CountSubmittedSinceAsync(userId, now.AddDays(-1), ct) >= Opt.MaxSubmissionsPerDay)
            return new(SubmitFailure.RateLimited);

        var entity = new Entity
        {
            CategoryId = category.Id,
            Name = new LocalizedText(nameAr, nameEn),
            AlternateNames = alternates,
            Location = GeoJson.Point(GeoJson.Geographic(lon, lat)),
            Address = address,
            City = city,
            Country = country,
            Fields = fields,
            Status = EntityStatus.PendingModeration,
            SubmittedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        entity.SearchTerms = BuildSearchTerms(entity);

        var candidates = await FindCandidatesAsync(entity, ct);
        entity.DuplicateCheck = await judge.JudgeAsync(entity, candidates, now, ct);

        await entities.InsertAsync(entity, ct);
        return new(SubmitFailure.None, entity);
    }

    public async Task<List<DuplicateCandidate>> FindCandidatesAsync(Entity entity, CancellationToken ct = default)
    {
        var point = entity.Location!.Coordinates;
        var pool = await entities.CandidatePoolAsync(entity.CategoryId, point.Longitude, point.Latitude, Opt.DuplicateRadiusMeters,
            entity.SearchTerms, ct: ct);

        var mine = AllNames(entity).ToList();
        var result = new List<DuplicateCandidate>();
        foreach (var other in pool.Where(o => o.Id != entity.Id))
        {
            var score = NameMatcher.BestScore(mine, AllNames(other));
            if (score < Opt.CandidateThreshold) continue;

            double? distance = other.Location is null ? null
                : Geo.DistanceMeters(point.Longitude, point.Latitude, other.Location.Coordinates.Longitude, other.Location.Coordinates.Latitude);
            // Far apart: branches of a chain, not the same place. Without a location on one side only an identical name counts.
            if (distance is { } d ? d > Opt.DuplicateRadiusMeters : score < 1.0) continue;

            result.Add(new DuplicateCandidate { EntityId = other.Id, Name = other.Name, NameScore = Math.Round(score, 3), DistanceMeters = distance is null ? null : Math.Round(distance.Value), Status = other.Status });
        }
        return [.. result.OrderByDescending(c => c.NameScore).ThenBy(c => c.DistanceMeters ?? double.MaxValue).Take(Opt.MaxCandidates)];
    }

    // ---- moderator actions ----

    public async Task<ActionResult> ApproveAsync(string entityId, string moderatorId, CancellationToken ct = default)
    {
        var entity = await entities.GetAsync(entityId, ct);
        if (entity is null) return new(ActionOutcome.NotFound);
        if (entity.Status != EntityStatus.PendingModeration) return new(ActionOutcome.WrongState);

        var now = time.GetUtcNow().UtcDateTime;
        var slugAr = Slugger.Make(entity.Name.Ar.Length > 0 ? entity.Name.Ar : entity.Name.En);
        var slugEn = Slugger.Make(entity.Name.En.Length > 0 ? entity.Name.En : entity.Name.Ar);
        entity.Slugs = new Dictionary<string, string> { ["ar"] = slugAr, ["en"] = slugEn };
        entity.Status = EntityStatus.Active;
        entity.Decision = new ModerationDecision { DecidedBy = moderatorId, DecidedAt = now };
        await entities.ReplaceAsync(entity, now, ct);
        await audit.AppendAsync(new AuditRecord(moderatorId, "entity.approved", "entity", entity.Id, "Approved from the moderator queue"), ct);
        return new(ActionOutcome.Ok, entity);
    }

    public async Task<ActionResult> RejectAsync(string entityId, string moderatorId, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) return new(ActionOutcome.Invalid);
        var entity = await entities.GetAsync(entityId, ct);
        if (entity is null) return new(ActionOutcome.NotFound);
        if (entity.Status != EntityStatus.PendingModeration) return new(ActionOutcome.WrongState);

        var now = time.GetUtcNow().UtcDateTime;
        entity.Status = EntityStatus.Rejected;
        entity.Decision = new ModerationDecision { DecidedBy = moderatorId, DecidedAt = now, Reason = reason.Trim() };
        await entities.ReplaceAsync(entity, now, ct);
        await audit.AppendAsync(new AuditRecord(moderatorId, "entity.rejected", "entity", entity.Id, reason.Trim()), ct);
        return new(ActionOutcome.Ok, entity);
    }

    /// <summary>
    /// Folds a duplicate (the source) into the entity that stays (the target). In order:
    /// 1 check both are mergeable and ask every module whether its data would clash (a clash stops the merge, nothing is changed);
    /// 2 each module moves its data (reviews, verifications) to the target;
    /// 3 the target takes over the source's other names, external ids, sameAs links and place id, and its summary is dropped as stale;
    /// 4 the source becomes Merged and points at the target, so its old id redirects.
    /// There is no transaction, so every step is safe to repeat: if the merge stops half way it is simply run again.
    /// </summary>
    public async Task<ActionResult> MergeAsync(string sourceId, string targetId, string moderatorId, CancellationToken ct = default)
    {
        if (sourceId == targetId) return new(ActionOutcome.Invalid);
        var source = await entities.GetAsync(sourceId, ct);
        var target = await entities.GetAsync(targetId, ct);
        if (source is null || target is null) return new(ActionOutcome.NotFound);
        if (source.Status is not (EntityStatus.Active or EntityStatus.PendingModeration) || target.Status != EntityStatus.Active)
            return new(ActionOutcome.WrongState);
        if (source.CategoryId != target.CategoryId) return new(ActionOutcome.CategoryMismatch);

        var conflicts = new List<MergeConflict>();
        foreach (var participant in mergeParticipants) conflicts.AddRange(await participant.FindConflictsAsync(source.Id, target.Id, ct));
        if (conflicts.Count > 0) return new(ActionOutcome.Conflict, Conflicts: conflicts);

        foreach (var participant in mergeParticipants) await participant.MoveAsync(source.Id, target.Id, ct);

        var now = time.GetUtcNow().UtcDateTime;
        var known = AllNames(target).Select(ArabicNormalizer.Normalize).ToHashSet();
        foreach (var name in AllNames(source))
            if (known.Add(ArabicNormalizer.Normalize(name))) target.AlternateNames.Add(name);

        foreach (var (key, value) in source.ExternalIds) target.ExternalIds.TryAdd(key, value); // the surviving entity wins a clash
        target.SameAs = [.. target.SameAs.Concat(source.SameAs).Distinct()];
        target.Address ??= source.Address;
        target.City ??= source.City;
        target.Country ??= source.Country;
        foreach (var (key, value) in source.Fields) target.Fields.TryAdd(key, value);
        target.Summary = null;
        target.SearchTerms = BuildSearchTerms(target);

        // The place id is unique across entities: free it on the source before the target takes it.
        var movedPlaceId = target.GooglePlaceId is null ? source.GooglePlaceId : null;
        source.GooglePlaceId = null;
        source.Status = EntityStatus.Merged;
        source.MergedIntoId = target.Id;
        source.Decision = new ModerationDecision { DecidedBy = moderatorId, DecidedAt = now, Reason = $"Merged into {target.Id}" };
        await entities.ReplaceAsync(source, now, ct);

        target.GooglePlaceId = movedPlaceId ?? target.GooglePlaceId;
        await entities.ReplaceAsync(target, now, ct);

        await audit.AppendAsync(new AuditRecord(moderatorId, "entity.merged", "entity", source.Id, $"Merged into {target.Id}"), ct);
        return new(ActionOutcome.Ok, target);
    }

    // ---- reading ----

    /// <summary>Follows merges to the entity that now stands for this id. Returns null when it does not exist or is not public.</summary>
    public async Task<(Entity? Entity, bool Redirected)> ResolveAsync(string id, CancellationToken ct = default)
    {
        var redirected = false;
        for (var hops = 0; hops < 6; hops++)
        {
            var entity = await entities.GetAsync(id, ct);
            if (entity is null) return (null, redirected);
            if (entity.Status == EntityStatus.Active) return (entity, redirected);
            if (entity is { Status: EntityStatus.Merged, MergedIntoId: { } next }) { id = next; redirected = true; continue; }
            return (null, redirected);
        }
        return (null, redirected);
    }

    // ---- helpers ----

    public static string BuildSearchTerms(Entity e) =>
        string.Join(' ', AllNames(e).Select(ArabicNormalizer.Normalize).Where(n => n.Length > 0).Distinct());

    public static IEnumerable<string> AllNames(Entity e) =>
        new[] { e.Name.Ar, e.Name.En }.Concat(e.AlternateNames).Where(n => !string.IsNullOrWhiteSpace(n));

    internal static string Clean(string? s) => (s ?? "").Trim();

    internal static string? Limit(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal string? ValidateFields(Category category, Dictionary<string, string>? input, out Dictionary<string, string> accepted)
    {
        accepted = [];
        foreach (var (key, raw) in input ?? [])
        {
            var definition = category.EntityFields.FirstOrDefault(f => f.Key == key);
            if (definition is null) return key; // not a field of this category
            var value = (raw ?? "").Trim();
            if (value.Length == 0) continue;
            if (value.Length > Opt.MaxFieldLength || value.Any(char.IsControl)) return key;
            if (definition.Type == "number" && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) return key;
            if (definition.Type == "choice" && definition.Options.All(o => o.Value != value)) return key;
            accepted[key] = value;
        }
        return null;
    }
}
