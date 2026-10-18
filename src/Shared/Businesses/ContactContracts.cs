namespace Ratable.Shared.Businesses;

/// <summary>How to reach a place: phone, WhatsApp and links. Owner-entered or imported; it is hidden the same way when the profile is locked.</summary>
public sealed record ContactDetails(string? Phone, string? WhatsApp, IReadOnlyList<string> Links)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Phone) && string.IsNullOrWhiteSpace(WhatsApp) && Links.Count == 0;
}

/// <summary>
/// Implemented by the Businesses module. A profile is locked when its category gates contact (a per-category setting, off by default) and the owner's plan does
/// not include contact (CLAUDE.md rule 19). Locking only hides contact details; it never changes a score, a verdict, a ranking or the nearby order.
/// </summary>
public interface IContactAccess
{
    Task<bool> IsLockedAsync(string entityId, string categoryId, CancellationToken cancellationToken = default);

    /// <summary>The ids of the places among <paramref name="places"/> that are locked. Places in categories that do not gate contact are never in it.</summary>
    Task<IReadOnlySet<string>> LockedAmongAsync(IEnumerable<(string EntityId, string CategoryId)> places, CancellationToken cancellationToken = default);

    /// <summary>The owner's plan includes contact (whatever the category says). Used for what the owner may see and do.</summary>
    Task<bool> PlanShowsContactAsync(string entityId, CancellationToken cancellationToken = default);
}

public enum ContactSource { Owner, Import }

/// <summary>Implemented by the Entities module: the contact details stored with a place.</summary>
public interface IEntityContacts
{
    Task<ContactDetails?> GetAsync(string entityId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the contact details. Returns false when the place does not exist.</summary>
    Task<bool> SetAsync(string entityId, ContactDetails details, ContactSource source, CancellationToken cancellationToken = default);
}

public sealed record ActivityTotals(int Views, int ContactTaps);

/// <summary>Implemented by the Entities module: how many times a page was opened and a contact was shown. Counters per day only, never who.</summary>
public interface IEntityActivity
{
    Task<ActivityTotals> GetTotalsAsync(string entityId, DateTime fromDayUtc, CancellationToken cancellationToken = default);
}

/// <summary>Implemented by the Identity module. A reader's phone is shared only with the one provider the reader chose, and only through this.</summary>
public interface IAccountPhones
{
    /// <summary>The decrypted number in international format, or null when the account has none.</summary>
    Task<string?> GetPhoneAsync(string userId, CancellationToken cancellationToken = default);
}
