using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Ratable.Api.Tests.Data;
using Ratable.Modules.Identity.Data;
using Ratable.Modules.Identity.Services;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Identity;

public sealed class StaticOpenIdConfiguration(OpenIdConnectConfiguration configuration) : IConfigurationManager<OpenIdConnectConfiguration>
{
    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(configuration);

    public void RequestRefresh()
    {
    }
}

public sealed class FakeIdTokens : IExternalIdTokenVerifier
{
    public string Provider => "google";

    /// <summary>The tokens this fake accepts, with the subject each stands for.</summary>
    public Dictionary<string, string> Accepted { get; } = [];

    public Task<ExternalIdentity?> VerifyAsync(string idToken, CancellationToken ct = default) =>
        Task.FromResult(Accepted.TryGetValue(idToken, out var subject) ? new ExternalIdentity("google", subject) : null);
}

public sealed class ExternalSignInTests
{
    private const string Issuer = "https://accounts.google.com";
    private const string Audience = "ratable-client-id.apps.googleusercontent.com";

    private static (OidcIdTokenVerifier Verifier, RsaSecurityKey Key, FakeTimeProvider Time) NewVerifier()
    {
        var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "k1" };
        var config = new OpenIdConnectConfiguration();
        config.SigningKeys.Add(new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "k1" });
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        return (new OidcIdTokenVerifier("google", [Issuer, "accounts.google.com"], Audience, new StaticOpenIdConfiguration(config), time), key, time);
    }

    private static string Token(SecurityKey key, DateTimeOffset now, string issuer = Issuer, string audience = Audience, string subject = "1234567890", int minutes = 30, string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("email", "person@example.com")]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.UtcDateTime.AddMinutes(minutes),
            SigningCredentials = new SigningCredentials(key, algorithm),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    [Fact]
    public async Task A_genuine_token_is_accepted_and_only_the_provider_and_the_subject_are_kept()
    {
        var (verifier, key, time) = NewVerifier();
        var identity = await verifier.VerifyAsync(Token(key, time.GetUtcNow()));
        Assert.Equal(new ExternalIdentity("google", "1234567890"), identity);
        Assert.Equal(["Provider", "Subject"], typeof(ExternalIdentity).GetProperties().Select(p => p.Name)); // no email, no name
    }

    [Fact]
    public async Task A_token_for_another_app_another_issuer_or_with_a_wrong_signature_is_refused()
    {
        var (verifier, key, time) = NewVerifier();
        var now = time.GetUtcNow();

        Assert.Null(await verifier.VerifyAsync(Token(key, now, audience: "someone-elses-app"))); // made for another app
        Assert.Null(await verifier.VerifyAsync(Token(key, now, issuer: "https://evil.example"))); // not from Google
        var stranger = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "k1" };
        Assert.Null(await verifier.VerifyAsync(Token(stranger, now))); // signed with a key Google never published
        var tampered = Token(key, now).Split('.');
        tampered[1] = Convert.ToBase64String("{\"sub\":\"someone-else\"}"u8.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Null(await verifier.VerifyAsync(string.Join('.', tampered))); // the payload was changed after signing
        Assert.Null(await verifier.VerifyAsync(""));
        Assert.Null(await verifier.VerifyAsync("not-a-token"));
        Assert.Null(await verifier.VerifyAsync(new string('a', 9000)));
    }

    [Fact]
    public async Task A_token_without_a_signature_or_signed_with_a_shared_secret_is_refused()
    {
        var (verifier, _, time) = NewVerifier();
        var now = time.GetUtcNow();
        var unsigned = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Issuer = Issuer, Audience = Audience, Subject = new ClaimsIdentity([new Claim("sub", "1")]), Expires = now.UtcDateTime.AddMinutes(30) });
        Assert.Null(await verifier.VerifyAsync(unsigned));

        var symmetric = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        Assert.Null(await verifier.VerifyAsync(Token(symmetric, now, algorithm: SecurityAlgorithms.HmacSha256)));
    }

    [Fact]
    public async Task An_expired_token_is_refused_and_the_application_clock_decides()
    {
        var (verifier, key, time) = NewVerifier();
        var token = Token(key, time.GetUtcNow(), minutes: 10);
        Assert.NotNull(await verifier.VerifyAsync(token));
        time.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(await verifier.VerifyAsync(token));
    }

    // ---- the endpoints ----

    [MongoFact]
    public async Task Continue_with_google_creates_the_account_once_signs_in_and_sets_an_httponly_cookie()
    {
        var tokens = new FakeIdTokens();
        tokens.Accepted["good-token"] = "google-subject-1";
        await using var api = new IdentityApiFactory(configure: s => s.AddSingleton<IExternalIdTokenVerifier>(tokens));
        using var http = api.CreateClient();

        var first = await http.PostAsJsonAsyncAndRead(api, new { provider = "google", idToken = "good-token", client = "web" });
        Assert.Equal(HttpStatusCode.OK, first.Response.StatusCode);
        Assert.Contains("httponly", string.Join(';', first.Response.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        var id = first.Json.GetProperty("user").GetProperty("id").GetString();
        Assert.False(first.Json.GetProperty("user").GetProperty("phoneVerified").GetBoolean());

        var again = await http.PostAsJsonAsyncAndRead(api, new { provider = "google", idToken = "good-token", client = "web" });
        Assert.Equal(id, again.Json.GetProperty("user").GetProperty("id").GetString()); // the same account, not a second one
        var users = api.Services.GetRequiredService<UserRepository>();
        Assert.Equal(id, (await users.GetByLoginAsync("google", "google-subject-1"))!.Id);

        // Nothing but the provider and the subject was stored.
        var stored = await api.Database.GetCollection<MongoDB.Bson.BsonDocument>("users").Find(MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty).FirstAsync();
        Assert.DoesNotContain("example.com", stored.ToJson());
    }

    [MongoFact]
    public async Task A_bad_token_an_unknown_provider_and_a_suspended_account_are_refused_and_the_app_gets_a_bearer_token()
    {
        var tokens = new FakeIdTokens();
        tokens.Accepted["good"] = "s1";
        await using var api = new IdentityApiFactory(configure: s => s.AddSingleton<IExternalIdTokenVerifier>(tokens));
        using var http = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsyncAndRead(api, new { provider = "google", idToken = "forged", client = "web" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsyncAndRead(api, new { provider = "apple", idToken = "good", client = "web" })).Response.StatusCode); // Apple is not set up
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsyncAndRead(api, new { provider = "google", idToken = "good", client = "tv" })).Response.StatusCode);

        var app = await http.PostAsJsonAsyncAndRead(api, new { provider = "google", idToken = "good", client = "app" });
        var token = app.Json.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Get, "/api/me")).Response.StatusCode); // no token, no access
        using var me = IdentityApiFactory.WithBearer(HttpMethod.Get, "/api/me", token);
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().SendAsync(me)).StatusCode);

        var users = api.Services.GetRequiredService<UserRepository>();
        var user = (await users.GetByLoginAsync("google", "s1"))!;
        user.Status = Ratable.Modules.Identity.Models.UserStatus.Suspended;
        await users.ReplaceAsync(user);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsyncAndRead(api, new { provider = "google", idToken = "good", client = "web" })).Response.StatusCode);
    }

    [MongoFact]
    public async Task The_providers_list_shows_only_what_is_set_up()
    {
        await using var none = new IdentityApiFactory();
        var off = (await Send(none, HttpMethod.Get, "/api/auth/providers")).Json;
        Assert.True(off.GetProperty("phone").GetBoolean());
        Assert.Equal(JsonValueKind.Null, off.GetProperty("googleClientId").ValueKind);
        Assert.Equal(JsonValueKind.Null, off.GetProperty("appleClientId").ValueKind);

        await using var on = new IdentityApiFactory(new Dictionary<string, string> { ["Identity:GoogleClientId"] = "g-id", ["Identity:AppleClientId"] = "a-id" });
        var set = (await Send(on, HttpMethod.Get, "/api/auth/providers")).Json;
        Assert.Equal("g-id", set.GetProperty("googleClientId").GetString());
        Assert.Equal("a-id", set.GetProperty("appleClientId").GetString());
        // With a client id set the real verifier is registered.
        Assert.Equal(["apple", "google"], on.Services.GetServices<IExternalIdTokenVerifier>().Select(v => v.Provider).Order());
        Assert.Empty(none.Services.GetServices<IExternalIdTokenVerifier>());
    }
}

internal static class HttpExtensions
{
    public static async Task<(HttpResponseMessage Response, JsonElement Json)> PostAsJsonAsyncAndRead(this HttpClient http, IdentityApiFactory api, object body)
    {
        var response = await http.PostAsJsonAsync("/api/auth/external", body);
        var text = await response.Content.ReadAsStringAsync();
        return (response, text.StartsWith('{') ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }
}
