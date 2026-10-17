using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Serilog.Core;
using Serilog.Events;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Modules.Ai.Providers;
using Ratable.Modules.Identity.Services;
using Ratable.Shared.Ai;

namespace Ratable.Api.Tests.Identity;

/// <summary>Remembers every code that would have been sent, so tests can sign in without a real provider.</summary>
public sealed class CapturingOtpSender : IOtpSender
{
    private readonly List<(string Phone, string Code)> _sent = [];
    public IReadOnlyList<(string Phone, string Code)> Sent { get { lock (_sent) return [.. _sent]; } }

    public Task SendAsync(string phoneE164, string code, CancellationToken cancellationToken = default)
    {
        lock (_sent) _sent.Add((phoneE164, code));
        return Task.CompletedTask;
    }

    public string LastCode(string phoneE164) => Sent.Last(s => s.Phone == phoneE164).Code;
}

/// <summary>Keeps every log line the API writes (message and properties), so a test can check what was logged.</summary>
public sealed class CapturingLogSink : ILogEventSink
{
    private readonly List<string> _lines = [];
    public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }

    public void Emit(LogEvent logEvent)
    {
        var line = logEvent.RenderMessage() + " " + string.Join(' ', logEvent.Properties.Select(p => $"{p.Key}={p.Value}")) + (logEvent.Exception?.ToString() ?? "");
        lock (_lines) _lines.Add(line);
    }
}

/// <summary>The API on a throwaway database, with a fake clock and a capturing OTP sender.</summary>
public sealed class IdentityApiFactory : WebApplicationFactory<Program>
{
    public CapturingOtpSender Sender { get; } = new();
    public CapturingLogSink Logs { get; } = new();
    private readonly Action<IServiceCollection>? _configure;

    /// <summary>Replaces IAiClient when the factory is created with fakeAi: true. No test calls a real model.</summary>
    public FakeAiClient Ai { get; } = new();
    private readonly bool _fakeAi;
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public string DatabaseName { get; } = $"ratable_tests_{Guid.NewGuid():N}";
    private readonly Dictionary<string, string> _settings;

    public IdentityApiFactory(Dictionary<string, string>? settings = null, bool fakeAi = false, Action<IServiceCollection>? configure = null)
    {
        _configure = configure;
        _settings = settings ?? [];
        _fakeAi = fakeAi;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Mongo:ConnectionString", MongoFixture.ConnectionString ?? "mongodb://127.0.0.1:1");
        builder.UseSetting("Mongo:Database", DatabaseName);
        builder.UseSetting("Identity:PhoneEncryptionKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.UseSetting("Identity:PhoneHashKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IOtpSender>(Sender);
            services.AddSingleton<TimeProvider>(Time);
            if (_fakeAi) services.AddSingleton<IAiClient>(Ai);
            services.AddSingleton<ILogEventSink>(Logs);
            _configure?.Invoke(services);
        });
    }

    public IMongoDatabase Database => new MongoClient(MongoFixture.ConnectionString).GetDatabase(DatabaseName);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && MongoFixture.ConnectionString is not null)
            new MongoClient(MongoFixture.ConnectionString).DropDatabase(DatabaseName);
    }

    // ---- helpers ----

    public sealed record SignedIn(HttpResponseMessage Response, JsonElement Body, string? Cookie, string? Token, string Phone);

    /// <summary>Requests a code and verifies it. client is "web" (cookie) or "app" (bearer token).</summary>
    public async Task<SignedIn> SignInAsync(string phoneInput, string client = "web")
    {
        using var http = CreateClient();
        var request = await http.PostAsJsonAsync("/api/auth/otp/request", new { phone = phoneInput });
        request.EnsureSuccessStatusCode();
        var phone = PhoneNumber.Normalize(phoneInput)!;
        var verify = await http.PostAsJsonAsync("/api/auth/otp/verify", new { phone = phoneInput, code = Sender.LastCode(phone), client });
        var body = await verify.Content.ReadFromJsonAsync<JsonElement>();
        string? cookie = null;
        if (verify.Headers.TryGetValues("Set-Cookie", out var cookies))
            cookie = cookies.FirstOrDefault(c => c.StartsWith("ratable_session=", StringComparison.Ordinal));
        var token = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("token", out var t) ? t.GetString() : null;
        return new SignedIn(verify, body, cookie, token, phone);
    }

    public static HttpRequestMessage WithCookie(HttpMethod method, string url, string setCookie, object? json = null)
    {
        var message = new HttpRequestMessage(method, url);
        message.Headers.Add("Cookie", setCookie.Split(';')[0]);
        if (json is not null) message.Content = JsonContent.Create(json);
        return message;
    }

    public static HttpRequestMessage WithBearer(HttpMethod method, string url, string token, object? json = null)
    {
        var message = new HttpRequestMessage(method, url);
        message.Headers.Add("Authorization", $"Bearer {token}");
        if (json is not null) message.Content = JsonContent.Create(json);
        return message;
    }
}
