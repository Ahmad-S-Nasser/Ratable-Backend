using Ratable.Modules.Identity.Data;
using Ratable.Modules.Identity.Models;
using Ratable.Shared.Businesses;

namespace Ratable.Modules.Identity.Services;

/// <summary>Gives or takes back the business-owner role when a place claim is approved or an ownership is revoked. Roles are read fresh on every request, so it applies at once.</summary>
public sealed class BusinessRoleGranter(UserRepository users) : IBusinessRoleGranter
{
    public async Task SetBusinessOwnerAsync(string userId, bool isOwner, CancellationToken ct = default)
    {
        var user = await users.GetAsync(userId, ct);
        if (user is null || user.Roles.Contains(UserRole.BusinessOwner) == isOwner) return;
        if (isOwner) user.Roles.Add(UserRole.BusinessOwner);
        else user.Roles.Remove(UserRole.BusinessOwner);
        await users.ReplaceAsync(user, ct);
    }
}
