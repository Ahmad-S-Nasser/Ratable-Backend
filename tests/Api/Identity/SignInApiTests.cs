using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Modules.Identity.Models;
using Ratable.Shared.Data;

namespace Ratable.Api.Tests.Identity;

public sealed class SignInApiTests
{
    private const string Phone = "+201012345678";

    private static async Task<string?> ErrorOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out var e) ? e.GetString() : null;

    // ---- sign-in basics ----

    [MongoFact]
    public async Task Signing_in_with_a_phone_creates_an_account_and_a_httponly_cookie_and_does_not_verify_the_phone()
    {
        await using var api = new IdentityApiFactory();
        var result = await api.SignInAsync(Phone);

        Assert.Equal(HttpStatusCode.OK, result.Response.StatusCode);
        Assert.NotNull(result.Cookie);
        Assert.Contains("httponly", result.Cookie!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", result.Cookie, StringComparison.OrdinalIgnoreCase);

        // Plan update 2: signing in alone must leave PhoneVerified false. Only IPhoneVerifier (P9h) may set it.
        var user = result.Body.GetProperty("user");
        Assert.False(user.GetProperty("phoneVerified").GetBoolean());
        var stored = await api.Database.GetCollection<User>(Collections.Users).Find(FilterDefinition<User>.Empty).SingleAsync();
        Assert.False(stored.PhoneVerified);
        Assert.Equal([UserRole.User], stored.Roles);
    }

    [MongoFact]
    public async Task The_phone_is_stored_encrypted_and_never_returned()
    {
        await using var api = new IdentityApiFactory();
        var result = await api.SignInAsync(Phone);

        var raw = await api.Database.GetCollection<BsonDocument>(Collections.Users).Find(new BsonDocument()).SingleAsync();
        Assert.DoesNotContain("1012345678", raw["phoneEncrypted"].AsString);
        Assert.Equal(64, raw["phoneHash"].AsString.Length);
        Assert.False(raw.Contains("phone"));

        using var http = api.CreateClient();
        var me = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, "/api/me", result.Cookie!));
        var text = await me.Content.ReadAsStringAsync();
        Assert.DoesNotContain("1012345678", text);
        Assert.DoesNotContain("phoneEncrypted", text);
        Assert.DoesNotContain("phoneHash", text);
        Assert.DoesNotContain("1012345678", await result.Response.Content.ReadAsStringAsync());
    }

    [MongoFact]
    public async Task One_account_per_phone_number_whatever_the_spelling()
    {
        await using var api = new IdentityApiFactory();
        var first = await api.SignInAsync("+201012345678");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var second = await api.SignInAsync("01012345678");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var third = await api.SignInAsync("٠١٠١٢٣٤٥٦٧٨");

        var ids = new[] { first, second, third }.Select(s => s.Body.GetProperty("user").GetProperty("id").GetString()).Distinct().ToList();
        Assert.Single(ids);
        Assert.Equal(1, await api.Database.GetCollection<User>(Collections.Users).CountDocumentsAsync(FilterDefinition<User>.Empty));
    }

    [MongoFact]
    public async Task Invalid_numbers_are_refused_and_nothing_is_sent()
    {
        await using var api = new IdentityApiFactory();
        using var http = api.CreateClient();
        var r = await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = "not a number" });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_phone", await ErrorOf(r));
        Assert.Empty(api.Sender.Sent);
    }

    [MongoFact]
    public async Task Blocked_temporary_number_prefixes_cannot_sign_in()
    {
        await using var api = new IdentityApiFactory(new() { ["Identity:BlockedPhonePrefixes:0"] = "+1555", ["Identity:BlockedPhonePrefixes:1"] = "+4474" });
        using var http = api.CreateClient();

        var blocked = await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = "+15551234567" });
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Equal("number_not_allowed", await ErrorOf(blocked));
        Assert.Empty(api.Sender.Sent);

        var allowed = await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone });
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
    }

    // ---- OTP rules ----

    [MongoFact]
    public async Task A_code_works_only_once()
    {
        await using var api = new IdentityApiFactory();
        using var http = api.CreateClient();
        await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone });
        var code = api.Sender.LastCode(Phone);

        var first = await http.PostAsJsonAsync("/api/auth/otp/verify", new { phone = Phone, code });
        var second = await http.PostAsJsonAsync("/api/auth/otp/verify", new { phone = Phone, code });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [MongoFact]
    public async Task Concurrent_uses_of_one_code_let_exactly_one_through()
    {
        await using var api = new IdentityApiFactory();
        using var http = api.CreateClient();
        await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone });
        var code = api.Sender.LastCode(Phone);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => http.PostAsJsonAsync("/api/auth/otp/verify", new { phone = Phone, code })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await api.Database.GetCollection<User>(Collections.Users).CountDocumentsAsync(FilterDefinition<User>.Empty));
    }

    [MongoFact]
    public async Task A_code_expires_after_ten_minutes()
    {
        await using var api = new IdentityApiFactory();
        using var http = api.CreateClient();
        await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone });
        var code = api.Sender.LastCode(Phone);

        api.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var late = await http.PostAsJsonAsync("/api/auth/otp/verify", new { phone = Phone, code });

        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Equal("code_expired", await ErrorOf(late));
    }

    [MongoFact]
    public async Task A_code_still_works_just_before_it_expires()
    {
        await using var api = new IdentityApiFactory();
        using var http = api.CreateClient();
        await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone });
        var code = api.Sender.LastCode(Phone);

        api.Time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/auth/otp/verify", new { phone = Phone, code })).StatusCode);
    }

    [MongoFact]
    public async Task After_five_wrong_tries_even_the_right_code_is_refused()
    {
        await using var api = new IdentityApiFactory();
        using var http = api.CreateClient();
        await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone });
        var code = api.Sender.LastCode(Phone);
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
            Assert.Equal("invalid_code", await ErrorOf(await http.PostAsJsonAsync("/api/auth/otp/verify", new { phone = Phone, code = wrong })));

        var right = await http.PostAsJsonAsync("/api/auth/otp/verify", new { phone = Phone, code });
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
        Assert.Equal("too_many_attempts", await ErrorOf(right));
    }

    [MongoFact]
    public async Task A_code_for_one_number_does_not_work_for_another()
    {
        await using var api = new IdentityApiFactory();
        using var http = api.CreateClient();
        await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone });
        await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = "+201112223334" });

        var crossed = await http.PostAsJsonAsync("/api/auth/otp/verify", new { phone = "+201112223334", code = api.Sender.LastCode(Phone) });

        // Only valid if both codes happen to be equal (1 in a million); retry guards against that fluke.
        if (api.Sender.LastCode(Phone) != api.Sender.LastCode("+201112223334"))
            Assert.Equal(HttpStatusCode.BadRequest, crossed.StatusCode);
    }

    [MongoFact]
    public async Task The_code_is_never_stored_in_the_database()
    {
        await using var api = new IdentityApiFactory();
        using var http = api.CreateClient();
        await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone });
        var code = api.Sender.LastCode(Phone);

        var raw = (await api.Database.GetCollection<BsonDocument>(Collections.OtpChallenges).Find(new BsonDocument()).SingleAsync()).ToJson();

        Assert.DoesNotContain($"\"{code}\"", raw);
        Assert.DoesNotContain("1012345678", raw);
    }

    // ---- rate limits ----

    [MongoFact]
    public async Task Asking_for_a_new_code_too_soon_is_limited()
    {
        await using var api = new IdentityApiFactory();
        using var http = api.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone })).StatusCode);

        var again = await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone });
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        Assert.True(again.Headers.Contains("Retry-After"));

        api.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone })).StatusCode);
    }

    [MongoFact]
    public async Task One_number_gets_at_most_five_codes_an_hour()
    {
        await using var api = new IdentityApiFactory();
        using var http = api.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone })).StatusCode);
            api.Time.Advance(TimeSpan.FromSeconds(61));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone })).StatusCode);
        Assert.Equal(5, api.Sender.Sent.Count);

        api.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone })).StatusCode);
    }

    [MongoFact]
    public async Task One_ip_cannot_request_codes_for_many_numbers()
    {
        await using var api = new IdentityApiFactory(new() { ["Identity:OtpPerIpPerHour"] = "3" });
        using var http = api.CreateClient();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = $"+20101000000{i}" })).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = "+201010000009" })).StatusCode);
    }

    // ---- sessions: cookie for the web, bearer for the app ----

    [MongoFact]
    public async Task The_cookie_and_the_bearer_token_both_sign_requests_in_and_anonymous_requests_are_refused()
    {
        await using var api = new IdentityApiFactory();
        var web = await api.SignInAsync(Phone, "web");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var app = await api.SignInAsync(Phone, "app");
        using var http = api.CreateClient();

        Assert.Null(web.Token);
        Assert.NotNull(app.Token);
        Assert.Null(app.Cookie);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, "/api/me", web.Cookie!))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(IdentityApiFactory.WithBearer(HttpMethod.Get, "/api/me", app.Token!))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(IdentityApiFactory.WithBearer(HttpMethod.Get, "/api/me", "made-up-token"))).StatusCode);
    }

    [MongoFact]
    public async Task Only_a_hash_of_the_session_token_is_stored()
    {
        await using var api = new IdentityApiFactory();
        var app = await api.SignInAsync(Phone, "app");

        var raw = (await api.Database.GetCollection<BsonDocument>(Collections.Sessions).Find(new BsonDocument()).SingleAsync()).ToJson();

        Assert.DoesNotContain(app.Token!, raw);
    }

    [MongoFact]
    public async Task Logging_out_ends_the_session()
    {
        await using var api = new IdentityApiFactory();
        var web = await api.SignInAsync(Phone);
        using var http = api.CreateClient();

        var logout = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Post, "/api/auth/logout", web.Cookie!));

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, "/api/me", web.Cookie!))).StatusCode);
    }

    [MongoFact]
    public async Task A_session_expires_after_its_lifetime()
    {
        await using var api = new IdentityApiFactory();
        var web = await api.SignInAsync(Phone, "web");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var app = await api.SignInAsync(Phone, "app");
        using var http = api.CreateClient();

        api.Time.Advance(TimeSpan.FromDays(31));

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, "/api/me", web.Cookie!))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(IdentityApiFactory.WithBearer(HttpMethod.Get, "/api/me", app.Token!))).StatusCode);

        api.Time.Advance(TimeSpan.FromDays(60));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(IdentityApiFactory.WithBearer(HttpMethod.Get, "/api/me", app.Token!))).StatusCode);
    }

    [MongoFact]
    public async Task A_suspended_account_loses_its_sessions_and_cannot_sign_in_again()
    {
        await using var api = new IdentityApiFactory();
        var web = await api.SignInAsync(Phone);
        await api.Database.GetCollection<BsonDocument>(Collections.Users)
            .UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("status", "Suspended"));
        using var http = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, "/api/me", web.Cookie!))).StatusCode);

        api.Time.Advance(TimeSpan.FromMinutes(2));
        await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = Phone });
        var again = await http.PostAsJsonAsync("/api/auth/otp/verify", new { phone = Phone, code = api.Sender.LastCode(Phone) });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    // ---- roles ----

    [MongoFact]
    public async Task A_normal_user_cannot_change_roles_and_an_anonymous_caller_is_not_signed_in()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync(Phone);
        var id = user.Body.GetProperty("user").GetProperty("id").GetString();
        using var http = api.CreateClient();
        var body = new { roles = new[] { "Admin" } };

        var anonymous = await http.PutAsJsonAsync($"/api/admin/users/{id}/roles", body);
        var forbidden = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Put, $"/api/admin/users/{id}/roles", user.Cookie!, body));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var stored = await api.Database.GetCollection<User>(Collections.Users).Find(FilterDefinition<User>.Empty).SingleAsync();
        Assert.Equal([UserRole.User], stored.Roles);
    }

    [MongoFact]
    public async Task The_bootstrap_admin_can_grant_roles_and_a_moderator_still_cannot()
    {
        await using var api = new IdentityApiFactory(new() { ["Identity:BootstrapAdminPhone"] = "+201000000001" });
        var admin = await api.SignInAsync("+201000000001");
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var other = await api.SignInAsync("+201000000002");
        var otherId = other.Body.GetProperty("user").GetProperty("id").GetString();
        using var http = api.CreateClient();
        Assert.Contains("Admin", admin.Body.GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        var grant = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Put, $"/api/admin/users/{otherId}/roles", admin.Cookie!, new { roles = new[] { "Moderator" } }));
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        var roles = (await grant.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal(["User", "Moderator"], roles);

        // The moderator's own session sees the new role at once, but a moderator is still not an admin.
        var tryAdmin = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Put, $"/api/admin/users/{otherId}/roles", other.Cookie!, new { roles = new[] { "Admin" } }));
        Assert.Equal(HttpStatusCode.Forbidden, tryAdmin.StatusCode);
    }

    [MongoFact]
    public async Task Roles_must_be_known_and_every_account_keeps_the_user_role()
    {
        await using var api = new IdentityApiFactory(new() { ["Identity:BootstrapAdminPhone"] = "+201000000001" });
        var admin = await api.SignInAsync("+201000000001");
        var adminId = admin.Body.GetProperty("user").GetProperty("id").GetString();
        using var http = api.CreateClient();

        var bad = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Put, $"/api/admin/users/{adminId}/roles", admin.Cookie!, new { roles = new[] { "Emperor" } }));
        var onlyAdmin = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Put, $"/api/admin/users/{adminId}/roles", admin.Cookie!, new { roles = new[] { "Admin", "BusinessOwner" } }));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var roles = (await onlyAdmin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal(["User", "Admin", "BusinessOwner"], roles);
    }

    // ---- public profile is separate from the account ----

    [MongoFact]
    public async Task The_public_profile_is_optional_and_can_be_changed_without_touching_the_account()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync(Phone);
        using var http = api.CreateClient();
        Assert.Equal(JsonValueKind.Null, user.Body.GetProperty("user").GetProperty("profile").GetProperty("displayName").ValueKind);

        var update = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Put, "/api/me/profile", user.Cookie!,
            new { displayName = "  سارة أحمد  ", avatarUrl = "https://cdn.example.com/a.png" }));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var me = await (await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, "/api/me", user.Cookie!))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("سارة أحمد", me.GetProperty("profile").GetProperty("displayName").GetString());
        Assert.False(me.GetProperty("phoneVerified").GetBoolean());
        Assert.Equal(user.Body.GetProperty("user").GetProperty("id").GetString(), me.GetProperty("id").GetString());
    }

    [MongoFact]
    public async Task Bad_profile_values_are_refused()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync(Phone);
        using var http = api.CreateClient();

        foreach (var body in new object[]
        {
            new { displayName = "x" },
            new { displayName = new string('a', 41) },
            new { displayName = "bad\u0007name" },
            new { displayName = "Sara", avatarUrl = "http://insecure.example.com/a.png" },
            new { displayName = "Sara", avatarUrl = "javascript:alert(1)" },
        })
        {
            var r = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Put, "/api/me/profile", user.Cookie!, body));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
    }

    [MongoFact]
    public async Task Changing_the_profile_needs_a_session()
    {
        await using var api = new IdentityApiFactory();
        using var http = api.CreateClient();

        var r = await http.PutAsJsonAsync("/api/me/profile", new { displayName = "Sara" });

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}
