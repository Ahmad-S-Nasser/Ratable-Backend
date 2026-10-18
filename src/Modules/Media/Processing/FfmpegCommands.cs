using System.Globalization;
using Ratable.Modules.Media.Models;

namespace Ratable.Modules.Media.Processing;

/// <summary>What must be done to a video's picture and sound before it can be published.</summary>
public sealed record ProtectionPlan(IReadOnlyList<ProtectedRegion> Regions, bool ChangeVoice);

/// <summary>
/// The ffmpeg command lines, built as plain argument lists so they can be checked in tests without running ffmpeg. Every output drops metadata
/// (location, device, time) and chapters; the originals are never published.
/// </summary>
public static class FfmpegCommands
{
    private static string N(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    public static IReadOnlyList<string> Probe(string input) => ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", input];

    /// <summary>Evenly spaced sample pictures (frame_1.jpg ...) for the analysis and as cover candidates.</summary>
    public static IReadOnlyList<string> SampleFrames(string input, string outputPattern, int count, double durationSeconds) =>
        ["-y", "-v", "error", "-i", input, "-vf", $"fps={N(count / Math.Max(durationSeconds, 1))},scale=-2:720", "-frames:v", count.ToString(CultureInfo.InvariantCulture), "-q:v", "3", "-map_metadata", "-1", outputPattern];

    public static IReadOnlyList<string> ExtractAudio(string input, string output) =>
        ["-y", "-v", "error", "-i", input, "-vn", "-ac", "1", "-ar", "16000", "-b:a", "32k", "-t", "65", "-map_metadata", "-1", output];

    /// <summary>One cover candidate: the protected picture at a moment of the finished stream.</summary>
    public static IReadOnlyList<string> PosterAt(string playlist, double seconds, string output) =>
        ["-y", "-v", "error", "-ss", N(seconds), "-i", playlist, "-frames:v", "1", "-q:v", "2", "-map_metadata", "-1", output];

    /// <summary>
    /// The video filter graph: each region to protect is cropped, blurred and laid back over the picture for its time span. The graph ends in
    /// [vout]. Coordinates are shares of the picture, so they do not depend on the video's size.
    /// </summary>
    public static string BlurGraph(IReadOnlyList<ProtectedRegion> regions)
    {
        if (regions.Count == 0) return "[0:v]null[vout]";
        var parts = new List<string>();
        var current = "0:v";
        for (var i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            var next = i == regions.Count - 1 ? "vout" : $"v{i + 1}";
            var x = Math.Clamp(r.X, 0, 1);
            var y = Math.Clamp(r.Y, 0, 1);
            var w = Math.Clamp(r.Width, 0.01, 1 - x);
            var h = Math.Clamp(r.Height, 0.01, 1 - y);
            parts.Add($"[{current}]split[m{i}][c{i}]");
            parts.Add($"[c{i}]crop=w=iw*{N(w)}:h=ih*{N(h)}:x=iw*{N(x)}:y=ih*{N(y)},boxblur=luma_radius=min(w\\,h)/4:luma_power=3:chroma_radius=min(cw\\,ch)/4:chroma_power=3[b{i}]");
            parts.Add($"[m{i}][b{i}]overlay=x=main_w*{N(x)}:y=main_h*{N(y)}:enable='between(t\\,{N(r.StartSeconds)}\\,{N(r.EndSeconds)})'[{next}]");
            current = next;
        }
        return string.Join(';', parts);
    }

    /// <summary>A lower voice that no longer matches the speaker: the pitch is shifted and the speed is put back.</summary>
    public const string VoiceChangeFilter = "asetrate=44100*0.82,aresample=44100,atempo=1.2195";

    /// <summary>
    /// The finished stream: protections applied, then two renditions (720p and 480p) cut into adaptive HLS segments under stream/,
    /// with a master playlist stream/index.m3u8. Metadata and chapters are dropped.
    /// </summary>
    public static IReadOnlyList<string> Stream(string input, string streamDirectory, ProtectionPlan plan, bool hasAudio)
    {
        var video = BlurGraph(plan.Regions) + ";[vout]split=2[s1][s2];[s1]scale=-2:720[v720];[s2]scale=-2:480[v480]";
        var args = new List<string> { "-y", "-v", "error", "-i", input };
        if (hasAudio)
        {
            var voice = plan.ChangeVoice ? VoiceChangeFilter + "," : "";
            args.AddRange(["-filter_complex", $"{video};[0:a]{voice}asplit=2[a1][a2]"]);
            args.AddRange(["-map", "[v720]", "-map", "[a1]", "-map", "[v480]", "-map", "[a2]"]);
        }
        else
        {
            args.AddRange(["-filter_complex", video, "-map", "[v720]", "-map", "[v480]"]);
        }
        args.AddRange(
        [
            "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-crf", "23", "-g", "48", "-keyint_min", "48", "-sc_threshold", "0",
            "-b:v:0", "2500k", "-maxrate:v:0", "3000k", "-bufsize:v:0", "5000k", "-b:v:1", "1000k", "-maxrate:v:1", "1200k", "-bufsize:v:1", "2000k",
        ]);
        if (hasAudio) args.AddRange(["-c:a", "aac", "-b:a:0", "128k", "-b:a:1", "96k"]);
        args.AddRange(
        [
            "-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact",
            "-f", "hls", "-hls_time", "4", "-hls_playlist_type", "vod",
            "-hls_segment_filename", Path.Combine(streamDirectory, "v%v_seg_%03d.ts"),
            "-master_pl_name", "index.m3u8",
            "-var_stream_map", hasAudio ? "v:0,a:0 v:1,a:1" : "v:0 v:1",
            Path.Combine(streamDirectory, "v%v.m3u8"),
        ]);
        return args;
    }
}
