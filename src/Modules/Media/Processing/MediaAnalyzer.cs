using System.Globalization;
using Ratable.Shared.Ai;

namespace Ratable.Modules.Media.Processing;

public sealed record AiRegion(string Kind, double StartSeconds, double EndSeconds, double X, double Y, double Width, double Height);

/// <summary>What the AI returns after looking at the sampled pictures of a video.</summary>
public sealed record FrameAnalysis(
    List<AiRegion>? Regions, int FacesFound, int ChildrenFound, int PlatesFound, bool Nudity, bool Violence, bool PersonalDataOnScreen,
    double? QualityScore, int? BestFrameIndex, List<string>? Concerns);

/// <summary>What the AI returns after listening to the sound.</summary>
public sealed record SpeechAnalysis(string? TranscriptAr, string? TranscriptEn, bool Abusive, bool PersonalData, string? Language);

/// <summary>What the AI returns after looking at a photo.</summary>
public sealed record PhotoAnalysis(int FacesFound, int ChildrenFound, int PlatesFound, bool Nudity, bool Violence, bool PersonalDataOnScreen, double? QualityScore, List<string>? Concerns);

/// <summary>
/// Looks at a review's video or photos through IAiClient (rule 2: the call is logged, priced and capped). When the AI is capped, down or not
/// connected, the result is null, and the caller keeps the item out of public view and tries again later: nothing is published on a missing check.
/// </summary>
public sealed class MediaAnalyzer(IAiClient ai)
{
    private const string System =
        "You check user videos and photos for a review website before they are published. Return only JSON that matches the requested shape. " +
        "Coordinates are shares of the picture from 0 to 1, measured from the top-left corner. Be thorough: missing a child's face or a number plate is the worst mistake.";

    public async Task<FrameAnalysis?> AnalyzeFramesAsync(string itemId, IReadOnlyList<byte[]> frames, IReadOnlyList<double> times, CancellationToken ct)
    {
        var timeList = string.Join(", ", times.Select((t, i) => $"frame {i} at {t.ToString("0.##", CultureInfo.InvariantCulture)} s"));
        var result = await ai.CompleteStructuredAsync<FrameAnalysis>(new AiRequest
        {
            Feature = "video_frames",
            ItemReference = itemId,
            Tier = AiTier.Standard,
            System = System,
            MaxOutputTokens = 3000,
            Attachments = [.. frames.Select(f => new AiAttachment("image/jpeg", f))],
            Prompt =
                $"These {frames.Count} pictures are sampled from one video ({timeList}). In order, they are frames 0 to {frames.Count - 1}. " +
                "Return JSON: {\"regions\":[{\"kind\":\"face\"|\"child_face\"|\"plate\",\"startSeconds\":n,\"endSeconds\":n,\"x\":n,\"y\":n,\"width\":n,\"height\":n}]," +
                "\"facesFound\":n,\"childrenFound\":n,\"platesFound\":n,\"nudity\":bool,\"violence\":bool,\"personalDataOnScreen\":bool," +
                "\"qualityScore\":0..1,\"bestFrameIndex\":n,\"concerns\":[short English phrases]}. " +
                "List a region for every face (use child_face for anyone who looks under 18) and every vehicle number plate, covering the whole time that person or plate is in view; " +
                "make each region a little larger than the face or plate. personalDataOnScreen is true for readable names, phone numbers, ID cards or addresses. " +
                "bestFrameIndex is the sharpest, best-lit frame that shows a person or the place, and is not black or a transition.",
        }, ct);
        return result.Completed ? result.Value : null;
    }

    public async Task<SpeechAnalysis?> AnalyzeSpeechAsync(string itemId, byte[] audio, CancellationToken ct)
    {
        var result = await ai.CompleteStructuredAsync<SpeechAnalysis>(new AiRequest
        {
            Feature = "video_audio",
            ItemReference = itemId,
            Tier = AiTier.Standard,
            System = System,
            MaxOutputTokens = 3000,
            Attachments = [new AiAttachment("audio/mpeg", audio)],
            Prompt =
                "Listen to this audio from a review video. Return JSON: {\"transcriptAr\":\"the words in Arabic\",\"transcriptEn\":\"the words in English\"," +
                "\"abusive\":bool,\"personalData\":bool,\"language\":\"ar\"|\"en\"|other}. " +
                "Transcribe faithfully (Egyptian and Gulf Arabic included) and translate into the other language. abusive is true for insults, hate or threats; " +
                "personalData is true when someone says a phone number, an address, an ID number or a full name of a private person.",
        }, ct);
        return result.Completed ? result.Value : null;
    }

    public async Task<PhotoAnalysis?> AnalyzePhotoAsync(string itemId, byte[] image, string contentType, CancellationToken ct)
    {
        var result = await ai.CompleteStructuredAsync<PhotoAnalysis>(new AiRequest
        {
            Feature = "photo_check",
            ItemReference = itemId,
            Tier = AiTier.Standard,
            System = System,
            MaxOutputTokens = 1500,
            Attachments = [new AiAttachment(contentType, image)],
            Prompt =
                "Return JSON: {\"facesFound\":n,\"childrenFound\":n,\"platesFound\":n,\"nudity\":bool,\"violence\":bool,\"personalDataOnScreen\":bool," +
                "\"qualityScore\":0..1,\"concerns\":[short English phrases]} for this photo from a review.",
        }, ct);
        return result.Completed ? result.Value : null;
    }
}
