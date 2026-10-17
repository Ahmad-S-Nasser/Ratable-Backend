using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Ratable.Modules.Identity.Data;
using Ratable.Modules.Identity.Models;

namespace Ratable.Modules.Identity.Services;

public sealed record IssuedSession(string Token, DateTime ExpiresAt);

/// <summary>Opaque server-side sessions. The same token works as the web cookie or as an app bearer token, and can be revoked.</summary>
public sealed class SessionService(
    SessionRepository sessions,
    UserRepository users,
    IOptions<IdentityOptions> options,
    TimeProvider time)
{
    public async Task<IssuedSession> CreateAsync(string userId, SessionKind kind, CancellationToken ct = default)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow().UtcDateTime;
        var days = kind == SessionKind.Web ? options.Value.WebSessionDays : options.Value.AppSessionDays;
        var session = new Session { TokenHash = HashToken(token), UserId = userId, Kind = kind, CreatedAt = now, ExpiresAt = now.AddDays(days) };
        await sessions.InsertAsync(session, ct);
        return new IssuedSession(token, session.ExpiresAt);
    }

    /// <summary>Returns the signed-in user, or null when the token is unknown, expired, revoked or the account is suspended.</summary>
    public async Task<User?> ValidateAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var session = await sessions.GetByTokenHashAsync(HashToken(token), ct);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= time.GetUtcNow().UtcDateTime) return null;
        var user = await users.GetAsync(session.UserId, ct);
        return user is { Status: UserStatus.Active } ? user : null;
    }

    public Task RevokeAsync(string token, CancellationToken ct = default) =>
        sessions.RevokeAsync(HashToken(token), time.GetUtcNow().UtcDateTime, ct);

    public Task RevokeAllAsync(string userId, CancellationToken ct = default) =>
        sessions.RevokeAllForUserAsync(userId, time.GetUtcNow().UtcDateTime, ct);

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
