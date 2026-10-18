using System.Text.RegularExpressions;
using Ratable.Modules.Media.Data;
using Ratable.Modules.Media.Models;
using Ratable.Modules.Media.Storage;
using Ratable.Shared.Audit;
using Ratable.Shared.Content;

namespace Ratable.Modules.Media.Services;

public enum ModerationFailure { None, NotFound, WrongState, AnalysisRequired, NotProtectedForAnonymous, FacesOrPlatesVisible, ReasonRequired, FileNotAvailable }

public sealed record ModerationResult(ModerationFailure Failure, MediaItem? Item = null)
{
    public bool Ok => Failure == ModerationFailure.None;
}

/// <summary>
/// The person's decision on media. Nothing becomes public without it, and some things cannot be approved even by a person: a video the AI did not check,
/// a video that was not protected for an anonymous author, a photo showing faces or number plates (there is no automatic blur for photos yet).
/// Every look at a file and every decision is written to the audit log.
/// </summary>
public sealed partial class MediaModerationService(MediaRepository items, IMediaStorage storage, IEnumerable<IContentDirectory> directories, IAuditLog audit, TimeProvider time)
{
    [GeneratedRegex(@"^(poster(_[0-9])?\.jpg|image\.[a-z0-9]+|stream/(index|v[0-9])\.m3u8|stream/v[0-9]_seg_[0-9]{3}\.ts)$")]
    private static partial Regex PreviewFile();

    public Task<List<MediaItem>> QueueAsync(CancellationToken ct = default) => items.ListPendingAsync(200, ct);

    public async Task<(ModerationResult Result, PublicFile? File)> OpenPreviewAsync(string moderatorId, string id, string file, CancellationToken ct = default)
    {
        var item = await items.GetAsync(id, ct);
        if (item is null) return (new(ModerationFailure.NotFound), null);
        if (!PreviewFile().IsMatch(file)) return (new(ModerationFailure.FileNotAvailable), null);
        var stream = storage.OpenRead(item.StorageKey, file);
        var length = storage.FileLength(item.StorageKey, file);
        if (stream is null || length is null) return (new(ModerationFailure.FileNotAvailable), null);
        // Only the start of a playlist or the poster is noted, so a video that is watched in many pieces makes one entry, not hundreds.
        if (file is "index.m3u8" or "stream/index.m3u8" or "poster.jpg" or "image.jpg" or "image.png" or "image.webp")
            await audit.AppendAsync(new AuditRecord(moderatorId, "media.viewed", "media", item.Id, "Opened the media for review"), ct);
        return (new(ModerationFailure.None, item), new PublicFile(stream, MediaPublicService.ContentTypeOf(file), length.Value));
    }

    public async Task<ModerationResult> ApproveAsync(string moderatorId, string id, CancellationToken ct = default)
    {
        var item = await items.GetAsync(id, ct);
        if (item is null) return new(ModerationFailure.NotFound);
        if (item.Status != MediaStatus.PendingModeration) return new(ModerationFailure.WrongState, item);

        var review = await directories.First(d => d.Kind == ContentKind.Review).GetAsync(item.ReviewId, ct);
        if (review is null || review.IsDeleted) return new(ModerationFailure.NotFound);

        if (item.Kind == MediaKind.Video)
        {
            if (item.Analysis.Status != AnalysisStatus.Completed || !item.HasStream) return new(ModerationFailure.AnalysisRequired, item);
            if (review.IsAnonymous && !item.ProtectedForAnonymous) return new(ModerationFailure.NotProtectedForAnonymous, item);
        }
        else if (item.Analysis is { Status: AnalysisStatus.Completed } a && (a.FacesFound > 0 || a.ChildrenFound > 0 || a.PlatesFound > 0))
        {
            return new(ModerationFailure.FacesOrPlatesVisible, item);
        }

        item.Status = MediaStatus.Published;
        item.DecidedAt = time.GetUtcNow().UtcDateTime;
        item.DecidedBy = moderatorId;
        await items.ReplaceAsync(item, ct);

        // Only the chosen cover is kept; the other candidates and the working copies are no longer needed.
        if (item.Kind == MediaKind.Video)
            for (var i = 0; i < item.CandidateFrames; i++) await storage.DeleteFileAsync(item.StorageKey, $"poster_{i}.jpg", ct);
        await audit.AppendAsync(new AuditRecord(moderatorId, "media.approved", "media", item.Id, "Approved"), ct);
        return new(ModerationFailure.None, item);
    }

    public async Task<ModerationResult> RejectAsync(string moderatorId, string id, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 300) return new(ModerationFailure.ReasonRequired);
        var item = await items.GetAsync(id, ct);
        if (item is null) return new(ModerationFailure.NotFound);
        if (item.Status is not (MediaStatus.PendingModeration or MediaStatus.Published)) return new(ModerationFailure.WrongState, item);

        await storage.DeleteAllAsync(item.StorageKey, ct);
        item.Status = MediaStatus.Rejected;
        item.RejectionReason = "moderator";
        item.ModeratorReason = reason.Trim();
        item.HasStream = false;
        item.PosterFile = null;
        item.ImageFile = null;
        item.Captions = null;
        item.DecidedAt = time.GetUtcNow().UtcDateTime;
        item.DecidedBy = moderatorId;
        await items.ReplaceAsync(item, ct);
        await audit.AppendAsync(new AuditRecord(moderatorId, "media.rejected", "media", item.Id, reason.Trim()), ct);
        return new(ModerationFailure.None, item);
    }
}
