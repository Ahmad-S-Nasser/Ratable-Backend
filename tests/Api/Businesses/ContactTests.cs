using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Businesses.Services;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Businesses;
using Ratable.Shared.Data;
using Ratable.Shared.Text;
using static Ratable.Api.Tests.Businesses.BusinessKit;
using static Ratable.Api.Tests.Moderation.ModerationKit;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Businesses;

public sealed class ContactTests
{
    private const string Phone = "+20 100 123 4567";
    private const string Handle = "@ahmed_plumber";

    private static readonly ContactDetails Details = new(Phone, "+201001234567", ["https://plumber.example/work"]);

    /// <summary>A service provider (a category that gates contact) in Cairo with contact details stored on it.</summary>
    private static async Task<Entity> Provider(IdentityApiFactory api, string name = "Ahmed Plumber", double lon = 31.64, double lat = 30.10, bool withContact = true)
    {
        var e = await SeedEntity(api, "service-providers");
        e.Name = new LocalizedText("أحمد السباك", name);
        e.City = "Cairo";
        e.Address = $"Next to the mosque, call {Phone}";
        e.Fields = new() { ["trade"] = "Plumber", ["serviceArea"] = $"Cairo and Giza, WhatsApp {Phone}" };
        e.SameAs = ["https://plumber.example/work"];
        e.Location = GeoJson.Point(GeoJson.Geographic(lon, lat));
        await api.Services.GetRequiredService<EntityRepository>().ReplaceAsync(e, DateTime.UtcNow);
        if (withContact) await api.Services.GetRequiredService<IEntityContacts>().SetAsync(e.Id, Details, ContactSource.Import);
        return e;
    }

    private static Task<(HttpResponseMessage Response, JsonElement Json)> Grant(IdentityApiFactory api, IdentityApiFactory.SignedIn admin, string entityId, string plan = "contact", DateTime? until = null) =>
        Send(api, HttpMethod.Put, $"/api/admin/entities/{entityId}/subscription", admin.Cookie, new { planId = plan, validUntil = until });

    private static async Task<string> ProviderReview(IdentityApiFactory api, IdentityApiFactory.SignedIn reader, string entityId, int score = 5, string text = "Fixed the leak in one visit")
    {
        var id = await CreateReview(api, reader.Cookie!, entityId, new { answers = Answers(("quality", score)), text }, state: null);
        await SetReviewState(api, id, ReviewStatus.Published, VerificationLevel.Verified);
        return id;
    }

    private static string Raw(JsonElement json) => json.GetRawText();

    // ---- the profile never carries contact when it is locked ----

    [MongoFact]
    public async Task A_locked_profile_never_returns_a_contact_field_in_the_entity_or_the_search_response()
    {
        await using var api = NewApi();
        var e = await Provider(api);

        var (_, one) = await Send(api, HttpMethod.Get, $"/api/entities/{e.Id}");
        var (_, found) = await Send(api, HttpMethod.Get, "/api/entities/search?q=" + Uri.EscapeDataString("Ahmed"));
        foreach (var json in new[] { Raw(one), Raw(found) })
        {
            Assert.False(json.Contains("100 123"), json);
            Assert.DoesNotContain("1001234567", json);
            Assert.DoesNotContain("plumber.example", json);
            Assert.DoesNotContain("\"whatsApp\"", json);   // no contact object, so no such key
        }
        Assert.True(one.GetProperty("contactLocked").GetBoolean());
        Assert.False(one.GetProperty("contactAvailable").GetBoolean());
        Assert.True(one.GetProperty("contact").ValueKind == JsonValueKind.Null);
        Assert.Equal(0, one.GetProperty("sameAs").GetArrayLength());
        Assert.Contains("Next to the mosque", one.GetProperty("address").GetString());   // the rest of the profile stays
        Assert.Equal("Plumber", one.GetProperty("fields").GetProperty("trade").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, HttpMethod.Post, $"/api/entities/{e.Id}/contact/reveal")).Response.StatusCode);
    }

    [MongoFact]
    public async Task A_category_that_does_not_gate_contact_shows_it_as_before()
    {
        await using var api = NewApi();
        var e = await SeedEntity(api);   // compounds: gating is off by default
        await api.Services.GetRequiredService<IEntityContacts>().SetAsync(e.Id, Details, ContactSource.Import);
        var (_, json) = await Send(api, HttpMethod.Get, $"/api/entities/{e.Id}");
        Assert.False(json.GetProperty("contactLocked").GetBoolean());
        Assert.Equal(Phone, json.GetProperty("contact").GetProperty("phone").GetString());
    }

    [MongoFact]
    public async Task A_subscribed_profile_keeps_contact_out_of_the_response_and_shows_it_only_after_a_counted_tap()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var e = await Provider(api);
        Assert.Equal(HttpStatusCode.NoContent, (await Grant(api, admin, e.Id)).Response.StatusCode);

        var (_, one) = await Send(api, HttpMethod.Get, $"/api/entities/{e.Id}");
        Assert.False(one.GetProperty("contactLocked").GetBoolean());
        Assert.True(one.GetProperty("contactAvailable").GetBoolean());
        Assert.True(one.GetProperty("contact").ValueKind == JsonValueKind.Null);   // the details are not in the page data, only behind the call
        Assert.Equal(0, one.GetProperty("sameAs").GetArrayLength());
        Assert.DoesNotContain("plumber.example", Raw(one));

        var activity = api.Services.GetRequiredService<IEntityActivity>();
        Assert.Equal(0, (await activity.GetTotalsAsync(e.Id, DateTime.UtcNow.Date.AddDays(-1))).ContactTaps);
        var (response, shown) = await Send(api, HttpMethod.Post, $"/api/entities/{e.Id}/contact/reveal");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Phone, shown.GetProperty("phone").GetString());
        Assert.Equal(1, (await activity.GetTotalsAsync(e.Id, DateTime.UtcNow.Date.AddDays(-1))).ContactTaps);
    }

    [MongoFact]
    public async Task Show_contact_is_rate_limited_and_a_refused_call_is_not_counted()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var e = await Provider(api);
        await Grant(api, admin, e.Id);
        for (var i = 0; i < 20; i++) Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Post, $"/api/entities/{e.Id}/contact/reveal")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(api, HttpMethod.Post, $"/api/entities/{e.Id}/contact/reveal")).Response.StatusCode);
        Assert.Equal(20, (await api.Services.GetRequiredService<IEntityActivity>().GetTotalsAsync(e.Id, DateTime.UtcNow.Date.AddDays(-1))).ContactTaps);
        api.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Post, $"/api/entities/{e.Id}/contact/reveal")).Response.StatusCode);
    }

    [MongoFact]
    public async Task An_ended_or_expired_plan_locks_the_profile_again()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var e = await Provider(api);
        await Grant(api, admin, e.Id, until: DateTime.UtcNow.AddMinutes(30));
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Post, $"/api/entities/{e.Id}/contact/reveal")).Response.StatusCode);
        api.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, HttpMethod.Post, $"/api/entities/{e.Id}/contact/reveal")).Response.StatusCode);

        await Grant(api, admin, e.Id);                    // granted again
        await Grant(api, admin, e.Id, plan: "free");      // and ended
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, HttpMethod.Post, $"/api/entities/{e.Id}/contact/reveal")).Response.StatusCode);
    }

    [MongoFact]
    public async Task Only_an_admin_grants_a_plan_it_must_exist_and_it_is_audited()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var user = await Business(api);
        var e = await Provider(api);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Put, $"/api/admin/entities/{e.Id}/subscription", null, new { planId = "contact" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Grant(api, user, e.Id)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Grant(api, admin, e.Id, plan: "platinum")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Grant(api, admin, e.Id)).Response.StatusCode);
        Assert.Contains("subscription.granted", Raw((await Send(api, HttpMethod.Get, "/api/admin/audit?limit=20", admin.Cookie)).Json));
    }

    // ---- rule 19: gating never changes a ranking ----

    [MongoFact]
    public async Task Ranking_and_scores_are_identical_with_and_without_the_plan()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var a = await Provider(api, "Provider A", 31.64, 30.10);
        var b = await Provider(api, "Provider B", 31.65, 30.11);
        var reader = await api.SignInAsync("+201111111111");
        var reader2 = await api.SignInAsync("+201222222222");
        await ProviderReview(api, reader, a.Id, 4);
        await ProviderReview(api, reader, b.Id, 5);
        await ProviderReview(api, reader2, a.Id, 3);

        string Order(JsonElement list) => string.Join(",", list.GetProperty("places").EnumerateArray().Select(p => $"{p.GetProperty("id").GetString()}:{p.GetProperty("overall").GetDouble()}:{p.GetProperty("verifiedReviews").GetInt32()}"));
        var before = Order((await Send(api, HttpMethod.Get, "/api/categories/service-providers/places")).Json);
        var nearBefore = Raw((await Send(api, HttpMethod.Get, "/api/places?lat=30.10&lng=31.64&radiusKm=10&category=service-providers")).Json);
        var scoreBefore = Raw((await Send(api, HttpMethod.Get, $"/api/entities/{a.Id}/score")).Json);

        await Grant(api, admin, a.Id);   // the lower-scored provider gets the plan

        Assert.Equal(before, Order((await Send(api, HttpMethod.Get, "/api/categories/service-providers/places")).Json));
        Assert.Equal(nearBefore, Raw((await Send(api, HttpMethod.Get, "/api/places?lat=30.10&lng=31.64&radiusKm=10&category=service-providers")).Json));
        Assert.Equal(scoreBefore, Raw((await Send(api, HttpMethod.Get, $"/api/entities/{a.Id}/score")).Json));
        Assert.StartsWith(b.Id, before);   // and B, with the better score, is still first
    }

    // ---- contact typed into text is removed from locked profiles ----

    [MongoFact]
    public async Task Contact_typed_into_a_review_a_comment_or_a_reply_is_removed_while_the_profile_is_locked()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var e = await Provider(api);
        var owner = await Business(api);
        await MakeOwner(api, owner, e.Id);
        var reader = await api.SignInAsync("+201111111111");
        var text = $"Great work. Call him on 0100 123 4567 or {Handle} or https://wa.me/201001234567 and ask for Ahmed";
        var reviewId = await ProviderReview(api, reader, e.Id, 5, text);
        api.Ai.Enqueue(Clean());
        var (commented, _) = await Send(api, HttpMethod.Post, $"/api/reviews/{reviewId}/comments", reader.Cookie, new { text = "Same here, his number is +20 100 123 4567" });
        Assert.Equal(HttpStatusCode.Created, commented.StatusCode);
        await api.Database.GetCollection<MongoDB.Bson.BsonDocument>(Collections.Comments).UpdateOneAsync(new MongoDB.Bson.BsonDocument(), MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Update.Set("status", "Published"));
        api.Ai.Enqueue(Clean());
        await Send(api, HttpMethod.Post, $"/api/reviews/{reviewId}/owner-reply", owner.Cookie, new { text = "Thanks! Reach us on 01001234567" });
        await api.Database.GetCollection<MongoDB.Bson.BsonDocument>(Collections.OwnerReplies).UpdateOneAsync(new MongoDB.Bson.BsonDocument(), MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Update.Set("status", "Published"));

        var locked = Raw((await Send(api, HttpMethod.Get, $"/api/entities/{e.Id}/reviews")).Json) + Raw((await Send(api, HttpMethod.Get, $"/api/reviews/{reviewId}/comments")).Json);
        Assert.Contains("Great work", locked);
        foreach (var leak in new[] { "0100 123", "1001234567", Handle, "wa.me" }) Assert.DoesNotContain(leak, locked);

        await Grant(api, admin, e.Id);   // with the plan the text is shown as written
        var open = Raw((await Send(api, HttpMethod.Get, $"/api/entities/{e.Id}/reviews")).Json);
        Assert.Contains("0100 123 4567", open);
    }

    [Fact]
    public void The_stripper_removes_phones_in_every_common_form_and_keeps_short_numbers()
    {
        foreach (var input in new[]
                 {
                     "call 01001234567 now", "call 0100 123 4567 now", "call +20 100 123 4567 now", "call 0020-100-123-4567 now", "call ٠١٠٠١٢٣٤٥٦٧ now",
                     "write to ahmed@mail.com now", "write @ahmed_plumber now", "open https://wa.me/20100123 now", "see www.plumber.example/x now", "t.me/ahmed",
                 })
        {
            var stripped = ContactStripper.Strip(input);
            Assert.NotEqual(input, stripped);
            Assert.Contains(ContactStripper.Mask, stripped);
        }
        foreach (var keep in new[] { "paid 1200 pounds in 2024", "room 305 on floor 3", "he came at 10.30 for 15 minutes", "rated 4.5 out of 5", "منذ ٣ أيام بمبلغ ٢٥٠٠ جنيه" })
            Assert.Equal(keep, ContactStripper.Strip(keep));
    }

    // ---- request contact ----

    private sealed record World(IdentityApiFactory Api, IdentityApiFactory.SignedIn Admin, Entity Provider, IdentityApiFactory.SignedIn Owner, IdentityApiFactory.SignedIn Reader);

    private static async Task<World> Setup(Dictionary<string, string>? settings = null)
    {
        var api = NewApi(settings);
        var admin = await api.SignInAsync(AdminPhone);
        var e = await Provider(api);
        var owner = await Business(api);
        await MakeOwner(api, owner, e.Id);
        var reader = await api.SignInAsync("+201111111111");
        await SetProfile(api, reader.Cookie!, "Mona Hassan");
        return new World(api, admin, e, owner, reader);
    }

    private static Task<(HttpResponseMessage Response, JsonElement Json)> Ask(World w, IdentityApiFactory.SignedIn? who = null, string what = "A leaking kitchen tap", bool share = false, string? entityId = null) =>
        Send(w.Api, HttpMethod.Post, $"/api/entities/{entityId ?? w.Provider.Id}/contact-requests", (who ?? w.Reader).Cookie, new { what, when = "This week", shareWhatsApp = share });

    [MongoFact]
    public async Task A_signed_in_reader_asks_a_locked_provider_once_and_the_rules_are_enforced()
    {
        var w = await Setup();
        await using var _ = w.Api;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(w.Api, HttpMethod.Post, $"/api/entities/{w.Provider.Id}/contact-requests", null, new { what = "A leaking tap" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(w, what: "x")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Ask(w, entityId: NewId())).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Ask(w, w.Owner)).Response.StatusCode);                       // not for the provider's own place

        var (created, body) = await Ask(w);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Open", body.GetProperty("status").GetString());
        Assert.Equal(7, (body.GetProperty("expiresAt").GetDateTime() - body.GetProperty("createdAt").GetDateTime()).TotalDays, 1);
        Assert.Equal(HttpStatusCode.Conflict, (await Ask(w)).Response.StatusCode);                                // one open request per provider

        // A profile that shows its contact needs no request.
        await Grant(w.Api, w.Admin, w.Provider.Id);
        var (open, openBody) = await Ask(w, await w.Api.SignInAsync("+201333333333"));
        Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);
        Assert.Equal("not_locked", openBody.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task Limits_apply_per_reader_per_day_and_per_provider_per_day()
    {
        var w = await Setup(new() { ["Businesses:MaxRequestsPerReaderPerDay"] = "2", ["Businesses:MaxRequestsPerProviderPerDay"] = "3" });
        await using var _ = w.Api;
        var p2 = await Provider(w.Api, "Second", 31.70, 30.20);
        var p3 = await Provider(w.Api, "Third", 31.71, 30.21);
        Assert.Equal(HttpStatusCode.Created, (await Ask(w)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Ask(w, entityId: p2.Id)).Response.StatusCode);
        var (third, body) = await Ask(w, entityId: p3.Id);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("too_many_today", body.GetProperty("error").GetString());
        w.Api.Time.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(1)));
        Assert.Equal(HttpStatusCode.Created, (await Ask(w, entityId: p3.Id)).Response.StatusCode);

        // A busy provider: the third reader of the day is turned away.
        var many = await Setup(new() { ["Businesses:MaxRequestsPerProviderPerDay"] = "2" });
        await using var __ = many.Api;
        Assert.Equal(HttpStatusCode.Created, (await Ask(many)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Ask(many, await many.Api.SignInAsync("+201444444444"))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Ask(many, await many.Api.SignInAsync("+201555444444"))).Response.StatusCode);
    }

    [MongoFact]
    public async Task Without_the_plan_the_provider_sees_only_how_many_requests_exist()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Ask(w, share: true);
        await Ask(w, await w.Api.SignInAsync("+201333333333"), what: "Replace a bathroom mixer");

        var (_, view) = await Send(w.Api, HttpMethod.Get, $"/api/business/places/{w.Provider.Id}/contact-requests", w.Owner.Cookie);
        Assert.True(view.GetProperty("locked").GetBoolean());
        Assert.Equal(2, view.GetProperty("total").GetInt32());
        Assert.Equal(2, view.GetProperty("open").GetInt32());
        Assert.Equal(0, view.GetProperty("items").GetArrayLength());
        Assert.DoesNotContain("leaking", Raw(view));
        Assert.DoesNotContain("Mona", Raw(view));
        var (reply, body) = await Send(w.Api, HttpMethod.Post, $"/api/business/places/{w.Provider.Id}/contact-requests/{NewId()}/reply", w.Owner.Cookie, new { text = "I can come tomorrow" });
        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.Equal("plan_required", body.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task A_readers_number_is_shared_only_with_the_chosen_provider_and_only_when_the_reader_chose_to()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var otherProvider = await Provider(w.Api, "Another Plumber", 31.80, 30.30);
        var otherOwner = await Business(w.Api, "+201555555552");
        await MakeOwner(w.Api, otherOwner, otherProvider.Id);

        var asker2 = await w.Api.SignInAsync("+201333333333");
        Assert.Equal(HttpStatusCode.Created, (await Ask(w, w.Reader, "Fix a tap", share: true)).Response.StatusCode);          // shares the number
        Assert.Equal(HttpStatusCode.Created, (await Ask(w, asker2, "Fix a sink", share: false)).Response.StatusCode);          // through Ratable only
        await Grant(w.Api, w.Admin, w.Provider.Id);
        await Grant(w.Api, w.Admin, otherProvider.Id);

        var (_, mine) = await Send(w.Api, HttpMethod.Get, $"/api/business/places/{w.Provider.Id}/contact-requests", w.Owner.Cookie);
        var items = mine.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("+201111111111", items.Single(i => i.GetProperty("what").GetString() == "Fix a tap").GetProperty("whatsApp").GetString());
        Assert.True(items.Single(i => i.GetProperty("what").GetString() == "Fix a sink").GetProperty("whatsApp").ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain("+201333333333", Raw(mine));    // the second reader never shared theirs

        // The other provider sees none of it, and cannot open this provider's requests.
        var (_, theirs) = await Send(w.Api, HttpMethod.Get, $"/api/business/places/{otherProvider.Id}/contact-requests", otherOwner.Cookie);
        Assert.Equal(0, theirs.GetProperty("total").GetInt32());
        Assert.DoesNotContain("+20111", Raw(theirs));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Get, $"/api/business/places/{w.Provider.Id}/contact-requests", otherOwner.Cookie)).Response.StatusCode);
        // And the number is not in any public response.
        Assert.DoesNotContain("+201111111111", Raw((await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.Provider.Id}")).Json));
    }

    [MongoFact]
    public async Task Sharing_a_whatsapp_number_needs_an_account_with_a_number()
    {
        var w = await Setup();
        await using var _ = w.Api;
        // Sign-in always has a phone in the product; an account without one cannot choose to share it.
        var users = w.Api.Services.GetRequiredService<Ratable.Modules.Identity.Data.UserRepository>();
        var reader = (await users.ListByIdsAsync([IdOf(w.Reader)])).Single();
        reader.PhoneEncrypted = null;
        await users.ReplaceAsync(reader);
        var (response, body) = await Ask(w, share: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("phone_required", body.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task A_request_closes_after_seven_days_and_offers_other_nearby_providers_best_score_first()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var near = await Provider(w.Api, "Nearby Low", 31.641, 30.101);
        var far = await Provider(w.Api, "Nearby High", 31.66, 30.12);
        var reviewer = await w.Api.SignInAsync("+201222222222");
        await ProviderReview(w.Api, reviewer, near.Id, 3);
        await ProviderReview(w.Api, reviewer, far.Id, 5);
        await Ask(w);

        var (_, fresh) = await Send(w.Api, HttpMethod.Get, "/api/me/contact-requests", w.Reader.Cookie);
        Assert.Equal("Open", fresh[0].GetProperty("status").GetString());
        Assert.Equal(0, fresh[0].GetProperty("alternatives").GetArrayLength());

        w.Api.Time.Advance(TimeSpan.FromDays(7).Add(TimeSpan.FromMinutes(1)));
        var (_, closed) = await Send(w.Api, HttpMethod.Get, "/api/me/contact-requests", w.Reader.Cookie);
        Assert.Equal("Closed", closed[0].GetProperty("status").GetString());
        var offered = closed[0].GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("id").GetString()).ToList();
        Assert.Equal([far.Id, near.Id], offered);                       // best verified score first, and the provider asked is not offered
        Assert.DoesNotContain(w.Provider.Id, offered);
        var (reply, _) = await Send(w.Api, HttpMethod.Post, $"/api/business/places/{w.Provider.Id}/contact-requests/{closed[0].GetProperty("id").GetString()}/reply", w.Owner.Cookie, new { text = "Too late" });
        Assert.NotEqual(HttpStatusCode.OK, reply.StatusCode);

        // The stored state follows too (the background closer does this on a timer).
        Assert.Equal(1, await w.Api.Services.GetRequiredService<ContactRequestService>().CloseExpiredAsync());
    }

    [MongoFact]
    public async Task A_replied_request_shows_the_providers_answer_to_the_reader_and_never_offers_alternatives()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var (_, created) = await Ask(w);
        var requestId = created.GetProperty("id").GetString();
        await Grant(w.Api, w.Admin, w.Provider.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Api, HttpMethod.Post, $"/api/business/places/{w.Provider.Id}/contact-requests/{requestId}/reply", w.Reader.Cookie, new { text = "Hi" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(w.Api, HttpMethod.Post, $"/api/business/places/{w.Provider.Id}/contact-requests/{requestId}/reply", w.Owner.Cookie, new { text = "I can come tomorrow at 10" })).Response.StatusCode);
        var (_, mine) = await Send(w.Api, HttpMethod.Get, "/api/me/contact-requests", w.Reader.Cookie);
        Assert.Equal("Replied", mine[0].GetProperty("status").GetString());
        Assert.Equal("I can come tomorrow at 10", mine[0].GetProperty("replyText").GetString());
        w.Api.Time.Advance(TimeSpan.FromDays(10));
        Assert.Equal(0, (await Send(w.Api, HttpMethod.Get, "/api/me/contact-requests", w.Reader.Cookie)).Json[0].GetProperty("alternatives").GetArrayLength());
    }

    // ---- stats and the upgrade messages ----

    [MongoFact]
    public async Task Page_views_are_counted_once_an_hour_per_visitor_and_robots_are_ignored()
    {
        await using var api = NewApi();
        var e = await Provider(api);
        var activity = api.Services.GetRequiredService<IEntityActivity>();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.NoContent, (await Send(api, HttpMethod.Post, $"/api/entities/{e.Id}/views")).Response.StatusCode);
        Assert.Equal(1, (await activity.GetTotalsAsync(e.Id, DateTime.UtcNow.Date.AddDays(-1))).Views);
        api.Time.Advance(TimeSpan.FromHours(2));
        await Send(api, HttpMethod.Post, $"/api/entities/{e.Id}/views");
        Assert.Equal(2, (await activity.GetTotalsAsync(e.Id, DateTime.UtcNow.Date.AddDays(-1))).Views);

        using var http = api.CreateClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Googlebot/2.1");
        api.Time.Advance(TimeSpan.FromHours(2));
        await http.PostAsync($"/api/entities/{e.Id}/views", null);
        Assert.Equal(2, (await activity.GetTotalsAsync(e.Id, DateTime.UtcNow.Date.AddDays(-1))).Views);
    }

    [MongoFact]
    public async Task The_brief_stats_belong_to_the_plan_and_match_the_counts_behind_them()
    {
        var w = await Setup();
        await using var _ = w.Api;
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Api, HttpMethod.Get, $"/api/business/places/{w.Provider.Id}/stats", w.Owner.Cookie)).Response.StatusCode);
        await Send(w.Api, HttpMethod.Post, $"/api/entities/{w.Provider.Id}/views");
        await Ask(w);
        await Grant(w.Api, w.Admin, w.Provider.Id);
        await Send(w.Api, HttpMethod.Post, $"/api/entities/{w.Provider.Id}/contact/reveal");

        var (response, json) = await Send(w.Api, HttpMethod.Get, $"/api/business/places/{w.Provider.Id}/stats", w.Owner.Cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var s = json.GetProperty("stats");
        Assert.Equal(1, s.GetProperty("viewsLast7Days").GetInt32());
        Assert.Equal(1, s.GetProperty("contactTapsLast30Days").GetInt32());
        Assert.Equal(1, s.GetProperty("requestsLast30Days").GetInt32());
        Assert.Equal(1, s.GetProperty("openRequests").GetInt32());
        Assert.Equal(6, s.GetProperty("ratingTrend").GetArrayLength());
        Assert.False(Raw(json).Contains("recommend", StringComparison.OrdinalIgnoreCase));   // no AI analytics or recommendations on this plan
    }

    private static async Task<List<JsonElement>> Nudges(World w) =>
        [.. (await Send(w.Api, HttpMethod.Get, $"/api/business/places/{w.Provider.Id}/nudges", w.Owner.Cookie)).Json.GetProperty("nudges").EnumerateArray()];

    [MongoFact]
    public async Task There_is_no_message_for_a_zero_and_a_message_uses_only_numbers_that_are_in_the_stats()
    {
        var w = await Setup();
        await using var _ = w.Api;
        Assert.Empty(await Nudges(w));                           // nothing to say yet

        await Send(w.Api, HttpMethod.Post, $"/api/entities/{w.Provider.Id}/views");
        await Ask(w);
        await Ask(w, await w.Api.SignInAsync("+201333333333"), what: "Replace a mixer");
        var nudges = await Nudges(w);
        var stats = await w.Api.Services.GetRequiredService<BusinessStatsService>().GetAsync(w.Provider.Id, "service-providers");
        var byTrigger = nudges.ToDictionary(n => n.GetProperty("trigger").GetString()!, n => n.GetProperty("count").GetInt32());

        Assert.Equal(stats.ViewsLast7Days, byTrigger["views"]);
        Assert.Equal(stats.OpenRequests, byTrigger["waiting"]);
        Assert.Equal(2, byTrigger["waiting"]);
        Assert.DoesNotContain("closed", byTrigger.Keys);          // no request has closed: zero, so no message
        Assert.DoesNotContain("top3", byTrigger.Keys);            // no verified score yet
        // Messages carry numbers and a trigger, never prose: the words live in the translation files.
        Assert.All(nudges, n => Assert.DoesNotContain(n.EnumerateObject().Select(p => p.Name), name => name is "text" or "message" or "title"));

        w.Api.Time.Advance(TimeSpan.FromDays(8));                // both requests close unanswered
        var later = (await Nudges(w)).ToDictionary(n => n.GetProperty("trigger").GetString()!, n => n.GetProperty("count").GetInt32());
        Assert.Equal((await w.Api.Services.GetRequiredService<BusinessStatsService>().GetAsync(w.Provider.Id, "service-providers")).ClosedUnansweredLast30Days, later["closed"]);
        Assert.Equal(2, later["closed"]);
        Assert.DoesNotContain("waiting", later.Keys);
    }

    [MongoFact]
    public async Task A_message_is_made_once_a_week_per_trigger_and_can_always_be_dismissed()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Send(w.Api, HttpMethod.Post, $"/api/entities/{w.Provider.Id}/views");
        Assert.Single(await Nudges(w));
        Assert.Single(await Nudges(w));                          // still there until it is dismissed, not a second one

        Assert.Equal(HttpStatusCode.NoContent, (await Send(w.Api, HttpMethod.Post, $"/api/business/places/{w.Provider.Id}/nudges/views/dismiss", w.Owner.Cookie)).Response.StatusCode);
        Assert.Empty(await Nudges(w));
        w.Api.Time.Advance(TimeSpan.FromDays(3));
        await Send(w.Api, HttpMethod.Post, $"/api/entities/{w.Provider.Id}/views");   // a new view, but the week is not over
        Assert.Empty(await Nudges(w));
        w.Api.Time.Advance(TimeSpan.FromDays(5));
        await Send(w.Api, HttpMethod.Post, $"/api/entities/{w.Provider.Id}/views");
        Assert.Single(await Nudges(w));                          // a week later there may be a new one

        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Post, $"/api/business/places/{w.Provider.Id}/nudges/other/dismiss", w.Owner.Cookie)).Response.StatusCode);
        var stranger = await Business(w.Api, "+201555555553");
        await MakeOwner(w.Api, stranger, (await Provider(w.Api, "Elsewhere", 31.9, 30.4)).Id);   // a business, but not of this place
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Get, $"/api/business/places/{w.Provider.Id}/nudges", stranger.Cookie)).Response.StatusCode);
    }

    [MongoFact]
    public async Task The_top_three_message_needs_a_real_top_three_place_and_an_owner_with_the_plan_gets_no_upgrade_messages()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var p2 = await Provider(w.Api, "Second", 31.65, 30.11);
        var p3 = await Provider(w.Api, "Third", 31.66, 30.12);
        var r1 = await w.Api.SignInAsync("+201222222222");
        await ProviderReview(w.Api, r1, w.Provider.Id, 5);
        await ProviderReview(w.Api, r1, p2.Id, 4);
        await ProviderReview(w.Api, r1, p3.Id, 3);

        var top = (await Nudges(w)).Single(n => n.GetProperty("trigger").GetString() == "top3");
        Assert.Equal(1, top.GetProperty("count").GetInt32());
        Assert.Equal(1, top.GetProperty("rank").GetInt32());
        Assert.Equal("Cairo", top.GetProperty("area").GetString());

        await Grant(w.Api, w.Admin, w.Provider.Id);
        Assert.Empty(await Nudges(w));
    }

    // ---- the owner's contact details ----

    [MongoFact]
    public async Task The_owner_edits_the_contact_details_with_checks_and_other_accounts_cannot()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var url = $"/api/business/places/{w.Provider.Id}/contact";
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Api, HttpMethod.Get, url, w.Reader.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(w.Api, HttpMethod.Put, url, w.Owner.Cookie, new { phone = "call me" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(w.Api, HttpMethod.Put, url, w.Owner.Cookie, new { links = new[] { "javascript:alert(1)" } })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(w.Api, HttpMethod.Put, url, w.Owner.Cookie, new { links = Enumerable.Range(0, 6).Select(i => $"https://example.com/{i}").ToArray() })).Response.StatusCode);

        var (saved, json) = await Send(w.Api, HttpMethod.Put, url, w.Owner.Cookie, new { phone = "+20 122 000 1111", whatsApp = "+201220001111", links = new[] { "https://newplumber.example" } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("https://newplumber.example", json.GetProperty("links")[0].GetString());
        // Saved, but still hidden from everyone while the profile is locked.
        Assert.DoesNotContain("122 000", Raw((await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.Provider.Id}")).Json));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Api, HttpMethod.Post, $"/api/entities/{w.Provider.Id}/contact/reveal")).Response.StatusCode);
    }
}
