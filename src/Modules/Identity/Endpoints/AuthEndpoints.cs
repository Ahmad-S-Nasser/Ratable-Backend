using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Ratable.Modules.Identity.Auth;
using Ratable.Modules.Identity.Data;
using Ratable.Modules.Identity.Models;
using Ratable.Modules.Identity.Services;

namespace Ratable.Modules.Identity.Endpoints;

public sealed record OtpRequestBody(string? Phone);
public sealed record OtpVerifyBody(string? Phone, string? Code, string? Client);
public sealed record ProfileBody(string? DisplayName, string? AvatarUrl, List<string>? Interests = null, string? AreaId = null);
public sealed record RolesBody(List<string>? Roles);
public sealed record ErrorBody(string Error, int? RetryAfterSeconds = null);

/// <summary>What the signed-in user sees about themselves. The phone number is never part of it.</summary>
public sealed record MeResponse(string Id, List<string> Roles, ProfileBody Profile, bool PhoneVerified);
public sealed record OtpRequestedResponse(DateTime ExpiresAt);
public sealed record SignedInWebResponse(MeResponse User);
public sealed record SignedInAppResponse(string Token, DateTime ExpiresAt, MeResponse User);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");
        auth.MapPost("/otp/request", RequestOtp).Produces<OtpRequestedResponse>(StatusCodes.Status202Accepted);
        auth.MapPost("/otp/verify", VerifyOtp).Produces<SignedInWebResponse>();
        auth.MapPost("/logout", Logout).Produces(StatusCodes.Status204NoContent);

        var me = app.MapGroup("/api/me").WithTags("Me").RequireAuthorization(RatableAuth.Policies.SignedIn);
        me.MapGet("", GetMe).Produces<MeResponse>();
        me.MapPut("/profile", UpdateProfile).Produces<MeResponse>();

        app.MapPut("/api/admin/users/{id}/roles", SetRoles)
            .WithTags("Admin").RequireAuthorization(RatableAuth.Policies.Admin).Produces<MeResponse>();
        return app;
    }

    private static async Task<IResult> RequestOtp(OtpRequestBody body, HttpContext http, OtpService otp)
    {
        var result = await otp.RequestAsync(body.Phone, http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
        return result.Failure switch
        {
            OtpFailure.None => Results.Accepted(value: new OtpRequestedResponse(result.ExpiresAt!.Value)),
            OtpFailure.InvalidPhone => Results.BadRequest(new ErrorBody("invalid_phone")),
            OtpFailure.BlockedNumber => Results.BadRequest(new ErrorBody("number_not_allowed")),
            _ => Retry(http, result.RetryAfterSeconds),
        };
    }

    private static IResult Retry(HttpContext http, int? seconds)
    {
        if (seconds is not null) http.Response.Headers.RetryAfter = seconds.ToString();
        return Results.Json(new ErrorBody("rate_limited", seconds), statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static async Task<IResult> VerifyOtp(OtpVerifyBody body, HttpContext http, OtpService otp, SessionService sessions, IHostEnvironment env)
    {
        var client = (body.Client ?? "web").ToLowerInvariant();
        if (client is not ("web" or "app")) return Results.BadRequest(new ErrorBody("invalid_client"));

        var result = await otp.VerifyAsync(body.Phone, body.Code, http.RequestAborted);
        switch (result.Failure)
        {
            case OtpFailure.None: break;
            case OtpFailure.CodeExpired: return Results.BadRequest(new ErrorBody("code_expired"));
            case OtpFailure.TooManyAttempts: return Results.Json(new ErrorBody("too_many_attempts"), statusCode: StatusCodes.Status429TooManyRequests);
            case OtpFailure.AccountSuspended: return Results.Json(new ErrorBody("account_suspended"), statusCode: StatusCodes.Status403Forbidden);
            default: return Results.BadRequest(new ErrorBody("invalid_code"));
        }

        var user = result.User!;
        var issued = await sessions.CreateAsync(user.Id, client == "web" ? SessionKind.Web : SessionKind.App, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers["X-Device-Id"].ToString(), http.RequestAborted);
        var me = ToMe(user);
        if (client == "app") return Results.Ok(new SignedInAppResponse(issued.Token, issued.ExpiresAt, me));

        // The browser never sees the token in JavaScript: it is an httpOnly cookie on the one public origin.
        http.Response.Cookies.Append(RatableAuth.CookieName, issued.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = !env.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Expires = issued.ExpiresAt,
            Path = "/",
        });
        return Results.Ok(new SignedInWebResponse(me));
    }

    private static async Task<IResult> Logout(HttpContext http, SessionService sessions)
    {
        var token = RatableAuthHandler.ReadToken(http.Request);
        if (token is not null) await sessions.RevokeAsync(token, http.RequestAborted);
        http.Response.Cookies.Delete(RatableAuth.CookieName, new CookieOptions { Path = "/" });
        return Results.NoContent();
    }

    private static async Task<IResult> GetMe(ClaimsPrincipal principal, UserRepository users, CancellationToken ct)
    {
        var user = await users.GetAsync(UserId(principal), ct);
        return user is null ? Results.Unauthorized() : Results.Ok(ToMe(user));
    }

    private static async Task<IResult> UpdateProfile(ProfileBody body, ClaimsPrincipal principal, UserRepository users, CancellationToken ct)
    {
        var name = body.DisplayName?.Trim();
        if (name is not null && (name.Length is < 2 or > 40 || name.Any(char.IsControl)))
            return Results.BadRequest(new ErrorBody("invalid_display_name"));
        var avatar = body.AvatarUrl?.Trim();
        if (!string.IsNullOrEmpty(avatar) && (avatar.Length > 500 || !Uri.TryCreate(avatar, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            return Results.BadRequest(new ErrorBody("invalid_avatar_url"));

        // The onboarding choices: up to 12 interests (category ids) and one area id. They are labels chosen by the person, never a location.
        var interests = body.Interests?.Select(i => i.Trim().ToLowerInvariant()).Where(i => i.Length > 0).Distinct().ToList();
        if (interests is not null && (interests.Count > 12 || interests.Any(i => i.Length > 40 || !i.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))))
            return Results.BadRequest(new ErrorBody("invalid_interests"));
        var area = body.AreaId?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(area) && (area.Length > 40 || !area.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return Results.BadRequest(new ErrorBody("invalid_area"));

        var user = await users.GetAsync(UserId(principal), ct);
        if (user is null) return Results.Unauthorized();
        user.Profile.DisplayName = string.IsNullOrEmpty(name) ? null : name;
        user.Profile.AvatarUrl = string.IsNullOrEmpty(avatar) ? null : avatar;
        if (interests is not null) user.Profile.Interests = interests;
        if (body.AreaId is not null) user.Profile.AreaId = string.IsNullOrEmpty(area) ? null : area;
        await users.ReplaceAsync(user, ct);
        return Results.Ok(ToMe(user));
    }

    private static async Task<IResult> SetRoles(string id, RolesBody body, UserRepository users, CancellationToken ct)
    {
        if (body.Roles is null || body.Roles.Count == 0 || !body.Roles.All(r => Enum.TryParse<UserRole>(r, out _)))
            return Results.BadRequest(new ErrorBody("invalid_roles"));
        var user = await users.GetAsync(id, ct);
        if (user is null) return Results.NotFound();

        user.Roles = [UserRole.User, .. body.Roles.Select(Enum.Parse<UserRole>).Where(r => r != UserRole.User).Distinct()];
        await users.ReplaceAsync(user, ct);
        return Results.Ok(ToMe(user));
    }

    private static string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static MeResponse ToMe(User u) =>
        new(u.Id, [.. u.Roles.Select(r => r.ToString())], new ProfileBody(u.Profile.DisplayName, u.Profile.AvatarUrl, u.Profile.Interests, u.Profile.AreaId), u.PhoneVerified);
}
