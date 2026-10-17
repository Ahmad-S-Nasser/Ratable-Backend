using Ratable.Modules.Identity.Data;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Identity.Services;

/// <summary>Gives moderation the facts it weighs about an account (is the phone verified, which network and device it signed in from, as hashes). Never who they are.</summary>
public sealed class AccountSignalsService(UserRepository users, SessionRepository sessions) : IAccountSignals
{
    public async Task<AccountSignals> GetAsync(string userId, CancellationToken ct = default)
    {
        var user = (await users.ListByIdsAsync([userId], ct)).FirstOrDefault();
        var session = await sessions.GetLatestForUserAsync(userId, ct);
        return new AccountSignals(user?.PhoneVerified ?? false, session?.IpHash, session?.DeviceHash);
    }
}
