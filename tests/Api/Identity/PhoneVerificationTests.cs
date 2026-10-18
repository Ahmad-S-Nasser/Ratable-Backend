using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Modules.Identity.PhoneVerification;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Identity;

public sealed class FakeNumberTypes : INumberTypeChecker
{
    public Dictionary<string, NumberType> Types { get; } = [];

    public Task<NumberType> CheckAsync(string phoneE164, CancellationToken ct = default) => Task.FromResult(Types.GetValueOrDefault(phoneE164, NumberType.Mobile));
}

public sealed class CapturingCodeSender : IVerificationCodeSender
{
    public List<(string Phone, string Code, string Channel)> Sent { get; } = [];
    public bool Fail { get; set; }

    public Task SendAsync(string phoneE164, string code, string channel, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("provider down");
        Sent.Add((phoneE164, code, channel));
        return Task.CompletedTask;
    }
}

public sealed class PhoneVerificationTests
{
    private const string Secret = "whatsapp-app-secret";
    private const string VerifyToken = "setup-token";

    private sealed record Rig(IdentityApiFactory Api, FakeNumberTypes Types, CapturingCodeSender Codes) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }

    private static Rig NewRig(Dictionary<string, string>? extra = null)
    {
        var types = new FakeNumberTypes();
        var codes = new CapturingCodeSender();
        var settings = new Dictionary<string, string>
        {
            ["Identity:WhatsAppNumber"] = "+201099999999",
            ["Identity:WhatsAppAppSecret"] = Secret,
            ["Identity:WhatsAppVerifyToken"] = VerifyToken,
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        var api = new IdentityApiFactory(settings, configure: s =>
        {
            s.AddSingleton<INumberTypeChecker>(types);
            s.AddSingleton<IVerificationCodeSender>(codes);
        });
        return new Rig(api, types, codes);
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Json)> SendWith(
        IdentityApiFactory api, HttpMethod method, string url, string cookie, object? body = null, string? device = null)
    {
        using var http = api.CreateClient();
        using var request = IdentityApiFactory.WithCookie(method, url, cookie, body);
        if (device is not null) request.Headers.Add("X-Device-Id", device);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, text.StartsWith('{') || text.StartsWith('[') ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    private static string Signature(byte[] body) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body)).ToLowerInvariant();

    /// <summary>What WhatsApp sends when someone messages Ratable's number.</summary>
    private static async Task<HttpResponseMessage> Webhook(IdentityApiFactory api, string fromDigits, string text, bool sign = true, string? signature = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { entry = new[] { new { changes = new[] { new { value = new { messages = new[] { new { from = fromDigits, text = new { body = text } } } } } } } } });
        using var http = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/whatsapp") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        if (sign || signature is not null) request.Headers.Add("X-Hub-Signature-256", signature ?? Signature(body));
        return await http.SendAsync(request);
    }

    private static string DigitsOf(IdentityApiFactory.SignedIn s) => s.Phone.TrimStart('+');

    private static async Task<JsonElement> Start(IdentityApiFactory api, IdentityApiFactory.SignedIn user, object? body = null, string? device = null) =>
        (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, body ?? new { }, device)).Json;

    private static async Task<bool> IsVerified(IdentityApiFactory api, IdentityApiFactory.SignedIn user) =>
        (await SendWith(api, HttpMethod.Get, "/api/me", user.Cookie!)).Json.GetProperty("phoneVerified").GetBoolean();

    private static async Task<string> Verify(IdentityApiFactory api, IdentityApiFactory.SignedIn user, string? device = null)
    {
        var started = await Start(api, user, device: device);
        var code = started.GetProperty("code").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Webhook(api, DigitsOf(user), $"Ratable {code}")).StatusCode);
        return code;
    }

    // ---- the reverse route (the default) ----

    [MongoFact]
    public async Task The_reverse_route_shows_a_code_opens_whatsapp_and_the_webhook_verifies_the_phone_once()
    {
        await using var rig = NewRig();
        var api = rig.Api;
        var user = await api.SignInAsync("+201111111111");
        Assert.False(await IsVerified(api, user)); // signing in with the number does not verify it

        var started = (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { })).Json;
        var code = started.GetProperty("code").GetString()!;
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.Equal("reverse", started.GetProperty("route").GetString());
        var url = started.GetProperty("whatsAppUrl").GetString()!;
        Assert.StartsWith("https://wa.me/201099999999?text=", url);
        Assert.Contains(Uri.EscapeDataString(code), url); // the code is already typed in the message
        Assert.DoesNotContain("1111111", started.ToString()); // the person's number is not echoed back

        Assert.Equal("pending", (await SendWith(api, HttpMethod.Get, "/api/me/phone/verify/status", user.Cookie!)).Json.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Webhook(api, DigitsOf(user), $"Ratable {code}")).StatusCode);
        Assert.True(await IsVerified(api, user));
        Assert.Equal("verified", (await SendWith(api, HttpMethod.Get, "/api/me/phone/verify/status", user.Cookie!)).Json.GetProperty("state").GetString());

        // The code is stored as a hash and the number is cleared once the check is done.
        var stored = await api.Database.GetCollection<BsonDocument>("phone_verifications").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        var record = Assert.Single(stored);
        Assert.DoesNotContain(code.Replace("-", ""), record.ToJson());
        Assert.Equal("", record["phoneEncrypted"].AsString);
        Assert.True(record.Contains("consumedAt"));
    }

    [MongoFact]
    public async Task A_code_works_once_even_when_the_message_arrives_twice_at_the_same_time()
    {
        await using var rig = NewRig();
        var api = rig.Api;
        var user = await api.SignInAsync("+201111111111");
        var code = (await Start(api, user)).GetProperty("code").GetString()!;

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => api.Services.GetRequiredService<PhoneVerificationService>().HandleIncomingMessageAsync($"+{DigitsOf(user)}", $"Ratable {code}")));

        Assert.Equal(1, results.Count(r => r.Ok)); // exactly one call used the code up
        Assert.True(await IsVerified(api, user));
        // Sending it again later changes nothing and is not an error for WhatsApp.
        Assert.Equal(HttpStatusCode.OK, (await Webhook(api, DigitsOf(user), $"Ratable {code}")).StatusCode);
    }

    [MongoFact]
    public async Task A_message_from_another_number_does_not_verify_and_the_right_sender_still_can()
    {
        await using var rig = NewRig();
        var api = rig.Api;
        var user = await api.SignInAsync("+201111111111");
        var code = (await Start(api, user)).GetProperty("code").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await Webhook(api, "201999999999", $"Ratable {code}")).StatusCode);
        Assert.False(await IsVerified(api, user));
        var attempts = (await api.Database.GetCollection<BsonDocument>("phone_verifications").Find(FilterDefinition<BsonDocument>.Empty).FirstAsync())["attempts"].AsInt32;
        Assert.Equal(1, attempts); // counted against the code

        await Webhook(api, DigitsOf(user), $"Ratable {code}");
        Assert.True(await IsVerified(api, user));
    }

    [MongoFact]
    public async Task A_stranger_who_guesses_codes_burns_the_code_and_cannot_verify_anyone()
    {
        await using var rig = NewRig();
        var api = rig.Api;
        var user = await api.SignInAsync("+201111111111");
        var code = (await Start(api, user)).GetProperty("code").GetString()!;
        for (var i = 0; i < 6; i++) await Webhook(api, "201999999999", $"Ratable {code}"); // five wrong-sender tries use the code up

        await Webhook(api, DigitsOf(user), $"Ratable {code}"); // even the right sender is too late now

        Assert.False(await IsVerified(api, user));
    }

    [MongoFact]
    public async Task A_code_expires_after_ten_minutes()
    {
        await using var rig = NewRig();
        var api = rig.Api;
        var user = await api.SignInAsync("+201111111111");
        var code = (await Start(api, user)).GetProperty("code").GetString()!;

        api.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("pending", (await SendWith(api, HttpMethod.Get, "/api/me/phone/verify/status", user.Cookie!)).Json.GetProperty("state").GetString());
        api.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("expired", (await SendWith(api, HttpMethod.Get, "/api/me/phone/verify/status", user.Cookie!)).Json.GetProperty("state").GetString());

        await Webhook(api, DigitsOf(user), $"Ratable {code}");
        Assert.False(await IsVerified(api, user));
    }

    // ---- the alternative route ----

    [MongoFact]
    public async Task The_code_route_sends_the_code_to_the_phone_and_the_person_types_it()
    {
        await using var rig = NewRig();
        var api = rig.Api;
        var user = await api.SignInAsync("+201111111111");

        var started = await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { route = "code", channel = "sms" });
        Assert.Equal(HttpStatusCode.OK, started.Response.StatusCode);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, started.Json.GetProperty("code").ValueKind); // the code goes to the phone, not to the page
        var (phone, code, channel) = Assert.Single(rig.Codes.Sent);
        Assert.Equal("+201111111111", phone);
        Assert.Equal("sms", channel);

        Assert.Equal("invalid_code", (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/confirm", user.Cookie!, new { code = "AAAA-AAAA" })).Json.GetProperty("error").GetString());
        Assert.False(await IsVerified(api, user));
        Assert.Equal(HttpStatusCode.OK, (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/confirm", user.Cookie!, new { code = code.ToLowerInvariant().Replace("-", "") })).Response.StatusCode); // spelling and case are forgiven
        Assert.True(await IsVerified(api, user));
        Assert.Equal("not_started", (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/confirm", user.Cookie!, new { code })).Json.GetProperty("error").GetString()); // used once
    }

    [MongoFact]
    public async Task Retries_are_limited_even_with_the_right_code_afterwards()
    {
        await using var rig = NewRig();
        var api = rig.Api;
        var user = await api.SignInAsync("+201111111111");
        await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { route = "code" });
        var code = rig.Codes.Sent.Single().Code;

        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.BadRequest, (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/confirm", user.Cookie!, new { code = "ZZZZ-ZZZZ" })).Response.StatusCode);
        var locked = await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/confirm", user.Cookie!, new { code });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.Response.StatusCode);
        Assert.Equal("too_many_attempts", locked.Json.GetProperty("error").GetString());
        Assert.False(await IsVerified(api, user));
    }

    [MongoFact]
    public async Task Without_a_code_provider_the_code_route_says_so()
    {
        var api = new IdentityApiFactory(new Dictionary<string, string> { ["Identity:WhatsAppNumber"] = "+201099999999" }); // no fake sender: the unconfigured default
        await using var _ = api;
        var user = await api.SignInAsync("+201111111111");
        var result = await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { route = "code" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Response.StatusCode);
        Assert.Equal("provider_unavailable", result.Json.GetProperty("error").GetString());
        // The default route is not affected.
        Assert.Equal(HttpStatusCode.OK, (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { })).Response.StatusCode);
    }

    // ---- limits and blocks ----

    [MongoFact]
    public async Task A_virtual_or_temporary_number_is_blocked()
    {
        await using var rig = NewRig(new Dictionary<string, string> { ["Identity:BlockedPhonePrefixes:0"] = "+201555" });
        var api = rig.Api;
        var user = await api.SignInAsync("+201111111111");
        rig.Types.Types["+201222222222"] = NumberType.Virtual;
        rig.Types.Types["+201333333333"] = NumberType.Temporary;

        foreach (var number in new[] { "+201222222222", "+201333333333" })
        {
            var result = await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { phone = number });
            Assert.Equal(HttpStatusCode.BadRequest, result.Response.StatusCode);
            Assert.Equal("number_not_allowed", result.Json.GetProperty("error").GetString());
        }
        // A mobile number is fine; landlines and unknowns are not blocked either.
        Assert.Equal(HttpStatusCode.OK, (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { phone = "+201444444444" })).Response.StatusCode);
    }

    [MongoFact]
    public async Task The_default_checker_blocks_the_configured_prefixes()
    {
        var api = new IdentityApiFactory(new Dictionary<string, string> { ["Identity:WhatsAppNumber"] = "+201099999999", ["Identity:BlockedPhonePrefixes:0"] = "+201555" });
        await using var _ = api;
        var user = await api.SignInAsync("+201111111111");
        var blocked = await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { phone = "+201555123456" });
        Assert.Equal("number_not_allowed", blocked.Json.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task Starting_is_limited_per_number_per_device_and_per_network()
    {
        await using var rig = NewRig(new Dictionary<string, string> { ["Identity:VerifyStartsPerPhonePerHour"] = "3", ["Identity:VerifyStartsPerDevicePerDay"] = "5", ["Identity:VerifyStartsPerIpPerHour"] = "6" });
        var api = rig.Api;
        var user = await api.SignInAsync("+201111111111");

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { })).Response.StatusCode);
        var limited = await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Response.StatusCode);
        Assert.Equal("rate_limited", limited.Json.GetProperty("error").GetString());

        // After an hour the number is allowed again.
        api.Time.Advance(TimeSpan.FromHours(1.1));
        Assert.Equal(HttpStatusCode.OK, (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { })).Response.StatusCode);

        // One device asking for many different numbers hits the device limit.
        var other = await api.SignInAsync("+201888888888");
        var codes = 0;
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 8 && last == HttpStatusCode.OK; i++)
        {
            last = (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", other.Cookie!, new { phone = $"+2017000000{i:D2}" }, device: "same-phone")).Response.StatusCode;
            if (last == HttpStatusCode.OK) codes++;
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.True(codes <= 5);
    }

    [MongoFact]
    public async Task A_number_that_belongs_to_another_account_cannot_be_verified_on_this_one()
    {
        await using var rig = NewRig();
        var api = rig.Api;
        var owner = await api.SignInAsync("+201111111111");
        var other = await api.SignInAsync("+201222222222");

        var result = await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", other.Cookie!, new { phone = "+201111111111" });

        Assert.Equal(HttpStatusCode.Conflict, result.Response.StatusCode);
        Assert.Equal("phone_in_use", result.Json.GetProperty("error").GetString());
        Assert.NotNull(owner);
    }

    [MongoFact]
    public async Task Only_a_few_verified_accounts_may_share_a_device()
    {
        await using var rig = NewRig(new Dictionary<string, string> { ["Identity:VerifiedAccountsPerDevice"] = "3", ["Identity:VerifyStartsPerIpPerHour"] = "50" });
        var api = rig.Api;
        var users = new List<IdentityApiFactory.SignedIn>();
        for (var i = 0; i < 4; i++) users.Add(await api.SignInAsync($"+2011000000{i:D2}", device: "shared-tablet"));

        for (var i = 0; i < 3; i++) await Verify(api, users[i], "shared-tablet");
        var fourth = await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", users[3].Cookie!, new { }, "shared-tablet");

        Assert.Equal(HttpStatusCode.Conflict, fourth.Response.StatusCode);
        Assert.Equal("too_many_accounts_on_device", fourth.Json.GetProperty("error").GetString());
        // From a different device the same person can verify.
        Assert.Equal(HttpStatusCode.OK, (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", users[3].Cookie!, new { }, "their-own-phone")).Response.StatusCode);
    }

    [MongoFact]
    public async Task An_account_that_signed_in_with_google_can_verify_a_number_and_it_becomes_theirs()
    {
        await using var rig = NewRig();
        var api = rig.Api;
        // A social account has no phone: it types one.
        var external = api.Services.GetRequiredService<Ratable.Modules.Identity.Data.UserRepository>();
        var user = new Ratable.Modules.Identity.Models.User { Logins = [new() { Provider = "google", Subject = "g-1" }] };
        await external.InsertAsync(user);
        var session = await api.Services.GetRequiredService<Ratable.Modules.Identity.Services.SessionService>().CreateAsync(user.Id, Ratable.Modules.Identity.Models.SessionKind.Web);
        var cookie = $"ratable_session={session.Token}";

        Assert.Equal("invalid_phone", (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", cookie, new { })).Json.GetProperty("error").GetString());
        var started = await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", cookie, new { phone = "+201777777777" });
        await Webhook(api, "201777777777", $"Ratable {started.Json.GetProperty("code").GetString()}");

        Assert.True((await SendWith(api, HttpMethod.Get, "/api/me", cookie)).Json.GetProperty("phoneVerified").GetBoolean());
        Assert.NotNull((await external.GetAsync(user.Id))!.PhoneHash);
    }

    // ---- the webhook itself ----

    [MongoFact]
    public async Task The_webhook_accepts_only_signed_calls_and_answers_the_setup_check()
    {
        await using var rig = NewRig();
        var api = rig.Api;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Webhook(api, "201111111111", "hi", sign: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Webhook(api, "201111111111", "hi", signature: "sha256=" + new string('0', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Webhook(api, "201111111111", "hi")).StatusCode);

        using var http = api.CreateClient();
        var ok = await http.GetAsync($"/api/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=12345");
        Assert.Equal("12345", await ok.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1")).StatusCode);
    }

    [MongoFact]
    public async Task Without_an_app_secret_the_webhook_refuses_everything()
    {
        var api = new IdentityApiFactory(new Dictionary<string, string> { ["Identity:WhatsAppNumber"] = "+201099999999" });
        await using var _ = api;
        using var http = api.CreateClient();
        var response = await http.PostAsync("/api/webhooks/whatsapp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=&hub.challenge=1")).StatusCode);
    }

    [MongoFact]
    public async Task Starting_needs_a_signed_in_account_and_the_number_is_never_shown()
    {
        await using var rig = NewRig();
        var api = rig.Api;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Post, "/api/me/phone/verify/start", null, new { })).Response.StatusCode);
        var user = await api.SignInAsync("+201111111111");
        await Verify(api, user);
        var me = (await SendWith(api, HttpMethod.Get, "/api/me", user.Cookie!)).Json.ToString();
        Assert.DoesNotContain("1111111", me);
        Assert.Equal(HttpStatusCode.Conflict, (await SendWith(api, HttpMethod.Post, "/api/me/phone/verify/start", user.Cookie!, new { })).Response.StatusCode); // already verified
        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains("1111111"));
    }
}
