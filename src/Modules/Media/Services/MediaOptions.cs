namespace Ratable.Modules.Media.Services;

/// <summary>
/// Bound from "Media". Environment variables: Media__StoragePath, Media__SigningKey, Media__FfmpegPath, Media__FfprobePath, Media__PublicBaseUrl.
/// The limits are the product rules (one video up to 60 seconds and 100 MB, up to 4 photos).
/// </summary>
public sealed class MediaOptions
{
    public const string Section = "Media";

    /// <summary>Private folder for uploads and processed files. Outside any web root. In production an object store (with a CDN in front of the approved files) can replace it behind <c>IMediaStorage</c>.</summary>
    public string StoragePath { get; set; } = "App_Data/media";

    /// <summary>Base64 of 32 random bytes. Signs upload addresses. Never in the repository.</summary>
    public string SigningKey { get; set; } = "";

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    public long MaxVideoBytes { get; set; } = 100L * 1024 * 1024;
    public long MaxPhotoBytes { get; set; } = 10L * 1024 * 1024;
    public int MaxVideoSeconds { get; set; } = 60;
    public int MaxVideosPerReview { get; set; } = 1;
    public int MaxPhotosPerReview { get; set; } = 4;

    /// <summary>The size of one upload piece. A phone on a weak network resumes from the last piece that arrived.</summary>
    public int ChunkBytes { get; set; } = 4 * 1024 * 1024;

    public int UploadLinkMinutes { get; set; } = 60;
    public int MaxUploadsPerDay { get; set; } = 20;

    /// <summary>How many frames are sampled from a video for the analysis and as cover candidates.</summary>
    public int SampleFrames { get; set; } = 8;

    public int WorkerIntervalSeconds { get; set; } = 5;

    /// <summary>Turn the background worker off (tests run the pipeline by hand; a second web server that should not process can switch it off too).</summary>
    public bool WorkerEnabled { get; set; } = true;
}
