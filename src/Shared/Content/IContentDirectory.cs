namespace Ratable.Shared.Content;

public enum ContentKind { Review, Comment }

/// <summary>What the Moderation module may know about a piece of content: who wrote it, whether it is anonymous, and whether the public can see it.</summary>
public sealed record ContentInfo(string Id, string AuthorId, bool IsAnonymous, bool IsPublic, string? CategoryId, string? EntityId = null, bool IsDeleted = false);

/// <summary>
/// Implemented by each module that owns content users can report (Reviews, Comments). Lets Moderation validate a report and run the audited
/// anonymous-author look-up without touching those modules' collections.
/// </summary>
public interface IContentDirectory
{
    ContentKind Kind { get; }

    Task<ContentInfo?> GetAsync(string id, CancellationToken cancellationToken = default);
}
