using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratable.Modules.Media.Services;

namespace Ratable.Modules.Media.Processing;

public sealed record ProbeResult(double DurationSeconds, int Width, int Height, bool HasVideo, bool HasAudio);

/// <summary>
/// The tools that read and rewrite video: probe it, sample pictures, take the sound, produce the protected adaptive stream and cover candidates.
/// The real one runs ffmpeg; tests use a fake. A missing ffmpeg is a clear failure, never a silent skip.
/// </summary>
public interface IMediaProcessor
{
    Task<ProbeResult> ProbeAsync(string input, CancellationToken cancellationToken);

    /// <summary>Writes frame_1.jpg ... frame_N.jpg into the folder and returns their paths in order.</summary>
    Task<IReadOnlyList<string>> SampleFramesAsync(string input, string folder, int count, double durationSeconds, CancellationToken cancellationToken);

    /// <summary>The sound as a small file, or null when there is none.</summary>
    Task<string?> ExtractAudioAsync(string input, string output, bool hasAudio, CancellationToken cancellationToken);

    /// <summary>Writes stream/index.m3u8 (master) and the renditions, protections applied and metadata removed.</summary>
    Task ProduceStreamAsync(string input, string folder, ProtectionPlan plan, bool hasAudio, CancellationToken cancellationToken);

    /// <summary>Writes poster_0.jpg ... poster_{count-1}.jpg taken from the finished stream (so they are already protected). Returns how many were written.</summary>
    Task<int> ProducePostersAsync(string folder, int count, double durationSeconds, CancellationToken cancellationToken);
}

public sealed class MediaProcessorUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class FfmpegMediaProcessor(IOptions<MediaOptions> options, ILogger<FfmpegMediaProcessor> logger) : IMediaProcessor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private async Task<string> RunAsync(string tool, IReadOnlyList<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);

        Process process;
        try { process = Process.Start(info) ?? throw new MediaProcessorUnavailableException($"{tool} did not start"); }
        catch (System.ComponentModel.Win32Exception ex) { throw new MediaProcessorUnavailableException($"{tool} was not found. Install ffmpeg and set Media__FfmpegPath / Media__FfprobePath.", ex); }

        using (process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ } throw; }
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
            {
                // The error text can name files in the item's private folder (random keys), never anything about the person.
                logger.LogWarning("{Tool} failed with exit code {Code}: {Error}", Path.GetFileName(tool), process.ExitCode, error.Length > 500 ? error[..500] : error);
                throw new InvalidOperationException($"{Path.GetFileName(tool)} failed");
            }
            return output;
        }
    }

    public async Task<ProbeResult> ProbeAsync(string input, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await RunAsync(options.Value.FfprobePath, FfmpegCommands.Probe(input), ct));
        var streams = json.RootElement.TryGetProperty("streams", out var s) ? s.EnumerateArray().ToList() : [];
        var video = streams.FirstOrDefault(x => x.TryGetProperty("codec_type", out var t) && t.GetString() == "video");
        var hasAudio = streams.Any(x => x.TryGetProperty("codec_type", out var t) && t.GetString() == "audio");
        double duration = 0;
        if (json.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        var hasVideo = video.ValueKind == JsonValueKind.Object;
        return new ProbeResult(duration, hasVideo ? video.GetProperty("width").GetInt32() : 0, hasVideo ? video.GetProperty("height").GetInt32() : 0, hasVideo, hasAudio);
    }

    public async Task<IReadOnlyList<string>> SampleFramesAsync(string input, string folder, int count, double durationSeconds, CancellationToken ct)
    {
        await RunAsync(options.Value.FfmpegPath, FfmpegCommands.SampleFrames(input, Path.Combine(folder, "frame_%d.jpg"), count, durationSeconds), ct);
        return [.. Enumerable.Range(1, count).Select(i => Path.Combine(folder, $"frame_{i}.jpg")).Where(File.Exists)];
    }

    public async Task<string?> ExtractAudioAsync(string input, string output, bool hasAudio, CancellationToken ct)
    {
        if (!hasAudio) return null;
        await RunAsync(options.Value.FfmpegPath, FfmpegCommands.ExtractAudio(input, output), ct);
        return File.Exists(output) ? output : null;
    }

    public async Task ProduceStreamAsync(string input, string folder, ProtectionPlan plan, bool hasAudio, CancellationToken ct)
    {
        var streamDirectory = Path.Combine(folder, "stream");
        Directory.CreateDirectory(streamDirectory);
        await RunAsync(options.Value.FfmpegPath, FfmpegCommands.Stream(input, streamDirectory, plan, hasAudio), ct);
        if (!File.Exists(Path.Combine(streamDirectory, "index.m3u8"))) throw new InvalidOperationException("the stream was not produced");
    }

    public async Task<int> ProducePostersAsync(string folder, int count, double durationSeconds, CancellationToken ct)
    {
        var playlist = Path.Combine(folder, "stream", "v0.m3u8");
        var written = 0;
        for (var i = 0; i < count; i++)
        {
            var at = Math.Min(durationSeconds - 0.1, (i + 0.5) * durationSeconds / count);
            await RunAsync(options.Value.FfmpegPath, FfmpegCommands.PosterAt(playlist, Math.Max(0, at), Path.Combine(folder, $"poster_{i}.jpg")), ct);
            if (File.Exists(Path.Combine(folder, $"poster_{i}.jpg"))) written++;
        }
        return written;
    }
}
