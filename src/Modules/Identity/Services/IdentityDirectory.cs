using Ratable.Modules.Identity.Data;
using Ratable.Shared.Authors;

namespace Ratable.Modules.Identity.Services;

/// <summary>
/// Gives other modules the little they may know about accounts: the optional public profile, and (for the audited admin look-up only)
/// a summary. Neither ever includes a phone number, an email or any login.
/// </summary>
public sealed class IdentityDirectory(UserRepository users) : IPublicProfiles, IAccountDirectory
{
    public async Task<IReadOnlyDictionary<string, PublicProfile>> GetAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default) =>
        (await users.ListByIdsAsync(userIds, ct)).ToDictionary(u => u.Id, u => new PublicProfile(u.Profile.DisplayName, u.Profile.AvatarUrl, u.PhoneVerified));

    async Task<AccountSummary?> IAccountDirectory.GetAsync(string userId, CancellationToken ct)
    {
        var user = (await users.ListByIdsAsync([userId], ct)).FirstOrDefault();
        return user is null ? null : new AccountSummary(user.Id, user.Profile.DisplayName, user.CreatedAt, user.Status.ToString());
    }
}
