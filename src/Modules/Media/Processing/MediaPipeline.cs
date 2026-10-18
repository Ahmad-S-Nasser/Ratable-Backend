using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratable.Modules.Media.Data;
using Ratable.Modules.Media.Models;
using Ratable.Modules.Media.Services;
using Ratable.Modules.Media.Storage;
using Ratable.Shared.Content;
using Ratable.Shared.Data;

namespace Ratable.Modules.Media.Processing;

/// <summary>
/// Everything that happens to an uploaded photo or video before a person looks at it (CLAUDE.md rule 12): the type and length are checked, sample pictures
/// and the sound go through the AI, children's faces and number plates are always blurred, an anonymous author's faces are all blurred and the voice is
/// changed (or the video is refused), metadata is dropped, an adaptive stream and a cover are produced, and captions are stored as text. The result is
/// never public: it waits for a moderator. A video the AI could not check is retried later and can never be approved without that check.
/// </summary>
public sealed class MediaPipeline(
    MediaRepository items, IMediaStorage storage, IMediaProcessor processor, MediaAnalyzer analyzer, IEnumerable<IContentDirectory> directories,
    IOptions<MediaOptions> options, TimeProvider time, ILogger<MediaPipeline> logger)
{
    private const int MaxAttempts = 6;
    private MediaOptions Opt => options.Value;

    private IContentDirectory ReviewDirectory => directories.First(d => d.Kind == ContentKind.Review);

    public async Task ProcessAsync(MediaItem item, CancellationToken ct = default)
    {
        try
        {
            var review = await ReviewDirectory.GetAsync(item.ReviewId, ct);
            if (review is null || review.IsDeleted)
            {
                await RemoveAsync(item, ct);
                return;
            }
            if (item.Kind == MediaKind.Photo) await ProcessPhotoAsync(item, ct);
            else await ProcessVideoAsync(item, review.IsAnonymous, ct);
        }
        catch (MediaProcessorUnavailableException ex)
        {
            logger.LogError(ex, "Media processing is not available");
            await FailAsync(item, "processor_unavailable", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Processing media item {Id} failed (attempt {Attempt})", item.Id, item.Attempts + 1);
            await RetryAsync(item, "processing_failed", ct);
        }
    }

    // ---- photos ----

    private async Task ProcessPhotoAsync(MediaItem item, CancellationToken ct)
    {
        await using var stream = storage.OpenRead(item.StorageKey, item.ImageFile!) ?? throw new InvalidOperationException("the photo is missing");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var analysis = await analyzer.AnalyzePhotoAsync(item.Id, buffer.ToArray(), item.ContentType, ct);

        if (analysis is null)
        {
            // A photo has no automatic gate: a person sees every one anyway, so the check being down only means the person has less help.
            item.Analysis = new MediaAnalysis { Status = AnalysisStatus.Unavailable };
        }
        else
        {
            item.Analysis = new MediaAnalysis
            {
                Status = AnalysisStatus.Completed,
                FacesFound = analysis.FacesFound,
                ChildrenFound = analysis.ChildrenFound,
                PlatesFound = analysis.PlatesFound,
                Nudity = analysis.Nudity,
                Violence = analysis.Violence,
                PersonalDataOnScreen = analysis.PersonalDataOnScreen,
                QualityScore = analysis.QualityScore,
                Concerns = analysis.Concerns ?? [],
            };
        }
        item.PosterFile = item.ImageFile;
        await SaveAsync(item, MediaStatus.PendingModeration, ct);
    }

    // ---- videos ----

    private async Task ProcessVideoAsync(MediaItem item, bool anonymous, CancellationToken ct)
    {
        var folder = storage.WorkDirectory(item.StorageKey);
        var input = Path.Combine(folder, LocalDiskMediaStorage.UploadFile);
        if (!File.Exists(input)) throw new InvalidOperationException("the upload is missing");

        var probe = await processor.ProbeAsync(input, ct);
        if (!probe.HasVideo)
        {
            await RejectAsync(item, "not_a_video", ct); return;
        }
        if (probe.DurationSeconds > Opt.MaxVideoSeconds + 0.5) { await RejectAsync(item, "too_long", ct); return; }
        item.DurationSeconds = Math.Round(probe.DurationSeconds, 1);
        item.Width = probe.Width;
        item.Height = probe.Height;

        var count = Math.Clamp((int)Math.Floor(probe.DurationSeconds), 1, Opt.SampleFrames);
        var framePaths = await processor.SampleFramesAsync(input, folder, count, probe.DurationSeconds, ct);
        var frames = new List<byte[]>();
        foreach (var path in framePaths) frames.Add(await File.ReadAllBytesAsync(path, ct));
        var times = Enumerable.Range(0, frames.Count).Select(i => (i + 0.5) * probe.DurationSeconds / frames.Count).ToList();

        var analysis = frames.Count == 0 ? null : await analyzer.AnalyzeFramesAsync(item.Id, frames, times, ct);
        SpeechAnalysis? speech = null;
        var audioPath = await processor.ExtractAudioAsync(input, Path.Combine(folder, "audio.mp3"), probe.HasAudio, ct);
        if (audioPath is not null) speech = await analyzer.AnalyzeSpeechAsync(item.Id, await File.ReadAllBytesAsync(audioPath, ct), ct);

        if (analysis is null || (audioPath is not null && speech is null))
        {
            // Not checked, so not processed: the AI may be capped or down for now. Try again later; nothing is published meanwhile.
            await RetryAsync(item, "analysis_unavailable", ct);
            return;
        }

        // Which parts of the picture must be hidden. Children's faces and number plates always; every face for an anonymous author.
        var regions = (analysis.Regions ?? [])
            .Where(r => r.EndSeconds >= r.StartSeconds && r.Width > 0 && r.Height > 0)
            .Where(r => r.Kind is "child_face" or "plate" || (anonymous && r.Kind == "face"))
            .Select(r => new ProtectedRegion { Kind = r.Kind, StartSeconds = Math.Max(0, r.StartSeconds), EndSeconds = Math.Min(probe.DurationSeconds, r.EndSeconds), X = r.X, Y = r.Y, Width = r.Width, Height = r.Height })
            .ToList();

        // Seeing a face, a child or a plate without being able to place it means it cannot be hidden: the video is refused.
        var anyFace = analysis.FacesFound > 0 || analysis.ChildrenFound > 0;
        if (anonymous && anyFace && !regions.Any(r => r.Kind is "face" or "child_face")) { await RejectAsync(item, "anonymous_video_not_protected", ct); return; }
        if (analysis.ChildrenFound > 0 && !regions.Any(r => r.Kind == "child_face")) { await RejectAsync(item, "children_cannot_be_protected", ct); return; }
        if (analysis.PlatesFound > 0 && !regions.Any(r => r.Kind == "plate")) { await RejectAsync(item, "plates_cannot_be_protected", ct); return; }

        var changeVoice = anonymous && probe.HasAudio;
        await processor.ProduceStreamAsync(input, folder, new ProtectionPlan(regions, changeVoice), probe.HasAudio, ct);
        var posters = await processor.ProducePostersAsync(folder, count, probe.DurationSeconds, ct);

        item.Regions = regions;
        item.BlurApplied = regions.Count > 0;
        item.VoiceChanged = changeVoice;
        item.ProtectedForAnonymous = anonymous;
        item.HasStream = true;
        item.CandidateFrames = posters;

        var concerns = new List<string>(analysis.Concerns ?? []);
        if (speech is { Abusive: true }) concerns.Add("abusive speech");
        if (speech is { PersonalData: true }) concerns.Add("personal data in speech");
        item.Analysis = new MediaAnalysis
        {
            Status = AnalysisStatus.Completed,
            FacesFound = analysis.FacesFound,
            ChildrenFound = analysis.ChildrenFound,
            PlatesFound = analysis.PlatesFound,
            Nudity = analysis.Nudity,
            Violence = analysis.Violence,
            PersonalDataOnScreen = analysis.PersonalDataOnScreen,
            QualityScore = analysis.QualityScore,
            BestFrameIndex = analysis.BestFrameIndex,
            AbusiveSpeech = speech?.Abusive ?? false,
            PersonalDataInSpeech = speech?.PersonalData ?? false,
            Concerns = concerns,
        };

        // Captions are stored as text so they can be found by search; a transcript with personal data is not stored.
        item.Captions = speech is { PersonalData: false, Abusive: false } && (speech.TranscriptAr is not null || speech.TranscriptEn is not null)
            ? new LocalizedText(speech.TranscriptAr, speech.TranscriptEn)
            : null;

        ApplyCover(item, storage, folder);

        // The original (with unblurred faces and the real voice) and the working files are deleted as soon as the protected stream exists.
        foreach (var temp in framePaths.Append(audioPath).Append(input).Where(p => p is not null && File.Exists(p))) File.Delete(temp!);
        await SaveAsync(item, MediaStatus.PendingModeration, ct);
    }

    /// <summary>Makes poster.jpg from the chosen candidate: the author's pick, else the AI's best frame, else the first one.</summary>
    public static void ApplyCover(MediaItem item, IMediaStorage storage, string folder)
    {
        if (item.CandidateFrames == 0) return;
        var index = item.Cover switch
        {
            CoverChoice.First => 0,
            CoverChoice.Frame => item.CoverFrameIndex,
            _ => item.Analysis.BestFrameIndex ?? 0,
        };
        if (index < 0 || index >= item.CandidateFrames || !File.Exists(Path.Combine(folder, $"poster_{index}.jpg"))) index = 0;
        File.Copy(Path.Combine(folder, $"poster_{index}.jpg"), Path.Combine(folder, "poster.jpg"), overwrite: true);
        item.PosterFile = "poster.jpg";
    }

    // ---- outcomes ----

    private async Task SaveAsync(MediaItem item, MediaStatus status, CancellationToken ct)
    {
        item.Status = status;
        item.Attempts = 0;
        item.NextAttemptAt = null;
        await items.ReplaceAsync(item, ct);
    }

    private async Task RejectAsync(MediaItem item, string reason, CancellationToken ct)
    {
        await storage.DeleteAllAsync(item.StorageKey, ct);
        item.Status = MediaStatus.Rejected;
        item.RejectionReason = reason;
        item.HasStream = false;
        item.PosterFile = null;
        item.ImageFile = null;
        await items.ReplaceAsync(item, ct);
    }

    private async Task FailAsync(MediaItem item, string reason, CancellationToken ct)
    {
        item.Status = MediaStatus.Failed;
        item.FailureReason = reason;
        await items.ReplaceAsync(item, ct);
    }

    private async Task RetryAsync(MediaItem item, string reason, CancellationToken ct)
    {
        item.Attempts++;
        if (item.Attempts >= MaxAttempts)
        {
            await FailAsync(item, reason, ct);
            return;
        }
        item.Status = MediaStatus.Uploaded;
        item.NextAttemptAt = time.GetUtcNow().UtcDateTime.AddMinutes(Math.Min(30, 2 << item.Attempts));
        await items.ReplaceAsync(item, ct);
    }

    private async Task RemoveAsync(MediaItem item, CancellationToken ct)
    {
        await storage.DeleteAllAsync(item.StorageKey, ct);
        await items.DeleteAsync(item.Id, ct);
    }
}
