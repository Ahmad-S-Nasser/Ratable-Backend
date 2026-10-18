using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Ratable.Shared.Data;

namespace Ratable.Modules.Media.Models;

public enum MediaKind { Video, Photo }

public enum MediaStatus
{
    /// <summary>The upload is not complete yet (it can be resumed).</summary>
    Uploading,
    /// <summary>All bytes arrived and the file type was checked. Waiting for the worker.</summary>
    Uploaded,
    Processing,
    /// <summary>Processed and analysed. Waiting for a person. Nothing is public until a moderator approves it.</summary>
    PendingModeration,
    Published,
    Rejected,
    /// <summary>The worker could not process it (see <see cref="MediaItem.FailureReason"/>).</summary>
    Failed,
}

public enum AnalysisStatus { None, Completed, Unavailable }

public enum CoverChoice { Auto, First, Frame }

/// <summary>A region to blur: a face, a child's face or a number plate, over a stretch of time. Coordinates are shares of the picture (0 to 1).</summary>
public sealed class ProtectedRegion
{
    public string Kind { get; set; } = "face";
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>What the automatic analysis found. Moderators see this; the author sees only the outcome.</summary>
public sealed class MediaAnalysis
{
    public AnalysisStatus Status { get; set; }
    public int FacesFound { get; set; }
    public int ChildrenFound { get; set; }
    public int PlatesFound { get; set; }
    public bool Nudity { get; set; }
    public bool Violence { get; set; }
    public bool PersonalDataOnScreen { get; set; }
    public bool AbusiveSpeech { get; set; }
    public bool PersonalDataInSpeech { get; set; }
    public double? QualityScore { get; set; }
    public int? BestFrameIndex { get; set; }
    public List<string> Concerns { get; set; } = [];
    public string? Model { get; set; }
}

public sealed class MediaItem
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    public string ReviewId { get; set; } = "";

    /// <summary>The author. Never part of any public response.</summary>
    public string UserId { get; set; } = "";

    public MediaKind Kind { get; set; }
    public string ContentType { get; set; } = "";
    public long DeclaredBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public MediaStatus Status { get; set; } = MediaStatus.Uploading;

    /// <summary>A short code such as "too_long" or "anonymous_video_not_protected"; the author sees it translated.</summary>
    public string? RejectionReason { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>The words a moderator wrote when rejecting. The author sees them (the same rule as a hidden review).</summary>
    public string? ModeratorReason { get; set; }

    /// <summary>The random folder name in private storage. No name the user sent is ever stored (a file name can identify a person).</summary>
    public string StorageKey { get; set; } = "";

    public double? DurationSeconds { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>The picture file name inside the storage folder (a photo, or the chosen poster of a video).</summary>
    public string? PosterFile { get; set; }
    public string? ImageFile { get; set; }
    public bool HasStream { get; set; }
    public int CandidateFrames { get; set; }

    public CoverChoice Cover { get; set; } = CoverChoice.Auto;
    public int CoverFrameIndex { get; set; }

    public MediaAnalysis Analysis { get; set; } = new();
    public List<ProtectedRegion> Regions { get; set; } = [];

    /// <summary>The review was anonymous when the video was processed and every face was blurred and the voice changed.</summary>
    public bool ProtectedForAnonymous { get; set; }
    public bool BlurApplied { get; set; }
    public bool VoiceChanged { get; set; }

    /// <summary>The words spoken, in Arabic and in English, stored as text so they can be indexed.</summary>
    public LocalizedText? Captions { get; set; }

    /// <summary>How many times processing was tried, and when to try again (the AI may be capped or down for a while).</summary>
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
}
