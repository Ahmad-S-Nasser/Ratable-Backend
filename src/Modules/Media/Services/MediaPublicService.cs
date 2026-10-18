using System.Text.RegularExpressions;
using Ratable.Modules.Media.Data;
using Ratable.Modules.Media.Models;
using Ratable.Modules.Media.Storage;
using Ratable.Shared.Catalog;
using Ratable.Shared.Content;
using Ratable.Shared.Data;
using Ratable.Shared.Media;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Media.Services;

public sealed record PublicFile(Stream Content, string ContentType, long Length);

/// <summary>A video in the full-screen feed: the video itself and the page of the place it is about. No author, no review text.</summary>
public sealed record FeedItem(PublicMediaItem Media, string ReviewId, string EntityId, string CategoryId, LocalizedText EntityName, Dictionary<string, string> EntitySlugs, string? City);

/// <summary>
/// What the public may see of media, and the one place that decides it: an item is visible only when it is approved, its review is public, and
/// (for an anonymous review) a video was protected for an anonymous author. Files are streamed from private storage and nothing else is reachable.
/// </summary>
public sealed partial class MediaPublicService(MediaRepository items, IMediaStorage storage, IEnumerable<IContentDirectory> directories, IEntityCatalog catalog) : IReviewMedia
{
    private IContentDirectory ReviewDirectory => directories.First(d => d.Kind == ContentKind.Review);

    [GeneratedRegex(@"^(index|v[0-9])\.m3u8$|^v[0-9]_seg_[0-9]{3}\.ts$")]
    private static partial Regex StreamFile();

    private static bool Visible(MediaItem item, bool reviewIsAnonymous) =>
        item.Status == MediaStatus.Published && !(reviewIsAnonymous && item.Kind == MediaKind.Video && !item.ProtectedForAnonymous);

    public static PublicMediaItem ToPublic(MediaItem m) => new(
        m.Id, m.Kind.ToString(), m.DurationSeconds, m.Width, m.Height,
        m.PosterFile is null ? null : $"/api/media/{m.Id}/poster",
        m.HasStream ? $"/api/media/{m.Id}/stream/index.m3u8" : null,
        m.Kind == MediaKind.Photo && m.ImageFile is not null ? $"/api/media/{m.Id}/image" : null,
        m.Captions);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<PublicMediaItem>>> GetPublicAsync(
        IReadOnlyCollection<(string ReviewId, bool IsAnonymous)> reviews, CancellationToken ct = default)
    {
        if (reviews.Count == 0) return new Dictionary<string, IReadOnlyList<PublicMediaItem>>();
        var anonymous = reviews.ToDictionary(r => r.ReviewId, r => r.IsAnonymous);
        var found = await items.ListPublishedForReviewsAsync([.. anonymous.Keys], ct);
        return found.Where(m => Visible(m, anonymous[m.ReviewId]))
            .GroupBy(m => m.ReviewId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PublicMediaItem>)[.. g.Select(ToPublic)]);
    }

    /// <summary>Opens a public file of an item, or null when it is not public (or not there). The same answer for both, so nothing can be probed.</summary>
    public async Task<PublicFile?> OpenAsync(string id, string kind, string? file, CancellationToken ct = default)
    {
        var item = await items.GetAsync(id, ct);
        if (item is null) return null;
        var review = await ReviewDirectory.GetAsync(item.ReviewId, ct);
        if (review is not { IsPublic: true, IsDeleted: false } || !Visible(item, review.IsAnonymous)) return null;

        string? relative = kind switch
        {
            "poster" => item.PosterFile,
            "image" => item.Kind == MediaKind.Photo ? item.ImageFile : null,
            "stream" when item.HasStream && file is not null && StreamFile().IsMatch(file) => $"stream/{file}",
            _ => null,
        };
        if (relative is null) return null;
        var length = storage.FileLength(item.StorageKey, relative);
        var stream = storage.OpenRead(item.StorageKey, relative);
        return stream is null || length is null ? null : new PublicFile(stream, ContentTypeOf(relative), length.Value);
    }

    public static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".ts" => "video/mp2t",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>The newest approved videos of public, non-anonymous-unprotected reviews, each with the place it is about.</summary>
    public async Task<List<FeedItem>> FeedAsync(int limit, int skip, CancellationToken ct = default)
    {
        var result = new List<FeedItem>();
        var cursor = skip;
        // Some candidates are filtered out (review hidden, anonymous and unprotected), so read a little more than asked.
        for (var round = 0; round < 4 && result.Count < limit; round++)
        {
            var batch = await items.ListPublishedLatestVideosAsync(limit * 2, cursor, ct);
            if (batch.Count == 0) break;
            cursor += batch.Count;
            var contexts = new List<(MediaItem Item, ContentInfo Review)>();
            foreach (var m in batch)
            {
                var review = await ReviewDirectory.GetAsync(m.ReviewId, ct);
                if (review is { IsPublic: true, IsDeleted: false, EntityId: not null } && Visible(m, review.IsAnonymous)) contexts.Add((m, review));
            }
            var entries = (await catalog.GetActiveEntriesAsync([.. contexts.Select(c => c.Review.EntityId!).Distinct()], ct)).ToDictionary(e => e.Id);
            foreach (var (item, review) in contexts)
            {
                if (result.Count >= limit) break;
                if (!entries.TryGetValue(review.EntityId!, out var e)) continue;
                result.Add(new FeedItem(ToPublic(item), item.ReviewId, e.Id, e.CategoryId, e.Name, new Dictionary<string, string>(e.Slugs), e.City));
            }
        }
        return result;
    }
}

public sealed class MediaQueueCounter(MediaRepository items) : IQueueCounter
{
    public string Name => "media";

    public Task<long> CountAsync(CancellationToken ct = default) => items.CountPendingAsync(ct);
}
