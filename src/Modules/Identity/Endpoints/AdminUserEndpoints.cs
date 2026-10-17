using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ratable.Modules.Identity.Data;
using Ratable.Modules.Identity.Models;
using Ratable.Modules.Identity.Services;
using Ratable.Shared.Audit;
using Ratable.Shared.Auth;

namespace Ratable.Modules.Identity.Endpoints;

public sealed record SuspendBody(string? Reason);

/// <summary>What an admin sees of an account in the list. Never a phone number, a login or an email.</summary>
public sealed record AdminUser(string Id, List<string> Roles, string? DisplayName, string Status, bool PhoneVerified, DateTime CreatedAt);
public sealed record AdminUserPage(List<AdminUser> Items, long Total, int Page, int PageSize);

public static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/users").WithTags("Admin").RequireAuthorization(PolicyNames.Admin);
        admin.MapGet("", List).Produces<AdminUserPage>();
        admin.MapPost("/{id}/suspend", Suspend).Produces(StatusCodes.Status204NoContent);
        admin.MapPost("/{id}/unsuspend", Unsuspend).Produces(StatusCodes.Status204NoContent);
        return app;
    }

    private static async Task<IResult> List(string? role, string? status, int? page, int? pageSize, UserRepository users, CancellationToken ct)
    {
        UserRole? r = null;
        UserStatus? s = null;
        if (!string.IsNullOrWhiteSpace(role)) { if (!Enum.TryParse<UserRole>(role, true, out var pr)) return Results.BadRequest(new { error = "invalid_role" }); r = pr; }
        if (!string.IsNullOrWhiteSpace(status)) { if (!Enum.TryParse<UserStatus>(status, true, out var ps)) return Results.BadRequest(new { error = "invalid_status" }); s = ps; }
        var size = Math.Clamp(pageSize ?? 50, 1, 200);
        var p = Math.Max(1, page ?? 1);
        var (items, total) = await users.SearchAsync(r, s, (p - 1) * size, size, ct);
        return Results.Ok(new AdminUserPage([.. items.Select(u => new AdminUser(u.Id, [.. u.Roles.Select(x => x.ToString())], u.Profile.DisplayName, u.Status.ToString(), u.PhoneVerified, u.CreatedAt))], total, p, size));
    }

    private static async Task<IResult> Suspend(string id, SuspendBody body, ClaimsPrincipal admin, UserRepository users, SessionService sessions, IAuditLog audit, CancellationToken ct)
    {
        var adminId = admin.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var why = body.Reason?.Trim();
        if (string.IsNullOrEmpty(why) || why.Length > 500) return Results.BadRequest(new { error = "reason_required" });
        if (id == adminId) return Results.BadRequest(new { error = "cannot_suspend_yourself" });
        var user = (await users.ListByIdsAsync([id], ct)).FirstOrDefault();
        if (user is null) return Results.NotFound();
        if (user.Roles.Contains(UserRole.Admin)) return Results.BadRequest(new { error = "cannot_suspend_an_admin" });

        user.Status = UserStatus.Suspended;
        await users.ReplaceAsync(user, ct);
        await sessions.RevokeAllAsync(user.Id, ct); // signed out everywhere at once
        await audit.AppendAsync(new AuditRecord(adminId, "user.suspended", "user", user.Id, why), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Unsuspend(string id, SuspendBody body, ClaimsPrincipal admin, UserRepository users, IAuditLog audit, CancellationToken ct)
    {
        var why = body.Reason?.Trim();
        if (string.IsNullOrEmpty(why) || why.Length > 500) return Results.BadRequest(new { error = "reason_required" });
        var user = (await users.ListByIdsAsync([id], ct)).FirstOrDefault();
        if (user is null) return Results.NotFound();
        user.Status = UserStatus.Active;
        await users.ReplaceAsync(user, ct);
        await audit.AppendAsync(new AuditRecord(admin.FindFirstValue(ClaimTypes.NameIdentifier)!, "user.unsuspended", "user", user.Id, why), ct);
        return Results.NoContent();
    }
}
