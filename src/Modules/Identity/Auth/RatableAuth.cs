using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratable.Modules.Identity.Models;
using Ratable.Modules.Identity.Services;
using Ratable.Shared.Auth;

namespace Ratable.Modules.Identity.Auth;

public static class RatableAuth
{
    public const string Scheme = "Ratable";
    public const string CookieName = "ratable_session";

    public static class Policies
    {
        public const string SignedIn = PolicyNames.SignedIn;
        public const string Moderator = PolicyNames.Moderator;
        public const string Admin = PolicyNames.Admin;
        public const string BusinessOwner = PolicyNames.BusinessOwner;
    }

    public static void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(Policies.SignedIn, p => p.RequireAuthenticatedUser());
        // Admins can do everything a moderator can.
        options.AddPolicy(Policies.Moderator, p => p.RequireRole(nameof(UserRole.Moderator), nameof(UserRole.Admin)));
        options.AddPolicy(Policies.Admin, p => p.RequireRole(nameof(UserRole.Admin)));
        options.AddPolicy(Policies.BusinessOwner, p => p.RequireRole(nameof(UserRole.BusinessOwner)));
    }
}

/// <summary>Signs a request in from the httpOnly session cookie (web) or an "Authorization: Bearer" token (app). Roles are read fresh from the database on each request.</summary>
public sealed class RatableAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionService sessions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public static string? ReadToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header["Bearer ".Length..].Trim();
        return request.Cookies.TryGetValue(RatableAuth.CookieName, out var cookie) ? cookie : null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken(Request);
        if (token is null) return AuthenticateResult.NoResult();

        var user = await sessions.ValidateAsync(token, Context.RequestAborted);
        if (user is null) return AuthenticateResult.Fail("Invalid or expired session.");

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id) };
        claims.AddRange(user.Roles.Select(r => new Claim(ClaimTypes.Role, r.ToString())));
        var identity = new ClaimsIdentity(claims, RatableAuth.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), RatableAuth.Scheme));
    }
}
