namespace Ratable.Shared.Authors;

/// <summary>What the public may see of a named author.</summary>
public sealed record PublicAuthor(string Name, string? AvatarUrl, bool AccountVerified = false);

public sealed record PublicProfile(string? DisplayName, string? AvatarUrl, bool AccountVerified = false);

/// <summary>One item whose author is to be presented. <see cref="Key"/> is the caller's own id for the item (a review id, a comment id).</summary>
public sealed record AuthorSlot(string Key, string UserId, bool IsAnonymous);

/// <summary>Implemented by the Identity module: the optional public profile of accounts.</summary>
public interface IPublicProfiles
{
    Task<IReadOnlyDictionary<string, PublicProfile>> GetAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default);
}

public sealed record AccountSummary(string UserId, string? DisplayName, DateTime CreatedAt, string Status);

/// <summary>Implemented by the Identity module. Only for the audited admin look-up of an anonymous author. Never includes a phone number.</summary>
public interface IAccountDirectory
{
    Task<AccountSummary?> GetAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// THE one place that decides whether an author is shown (CLAUDE.md rule 1). Every public review and comment goes through it.
/// An anonymous item gets null, and its author's profile is never even looked up. A named item gets the public profile name; an account
/// without a public display name is shown as anonymous too, because showing nothing is safer than inventing a name.
/// </summary>
public interface IAuthorPresenter
{
    Task<IReadOnlyDictionary<string, PublicAuthor?>> PresentAsync(IReadOnlyCollection<AuthorSlot> items, CancellationToken cancellationToken = default);
}

public sealed class AuthorPresenter(IPublicProfiles profiles) : IAuthorPresenter
{
    public async Task<IReadOnlyDictionary<string, PublicAuthor?>> PresentAsync(IReadOnlyCollection<AuthorSlot> items, CancellationToken ct = default)
    {
        var result = new Dictionary<string, PublicAuthor?>();
        var named = items.Where(i => !i.IsAnonymous).ToList();
        foreach (var anonymous in items.Where(i => i.IsAnonymous)) result[anonymous.Key] = null;

        if (named.Count > 0)
        {
            var found = await profiles.GetAsync([.. named.Select(n => n.UserId).Distinct()], ct);
            foreach (var slot in named)
                result[slot.Key] = found.TryGetValue(slot.UserId, out var p) && !string.IsNullOrWhiteSpace(p.DisplayName)
                    ? new PublicAuthor(p.DisplayName.Trim(), p.AvatarUrl, p.AccountVerified)
                    : null;
        }
        return result;
    }
}
