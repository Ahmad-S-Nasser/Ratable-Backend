using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratable.Modules.Businesses.Data;
using Ratable.Modules.Businesses.Models;
using Ratable.Shared.Audit;
using Ratable.Shared.Authors;
using Ratable.Shared.Businesses;
using Ratable.Shared.Catalog;

namespace Ratable.Modules.Businesses.Services;

/// <summary>
/// Whether a profile is locked (rule 19): its category gates contact and the owner's plan does not include contact. A plan is read from configuration; an expired or ended
/// subscription counts as no plan. Nothing here is read by a score, a verdict, a ranking or the nearby order.
/// </summary>
public sealed class ContactAccess(SubscriptionRepository subscriptions, IEntityCatalog catalog, IOptions<BusinessesOptions> options, TimeProvider time) : IContactAccess
{
    private bool PlanHasContact(string planId) => options.Value.Plans.Any(p => p.Id == planId && p.ShowsContact);

    public async Task<bool> PlanShowsContactAsync(string entityId, CancellationToken ct = default) =>
        (await subscriptions.ListActiveAsync([entityId], time.GetUtcNow().UtcDateTime, ct)).Any(s => PlanHasContact(s.PlanId));

    public async Task<bool> IsLockedAsync(string entityId, string categoryId, CancellationToken ct = default) =>
        (await LockedAmongAsync([(entityId, categoryId)], ct)).Contains(entityId);

    public async Task<IReadOnlySet<string>> LockedAmongAsync(IEnumerable<(string EntityId, string CategoryId)> places, CancellationToken ct = default)
    {
        var list = places.Distinct().ToList();
        var gated = new Dictionary<string, bool>();
        foreach (var categoryId in list.Select(p => p.CategoryId).Distinct())
            gated[categoryId] = (await catalog.GetCategoryAsync(categoryId, ct))?.ContactGated ?? false;

        var candidates = list.Where(p => gated[p.CategoryId]).Select(p => p.EntityId).ToList();
        var subscribed = (await subscriptions.ListActiveAsync(candidates, time.GetUtcNow().UtcDateTime, ct)).Where(s => PlanHasContact(s.PlanId)).Select(s => s.EntityId).ToHashSet();
        return candidates.Where(id => !subscribed.Contains(id)).ToHashSet();
    }
}

public sealed record BusinessStats(
    int ViewsLast7Days, int ViewsLast30Days, int ContactTapsLast30Days, int OpenRequests, int ClosedUnansweredLast30Days, int RequestsLast30Days,
    IReadOnlyList<MonthlyScore> RatingTrend, int? CategoryRank, int? RankedPlaces, string? Area);

/// <summary>
/// The brief numbers shown on the dashboard of the contact plan, and the only source the upgrade messages use, so a message can never say a number the stats do not.
/// Views and taps are counters per day; ranks come from the same verified scores as every list.
/// </summary>
public sealed class BusinessStatsService(
    IEntityActivity activity, ContactRequestRepository requests, IBusinessReviewSource reviews, IEntityCatalog catalog, IOptions<BusinessesOptions> options, TimeProvider time)
{
    public async Task<BusinessStats> GetAsync(string entityId, string categoryId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var today = now.Date;
        var week = await activity.GetTotalsAsync(entityId, today.AddDays(-6), ct);
        var month = await activity.GetTotalsAsync(entityId, today.AddDays(-29), ct);
        var open = (int)await requests.CountOpenAsync(entityId, now, ct);
        var closed = (int)await requests.CountClosedUnansweredSinceAsync(entityId, now.AddDays(-30), now, ct);
        var sent = (int)await requests.CountSinceAsync(entityId, now.AddDays(-30), ct);
        var trend = (await reviews.GetStatsAsync(entityId, categoryId, options.Value.TrendMonths, ct)).Trend;
        var (rank, of, area) = await RankAsync(entityId, categoryId, ct);
        return new BusinessStats(week.Views, month.Views, month.ContactTaps, open, closed, sent, trend, rank, of, area);
    }

    /// <summary>The place's position among the scored places of its category in its area (city), best verified score first.</summary>
    private async Task<(int? Rank, int? Of, string? Area)> RankAsync(string entityId, string categoryId, CancellationToken ct)
    {
        var all = await catalog.ListActiveByCategoryAsync(categoryId, 500, ct);
        var me = all.FirstOrDefault(e => e.Id == entityId);
        if (me?.City is not { Length: > 0 } city) return (null, null, null);
        var sameArea = all.Where(e => string.Equals(e.City, city, StringComparison.OrdinalIgnoreCase)).Select(e => e.Id).ToList();
        var scores = await reviews.GetScoresAsync(sameArea, ct);
        if (!scores.TryGetValue(entityId, out var mine) || mine is null) return (null, null, city);
        var ranked = scores.Values.Where(s => s is not null).Select(s => s!.Value).ToList();
        return (1 + ranked.Count(s => s > mine.Value), ranked.Count, city);
    }
}

public sealed record Nudge(string Trigger, int Count, int? Rank, string? Area);

public interface IBusinessNudgeService
{
    /// <summary>The upgrade messages to show this owner now. Empty for an owner whose plan already includes contact.</summary>
    Task<IReadOnlyList<Nudge>> GetAsync(string userId, string entityId, CancellationToken cancellationToken = default);

    Task<bool> DismissAsync(string userId, string entityId, string trigger, CancellationToken cancellationToken = default);
}

/// <summary>
/// The upgrade messages (rule 19): real counts only, taken from <see cref="BusinessStatsService"/>; no message for a zero; at most one per trigger per week; always dismissible.
/// A message carries a trigger and numbers, and the copy lives in the translation files, so it cannot invent urgency, name a competitor or say that paying raises a rank.
/// </summary>
public sealed class BusinessNudgeService(
    OwnershipRepository ownerships, NudgeRepository nudges, BusinessStatsService stats, IContactAccess access, IEntityCatalog catalog, IOptions<BusinessesOptions> options, TimeProvider time) : IBusinessNudgeService
{
    public const string Top3 = "top3";
    public const string Views = "views";
    public const string Waiting = "waiting";
    public const string Closed = "closed";
    public static readonly string[] Triggers = [Top3, Views, Waiting, Closed];

    public async Task<IReadOnlyList<Nudge>> GetAsync(string userId, string entityId, CancellationToken ct = default)
    {
        if (!await ownerships.ExistsAsync(userId, entityId, ct)) return [];
        if (await access.PlanShowsContactAsync(entityId, ct)) return [];
        var entry = (await catalog.GetActiveEntriesAsync([entityId], ct)).FirstOrDefault();
        if (entry is null) return [];

        var s = await stats.GetAsync(entityId, entry.CategoryId, ct);
        var counts = new Dictionary<string, Nudge?>
        {
            [Top3] = s is { CategoryRank: <= 3 and >= 1, RankedPlaces: >= 3 } ? new Nudge(Top3, s.CategoryRank.Value, s.CategoryRank, s.Area) : null,
            [Views] = s.ViewsLast7Days > 0 ? new Nudge(Views, s.ViewsLast7Days, null, null) : null,
            [Waiting] = s.OpenRequests > 0 ? new Nudge(Waiting, s.OpenRequests, null, null) : null,
            [Closed] = s.ClosedUnansweredLast30Days > 0 ? new Nudge(Closed, s.ClosedUnansweredLast30Days, null, null) : null,
        };

        var now = time.GetUtcNow().UtcDateTime;
        var span = TimeSpan.FromDays(options.Value.NudgeIntervalDays);
        var result = new List<Nudge>();
        foreach (var trigger in Triggers)
        {
            if (counts[trigger] is not { } nudge) continue;   // no message for a zero
            var latest = await nudges.LatestAsync(entityId, trigger, ct);
            if (latest is null || now - latest.CreatedAt >= span)
            {
                latest = new NudgeInstance { EntityId = entityId, Trigger = trigger, CreatedAt = now };
                await nudges.InsertAsync(latest, ct);
            }
            if (latest.DismissedAt is null) result.Add(nudge);
        }
        return result;
    }

    public async Task<bool> DismissAsync(string userId, string entityId, string trigger, CancellationToken ct = default)
    {
        if (!Triggers.Contains(trigger) || !await ownerships.ExistsAsync(userId, entityId, ct)) return false;
        var latest = await nudges.LatestAsync(entityId, trigger, ct);
        if (latest is null) return false;
        latest.DismissedAt ??= time.GetUtcNow().UtcDateTime;
        await nudges.ReplaceAsync(latest, ct);
        return true;
    }
}

public enum RequestFailure
{
    None, EntityNotFound, NotLocked, OwnPlace, InvalidText, PhoneRequired, TooManyToday, TooManyForProvider, AlreadyOpen, NotFound, PlanRequired, WrongState,
}

public sealed record RequestResult(RequestFailure Failure, ContactRequest? Request = null)
{
    public bool Ok => Failure == RequestFailure.None;
}

public sealed record RequestInput(string? What, string? When, bool? ShareWhatsApp);

public sealed record Alternative(string Id, string CategoryId, Ratable.Shared.Data.LocalizedText Name, IReadOnlyDictionary<string, string> Slugs, string? City, double? Score, double? DistanceMeters);

public sealed record ReaderRequestView(
    string Id, string EntityId, string CategoryId, string What, string? When, bool ShareWhatsApp, string Status, DateTime CreatedAt, DateTime ExpiresAt,
    string? ReplyText, DateTime? RepliedAt, IReadOnlyList<Alternative> Alternatives);

public sealed record ProviderRequestView(string Id, string What, string? When, string Status, DateTime CreatedAt, DateTime ExpiresAt, string? ReaderName, string? WhatsApp, string? ReplyText, DateTime? RepliedAt);

/// <summary>What a provider sees. Without the plan: only how many requests there are (<see cref="Items"/> is empty and <see cref="Locked"/> is true).</summary>
public sealed record ProviderRequests(int Total, int Open, bool Locked, IReadOnlyList<ProviderRequestView> Items);

/// <summary>
/// Request contact (rule 19). A signed-in reader sends a short request to a locked provider and chooses to be reached through Ratable or to share their WhatsApp number with that
/// provider only. The request closes after 7 days (configurable) and the reader is offered other nearby providers. Without the plan the provider sees only the count; with it they
/// read and reply. Nothing here can change a score, a ranking or the nearby order.
/// </summary>
public sealed class ContactRequestService(
    ContactRequestRepository requests,
    OwnershipRepository ownerships,
    IEntityCatalog catalog,
    IContactAccess access,
    IAccountPhones phones,
    IPublicProfiles profiles,
    IBusinessReviewSource reviews,
    IOptions<BusinessesOptions> options,
    TimeProvider time)
{
    private BusinessesOptions Opt => options.Value;

    public async Task<RequestResult> CreateAsync(string readerId, string entityId, RequestInput input, CancellationToken ct = default)
    {
        var entry = (await catalog.GetActiveEntriesAsync([entityId], ct)).FirstOrDefault();
        if (entry is null) return new(RequestFailure.EntityNotFound);
        if (!await access.IsLockedAsync(entry.Id, entry.CategoryId, ct)) return new(RequestFailure.NotLocked);   // an open profile shows its contact
        if (await ownerships.ExistsAsync(readerId, entry.Id, ct)) return new(RequestFailure.OwnPlace);

        var what = input.What?.Trim();
        var when = string.IsNullOrWhiteSpace(input.When) ? null : input.When.Trim();
        if (string.IsNullOrEmpty(what) || what.Length < 3 || what.Length > Opt.MaxRequestTextLength || (when?.Length ?? 0) > 100) return new(RequestFailure.InvalidText);
        var share = input.ShareWhatsApp == true;
        if (share && await phones.GetPhoneAsync(readerId, ct) is null) return new(RequestFailure.PhoneRequired);

        var now = time.GetUtcNow().UtcDateTime;
        if (await requests.CountByReaderSinceAsync(readerId, now.AddDays(-1), ct) >= Opt.MaxRequestsPerReaderPerDay) return new(RequestFailure.TooManyToday);
        if (await requests.CountByEntitySinceAsync(entry.Id, now.AddDays(-1), ct) >= Opt.MaxRequestsPerProviderPerDay) return new(RequestFailure.TooManyForProvider);
        if (await requests.HasOpenAsync(readerId, entry.Id, now, ct)) return new(RequestFailure.AlreadyOpen);

        var request = new ContactRequest
        {
            EntityId = entry.Id,
            CategoryId = entry.CategoryId,
            ReaderId = readerId,
            What = what,
            When = when,
            ShareWhatsApp = share,
            CreatedAt = now,
            ExpiresAt = now.AddDays(Opt.RequestLifeDays),
        };
        await requests.InsertAsync(request, ct);
        return new(RequestFailure.None, request);
    }

    private static RequestStatus Effective(ContactRequest r, DateTime now) => r.Status == RequestStatus.Open && r.ExpiresAt <= now ? RequestStatus.Closed : r.Status;

    public async Task<IReadOnlyList<ReaderRequestView>> MineAsync(string readerId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var list = await requests.ListByReaderAsync(readerId, ct);
        var views = new List<ReaderRequestView>();
        foreach (var r in list)
        {
            var status = Effective(r, now);
            // A request that closed without an answer offers other nearby providers of the same kind.
            var alternatives = status == RequestStatus.Closed && r.RepliedAt is null ? await AlternativesAsync(r, ct) : [];
            views.Add(new ReaderRequestView(r.Id, r.EntityId, r.CategoryId, r.What, r.When, r.ShareWhatsApp, status.ToString(), r.CreatedAt, r.ExpiresAt, r.ReplyText, r.RepliedAt, alternatives));
        }
        return views;
    }

    /// <summary>The closest other places of the category within 25 km, best verified score first and then nearest. Nothing a place pays for takes part in this order.</summary>
    private async Task<IReadOnlyList<Alternative>> AlternativesAsync(ContactRequest r, CancellationToken ct)
    {
        var me = (await catalog.GetActiveEntriesAsync([r.EntityId], ct)).FirstOrDefault();
        if (me is not { Longitude: { } lon, Latitude: { } lat }) return [];
        var near = (await catalog.ListNearAsync(lat, lon, 25_000, r.CategoryId, 30, ct)).Where(e => e.Id != r.EntityId).ToList();
        var scores = await reviews.GetScoresAsync([.. near.Select(e => e.Id)], ct);
        return [.. near
            .Select(e => new Alternative(e.Id, e.CategoryId, e.Name, e.Slugs, e.City, scores.GetValueOrDefault(e.Id), DistanceMeters(lat, lon, e.Latitude, e.Longitude)))
            .OrderByDescending(a => a.Score ?? -1).ThenBy(a => a.DistanceMeters ?? double.MaxValue)
            .Take(3)];
    }

    private static double? DistanceMeters(double lat1, double lon1, double? lat2, double? lon2)
    {
        if (lat2 is null || lon2 is null) return null;
        const double R = 6_371_000;
        double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2.Value - lat1);
        var dLon = Rad(lon2.Value - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2.Value)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return Math.Round(R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)));
    }

    /// <summary>The provider's side. Only the owner of the place; the plan decides whether the requests can be read.</summary>
    public async Task<ProviderRequests?> ForProviderAsync(string userId, string entityId, CancellationToken ct = default)
    {
        if (!await ownerships.ExistsAsync(userId, entityId, ct)) return null;
        var now = time.GetUtcNow().UtcDateTime;
        var list = await requests.ListByEntityAsync(entityId, ct);
        var open = list.Count(r => Effective(r, now) == RequestStatus.Open);
        if (!await access.PlanShowsContactAsync(entityId, ct)) return new ProviderRequests(list.Count, open, true, []);

        var names = await profiles.GetAsync([.. list.Select(r => r.ReaderId).Distinct()], ct);
        var items = new List<ProviderRequestView>();
        foreach (var r in list)
        {
            // The reader's number leaves their account only for the provider they chose, and only when they chose to share it.
            var whatsApp = r.ShareWhatsApp ? await phones.GetPhoneAsync(r.ReaderId, ct) : null;
            items.Add(new ProviderRequestView(r.Id, r.What, r.When, Effective(r, now).ToString(), r.CreatedAt, r.ExpiresAt,
                names.TryGetValue(r.ReaderId, out var p) && !string.IsNullOrWhiteSpace(p.DisplayName) ? p.DisplayName : null, whatsApp, r.ReplyText, r.RepliedAt));
        }
        return new ProviderRequests(list.Count, open, false, items);
    }

    public async Task<RequestResult> ReplyAsync(string userId, string entityId, string requestId, string? text, CancellationToken ct = default)
    {
        if (!await ownerships.ExistsAsync(userId, entityId, ct)) return new(RequestFailure.NotFound);
        if (!await access.PlanShowsContactAsync(entityId, ct)) return new(RequestFailure.PlanRequired);
        var request = await requests.GetAsync(requestId, ct);
        if (request is null || request.EntityId != entityId) return new(RequestFailure.NotFound);
        var now = time.GetUtcNow().UtcDateTime;
        if (Effective(request, now) != RequestStatus.Open) return new(RequestFailure.WrongState);
        var clean = text?.Trim();
        if (string.IsNullOrEmpty(clean) || clean.Length < 2 || clean.Length > Opt.MaxRequestTextLength * 3) return new(RequestFailure.InvalidText);
        request.ReplyText = clean;
        request.RepliedAt = now;
        request.Status = RequestStatus.Replied;
        await requests.ReplaceAsync(request, ct);
        return new(RequestFailure.None, request);
    }

    public Task<long> CloseExpiredAsync(CancellationToken ct = default) => requests.CloseExpiredAsync(time.GetUtcNow().UtcDateTime, ct);
}

/// <summary>Marks requests whose 7 days are up as closed. Reads already treat them as closed; this keeps the stored state honest.</summary>
public sealed class ContactRequestCloser(ContactRequestService service, ILogger<ContactRequestCloser> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try
            {
                var closed = await service.CloseExpiredAsync(stoppingToken);
                if (closed > 0) logger.LogInformation("Closed {Count} contact requests whose time was up", closed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Closing expired contact requests failed; it will try again later.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Owner-entered contact details and the plan on a place (admin grant until payments exist).</summary>
public sealed class ContactAdminService(
    OwnershipRepository ownerships, SubscriptionRepository subscriptions, IEntityContacts contacts, IOptions<BusinessesOptions> options, IAuditLog audit, TimeProvider time)
{
    public const int MaxLinks = 5;
    private static readonly Regex PhonePattern = new(@"^\+?[0-9٠-٩ \-()]{6,20}$", RegexOptions.Compiled);

    /// <summary>Saves the owner's contact details after checking their shape. Always editable by the owner; shown to the public only while the plan includes contact.</summary>
    public async Task<(bool Ok, string? Error)> SetContactAsync(string userId, string entityId, string? phone, string? whatsApp, IReadOnlyList<string>? links, CancellationToken ct = default)
    {
        if (!await ownerships.ExistsAsync(userId, entityId, ct)) return (false, "not_owner");
        string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var p = Clean(phone);
        var w = Clean(whatsApp);
        if ((p is not null && !PhonePattern.IsMatch(p)) || (w is not null && !PhonePattern.IsMatch(w))) return (false, "invalid_phone");
        var cleanLinks = (links ?? []).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        if (cleanLinks.Count > MaxLinks || cleanLinks.Any(l => l.Length > 300 || !Uri.TryCreate(l, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)))
            return (false, "invalid_link");
        var saved = await contacts.SetAsync(entityId, new ContactDetails(p, w, cleanLinks), ContactSource.Owner, ct);
        return saved ? (true, null) : (false, "not_found");
    }

    public async Task<ContactDetails?> GetContactAsync(string userId, string entityId, CancellationToken ct = default) =>
        await ownerships.ExistsAsync(userId, entityId, ct) ? await contacts.GetAsync(entityId, ct) : null;

    /// <summary>Gives a place a plan, or ends it. An audited admin action, never a browser redirect (rule 10). Payment will replace it.</summary>
    public async Task<(bool Ok, string? Error)> GrantAsync(string adminId, string entityId, string? planId, DateTime? validUntil, CancellationToken ct = default)
    {
        if (options.Value.Plans.All(p => p.Id != planId)) return (false, "unknown_plan");
        foreach (var existing in await subscriptions.ListForEntityAsync(entityId, ct))
        {
            existing.Status = SubscriptionStatus.Ended;
            await subscriptions.ReplaceAsync(existing, ct);
        }
        if (planId != "free")
            await subscriptions.InsertAsync(new BusinessSubscription { EntityId = entityId, PlanId = planId!, StartedAt = time.GetUtcNow().UtcDateTime, ValidUntil = validUntil, Source = "admin", GrantedBy = adminId }, ct);
        await audit.AppendAsync(new AuditRecord(adminId, "subscription.granted", "entity", entityId, $"plan {planId}"), ct);
        return (true, null);
    }
}
