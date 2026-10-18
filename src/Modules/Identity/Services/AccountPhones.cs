using Ratable.Modules.Identity.Data;
using Ratable.Shared.Businesses;

namespace Ratable.Modules.Identity.Services;

/// <summary>
/// Gives the decrypted phone of an account to the one module that may share it: contact requests, when the reader chose to share their WhatsApp number with that provider.
/// It is never part of a profile, a review or a list (CLAUDE.md rule 18).
/// </summary>
public sealed class AccountPhones(UserRepository users, PhoneProtector protector) : IAccountPhones
{
    public async Task<string?> GetPhoneAsync(string userId, CancellationToken ct = default)
    {
        var user = (await users.ListByIdsAsync([userId], ct)).FirstOrDefault();
        return user?.PhoneEncrypted is { Length: > 0 } encrypted ? protector.Decrypt(encrypted) : null;
    }
}
