using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Modules.Businesses.Data;
using Ratable.Modules.Identity.Data;
using Ratable.Modules.Identity.Models;
using Ratable.Shared.Data;
using static Ratable.Api.Tests.Businesses.BusinessKit;
using static Ratable.Api.Tests.Moderation.ModerationKit;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Businesses;

public sealed class ClaimTests
{
    [MongoFact]
    public async Task A_claim_needs_an_account_a_verified_phone_and_a_real_company_email()
    {
        await using var api = NewApi();
        var entity = await SeedEntity(api);
        var unverified = await Business(api, "+201555555551", verified: false);
        var verified = await Business(api, "+201555555552");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Post, $"/api/entities/{entity.Id}/claims", null, new { companyEmail = "a@b-co.com" })).Response.StatusCode);
        var (noPhone, body) = await Claim(api, unverified.Cookie!, entity.Id);
        Assert.Equal(HttpStatusCode.Forbidden, noPhone.StatusCode);
        Assert.Equal("phone_not_verified", body.GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await Claim(api, verified.Cookie!, entity.Id, "not-an-email")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Claim(api, verified.Cookie!, entity.Id, role: "king")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Claim(api, verified.Cookie!, NewId())).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Claim(api, verified.Cookie!, entity.Id)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Claim(api, verified.Cookie!, entity.Id)).Response.StatusCode);   // one open claim per place
    }

    // ---- the company email is confirmed with a code ----

    [MongoFact]
    public async Task The_code_is_mailed_from_support_works_once_and_is_never_stored_in_clear()
    {
        await using var api = NewApi();
        var entity = await SeedEntity(api);
        var user = await Business(api);
        var (_, created) = await Claim(api, user.Cookie!, entity.Id);
        var id = created.GetProperty("id").GetString()!;
        Assert.False(created.GetProperty("emailVerified").GetBoolean());

        var mail = File.ReadAllText(new DirectoryInfo(api.MailDirectory).GetFiles("*.txt").Single().FullName);
        Assert.Contains("From: support@coon.one", mail);
        var code = MailedCode(api, "owner@madinaty-co.com")!;
        Assert.Equal(6, code.Length);
        var stored = (await api.Services.GetRequiredService<ClaimRepository>().GetAsync(id))!;
        Assert.DoesNotContain(code, System.Text.Json.JsonSerializer.Serialize(stored));   // only a hash is kept

        Assert.Equal(HttpStatusCode.OK, (await ConfirmEmail(api, user.Cookie!, id, code)).Response.StatusCode);
        Assert.True((await Send(api, HttpMethod.Get, "/api/me/claims", user.Cookie)).Json[0].GetProperty("emailVerified").GetBoolean());
        // The code is single use: it is gone once it worked.
        Assert.Null((await api.Services.GetRequiredService<ClaimRepository>().GetAsync(id))!.EmailCodeHash);
    }

    [MongoFact]
    public async Task A_code_expires_after_ten_minutes_and_a_new_one_replaces_the_old()
    {
        await using var api = NewApi();
        var entity = await SeedEntity(api);
        var user = await Business(api);
        var (_, created) = await Claim(api, user.Cookie!, entity.Id);
        var id = created.GetProperty("id").GetString()!;
        var first = MailedCode(api, "owner@madinaty-co.com")!;

        api.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(api, HttpMethod.Post, $"/api/me/claims/{id}/email/resend", user.Cookie)).Response.StatusCode);   // not so soon
        api.Time.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Post, $"/api/me/claims/{id}/email/resend", user.Cookie)).Response.StatusCode);
        var second = MailedCode(api, "owner@madinaty-co.com")!;
        Assert.NotEqual(first, second);
        if (first != second) Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmEmail(api, user.Cookie!, id, first)).Response.StatusCode);

        api.Time.Advance(TimeSpan.FromMinutes(11));
        var (expired, body) = await ConfirmEmail(api, user.Cookie!, id, second);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal("code_expired", body.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task Five_wrong_codes_kill_the_code_and_another_account_cannot_confirm_it()
    {
        await using var api = NewApi();
        var entity = await SeedEntity(api);
        var user = await Business(api);
        var other = await Business(api, "+201555555558");
        var (_, created) = await Claim(api, user.Cookie!, entity.Id);
        var id = created.GetProperty("id").GetString()!;
        var code = MailedCode(api, "owner@madinaty-co.com")!;
        var wrong = code == "000000" ? "111111" : "000000";

        Assert.Equal(HttpStatusCode.NotFound, (await ConfirmEmail(api, other.Cookie!, id, code)).Response.StatusCode);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmEmail(api, user.Cookie!, id, wrong)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ConfirmEmail(api, user.Cookie!, id, code)).Response.StatusCode);   // even the right code is refused now
    }

    [MongoFact]
    public async Task With_no_mail_set_up_a_claim_is_refused_cleanly_and_nothing_is_left_behind()
    {
        await using var api = NewApi(new() { ["Email:OutboxPath"] = "" });
        var entity = await SeedEntity(api);
        var user = await Business(api);
        var (response, body) = await Claim(api, user.Cookie!, entity.Id);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("email_unavailable", body.GetProperty("error").GetString());
        Assert.Equal(0, (await Send(api, HttpMethod.Get, "/api/me/claims", user.Cookie)).Json.GetArrayLength());
    }

    [MongoFact]
    public async Task A_free_mail_address_is_flagged_for_the_moderator_and_not_refused()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var entity = await SeedEntity(api);
        var user = await Business(api);
        await PendingClaim(api, user, entity.Id, "someone@gmail.com");

        var queue = (await Send(api, HttpMethod.Get, "/api/moderation/claims", admin.Cookie)).Json;
        var claim = Assert.Single(queue.EnumerateArray());
        Assert.True(claim.GetProperty("freeEmail").GetBoolean());
        Assert.False(claim.GetProperty("domainMatchesPlace").GetBoolean());
    }

    [MongoFact]
    public async Task A_claim_reaches_the_moderator_only_after_a_real_document_is_uploaded()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var entity = await SeedEntity(api);
        var user = await Business(api);
        var other = await Business(api, "+201555555553");
        var (_, created) = await Claim(api, user.Cookie!, entity.Id);
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal(0, (await Send(api, HttpMethod.Get, "/api/moderation/claims", admin.Cookie)).Json.GetArrayLength());

        // The company email comes first: no document before it is confirmed.
        var (early, earlyBody) = await Upload(api, user.Cookie!, id, Png());
        Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
        Assert.Equal("email_not_verified", earlyBody.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, (await ConfirmEmail(api, user.Cookie!, id, MailedCode(api, "owner@madinaty-co.com"))).Response.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(api, user.Cookie!, id, "just text, not a document"u8.ToArray())).Response.StatusCode);   // judged by its first bytes
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(api, user.Cookie!, id, Png(), "selfie")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(api, other.Cookie!, id, Png())).Response.StatusCode);                                     // not someone else's claim
        Assert.Equal(HttpStatusCode.OK, (await Upload(api, user.Cookie!, id, Pdf())).Response.StatusCode);

        Assert.Equal(1, (await Send(api, HttpMethod.Get, "/api/moderation/claims", admin.Cookie)).Json.GetArrayLength());
        Assert.Equal("Pending", (await Send(api, HttpMethod.Get, "/api/me/claims", user.Cookie)).Json[0].GetProperty("status").GetString());
    }

    [MongoFact]
    public async Task Approving_makes_the_account_the_owner_deletes_the_document_and_keeps_only_its_hash()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var entity = await SeedEntity(api);
        var user = await Business(api);
        var claimId = await PendingClaim(api, user, entity.Id);
        var claims = api.Services.GetRequiredService<ClaimRepository>();
        var key = (await claims.GetAsync(claimId))!.DocumentKey!;
        var store = api.Services.GetRequiredService<Ratable.Shared.Storage.IPrivateFileStore>();
        Assert.True(await store.ExistsAsync(key));

        // Reading the document is audited first, and only moderators can do it.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, HttpMethod.Get, $"/api/moderation/claims/{claimId}/document", user.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Get, $"/api/moderation/claims/{claimId}/document", admin.Cookie)).Response.StatusCode);
        Assert.Single(await api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "claim.document_read")).ToListAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, HttpMethod.Post, $"/api/moderation/claims/{claimId}/approve", user.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Post, $"/api/moderation/claims/{claimId}/approve", admin.Cookie)).Response.StatusCode);

        var after = (await claims.GetAsync(claimId))!;
        Assert.Null(after.DocumentKey);
        Assert.False(string.IsNullOrEmpty(after.DocumentHash));
        Assert.False(await store.ExistsAsync(key));
        Assert.True(await api.Services.GetRequiredService<Ratable.Shared.Businesses.IEntityOwnership>().IsOwnerAsync(IdOf(user), entity.Id));
        var account = (await api.Services.GetRequiredService<UserRepository>().ListByIdsAsync([IdOf(user)])).Single();
        Assert.Contains(UserRole.BusinessOwner, account.Roles);
        Assert.Single(await api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "claim.approved")).ToListAsync());

        // A decided claim cannot be decided again, and the place now has an owner.
        Assert.Equal(HttpStatusCode.Conflict, (await Send(api, HttpMethod.Post, $"/api/moderation/claims/{claimId}/approve", admin.Cookie)).Response.StatusCode);
        var rival = await Business(api, "+201555555554");
        Assert.Equal(HttpStatusCode.Conflict, (await Claim(api, rival.Cookie!, entity.Id)).Response.StatusCode);
    }

    [MongoFact]
    public async Task Rejecting_needs_a_reason_deletes_the_document_and_gives_no_ownership()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var entity = await SeedEntity(api);
        var user = await Business(api);
        var claimId = await PendingClaim(api, user, entity.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Post, $"/api/moderation/claims/{claimId}/reject", admin.Cookie, new { reason = "" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Post, $"/api/moderation/claims/{claimId}/reject", admin.Cookie, new { reason = "The register belongs to another company" })).Response.StatusCode);

        Assert.Null((await api.Services.GetRequiredService<ClaimRepository>().GetAsync(claimId))!.DocumentKey);
        Assert.False(await api.Services.GetRequiredService<Ratable.Shared.Businesses.IEntityOwnership>().IsOwnerAsync(IdOf(user), entity.Id));
        var mine = (await Send(api, HttpMethod.Get, "/api/me/claims", user.Cookie)).Json[0];
        Assert.Equal("Rejected", mine.GetProperty("status").GetString());
        Assert.Equal("The register belongs to another company", mine.GetProperty("decisionReason").GetString());
    }

    [MongoFact]
    public async Task Revoking_takes_the_place_and_the_role_back()
    {
        await using var api = NewApi();
        var admin = await api.SignInAsync(AdminPhone);
        var entity = await SeedEntity(api);
        var user = await Business(api);
        await MakeOwner(api, user, entity.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Post, $"/api/moderation/ownerships/{entity.Id}/revoke", admin.Cookie, new { reason = "" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(api, HttpMethod.Post, $"/api/moderation/ownerships/{entity.Id}/revoke", admin.Cookie, new { reason = "The claim was made by a former employee" })).Response.StatusCode);

        Assert.False(await api.Services.GetRequiredService<Ratable.Shared.Businesses.IEntityOwnership>().IsOwnerAsync(IdOf(user), entity.Id));
        Assert.DoesNotContain(UserRole.BusinessOwner, (await api.Services.GetRequiredService<UserRepository>().ListByIdsAsync([IdOf(user)])).Single().Roles);
    }

    [MongoFact]
    public async Task The_pricing_page_lists_plans_and_none_of_them_touches_ranking()
    {
        await using var api = NewApi();
        var (response, body) = await Send(api, HttpMethod.Get, "/api/plans");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["EGP", "SAR", "AED"], body.GetProperty("currencies").EnumerateArray().Select(c => c.GetString()).ToList());
        var plans = body.GetProperty("plans");
        foreach (var plan in plans.EnumerateArray())
            foreach (var currency in new[] { "EGP", "SAR", "AED" }) Assert.True(plan.GetProperty("monthlyPrices").TryGetProperty(currency, out _));
        Assert.Equal(0, plans.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "free").GetProperty("monthlyPrices").GetProperty("EGP").GetDecimal());
        // 99 riyals, the same figure in dirhams, and the pound price converted from the riyal price at the configured rate (14, rounded to ten).
        var insights = plans.EnumerateArray().Single(x => x.GetProperty("id").GetString() == "insights").GetProperty("monthlyPrices");
        Assert.Equal(99, insights.GetProperty("SAR").GetDecimal());
        Assert.Equal(99, insights.GetProperty("AED").GetDecimal());
        Assert.Equal(1390, insights.GetProperty("EGP").GetDecimal());
        var ids = plans.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Contains("free", ids);
        // Rule 6: no plan feature can change a score, a ranking or a removal.
        var banned = new[] { "rank", "score", "boost", "remove", "hide", "sponsored" };
        foreach (var plan in plans.EnumerateArray())
            foreach (var feature in plan.GetProperty("features").EnumerateArray())
                Assert.DoesNotContain(banned, b => feature.GetString()!.Contains(b, StringComparison.OrdinalIgnoreCase));
    }
}
