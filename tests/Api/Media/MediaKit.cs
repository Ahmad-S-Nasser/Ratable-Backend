using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Media.Data;
using Ratable.Modules.Media.Models;
using Ratable.Modules.Media.Processing;
using Ratable.Modules.Media.Services;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Media;

/// <summary>A stand-in for ffmpeg: it writes small files where the real one would, and remembers what it was asked to do.</summary>
public sealed class FakeMediaProcessor : IMediaProcessor
{
    public ProbeResult Probe { get; set; } = new(20, 1280, 720, true, true);
    public ProtectionPlan? LastPlan { get; private set; }
    public bool Unavailable { get; set; }

    public Task<ProbeResult> ProbeAsync(string input, CancellationToken ct) =>
        Unavailable ? throw new MediaProcessorUnavailableException("ffmpeg not found") : Task.FromResult(Probe);

    public Task<IReadOnlyList<string>> SampleFramesAsync(string input, string folder, int count, double durationSeconds, CancellationToken ct)
    {
        var paths = new List<string>();
        for (var i = 1; i <= count; i++)
        {
            var path = Path.Combine(folder, $"frame_{i}.jpg");
            File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9, (byte)i]);
            paths.Add(path);
        }
        return Task.FromResult<IReadOnlyList<string>>(paths);
    }

    public Task<string?> ExtractAudioAsync(string input, string output, bool hasAudio, CancellationToken ct)
    {
        if (!hasAudio) return Task.FromResult<string?>(null);
        File.WriteAllBytes(output, [1, 2, 3]);
        return Task.FromResult<string?>(output);
    }

    public Task ProduceStreamAsync(string input, string folder, ProtectionPlan plan, bool hasAudio, CancellationToken ct)
    {
        LastPlan = plan;
        var stream = Path.Combine(folder, "stream");
        Directory.CreateDirectory(stream);
        File.WriteAllText(Path.Combine(stream, "index.m3u8"), "#EXTM3U\nv0.m3u8\n");
        File.WriteAllText(Path.Combine(stream, "v0.m3u8"), "#EXTM3U\nv0_seg_000.ts\n");
        File.WriteAllBytes(Path.Combine(stream, "v0_seg_000.ts"), [0x47, 0, 0, 0]);
        return Task.CompletedTask;
    }

    public Task<int> ProducePostersAsync(string folder, int count, double durationSeconds, CancellationToken ct)
    {
        for (var i = 0; i < count; i++) File.WriteAllBytes(Path.Combine(folder, $"poster_{i}.jpg"), [0xFF, 0xD8, 0xFF, 0xD9, (byte)(100 + i)]);
        return Task.FromResult(count);
    }
}

public sealed record Setup(IdentityApiFactory Api, FakeMediaProcessor Processor, string Storage) : IDisposable
{
    public void Dispose()
    {
        if (Directory.Exists(Storage)) Directory.Delete(Storage, recursive: true);
    }

    public MediaRepository Items => Api.Services.GetRequiredService<MediaRepository>();
    public MediaPipeline Pipeline => Api.Services.GetRequiredService<MediaPipeline>();
}

public static class MediaKit
{
    public const string AdminPhone = "+201000000001";

    public static Setup NewSetup(Dictionary<string, string>? extra = null)
    {
        var storage = Path.Combine(Path.GetTempPath(), $"ratable-media-{Guid.NewGuid():N}");
        var processor = new FakeMediaProcessor();
        var settings = new Dictionary<string, string>
        {
            ["Identity:BootstrapAdminPhone"] = AdminPhone,
            ["Media:StoragePath"] = storage,
            ["Media:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Media:WorkerEnabled"] = "false",
            ["Media:ChunkBytes"] = "1048576",
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        var api = new IdentityApiFactory(settings, fakeAi: true, configure: services => services.AddSingleton<IMediaProcessor>(processor));
        return new Setup(api, processor, storage);
    }

    public static string IdOf(IdentityApiFactory.SignedIn s) => s.Body.GetProperty("user").GetProperty("id").GetString()!;

    // Small files that start with the bytes of the real formats.
    public static byte[] Jpeg(params byte[][] extraSegments)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        foreach (var s in extraSegments) bytes.AddRange(s);
        bytes.AddRange([0xFF, 0xDA, 0x00, 0x02, 0x11, 0x22, 0x33, 0xFF, 0xD9]);
        return [.. bytes];
    }

    /// <summary>A JPEG APP1 segment carrying EXIF with a recognisable "GPS" marker.</summary>
    public static byte[] ExifSegment(string marker = "GPSLatitude-30.05")
    {
        var body = System.Text.Encoding.ASCII.GetBytes("Exif\0\0" + marker);
        var length = body.Length + 2;
        return [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. body];
    }

    public static byte[] Mp4(int size = 4096)
    {
        var bytes = new byte[size];
        new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2' }.CopyTo(bytes, 0);
        return bytes;
    }

    public static async Task<(HttpResponseMessage Response, JsonElement Json)> Put(IdentityApiFactory api, string url, byte[] content, long start, long total, long? claimedLength = null)
    {
        using var http = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + (claimedLength ?? content.Length) - 1, total);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, text.StartsWith('{') ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    public sealed record Upload(string MediaId, string Url);

    /// <summary>Asks for an upload address as the author and returns it.</summary>
    public static async Task<(HttpResponseMessage Response, JsonElement Json)> Ask(IdentityApiFactory api, string cookie, string reviewId, string kind, string contentType, long size) =>
        await Send(api, HttpMethod.Post, $"/api/reviews/{reviewId}/media", cookie, new { kind, contentType, sizeBytes = size });

    public static async Task<Upload> StartUpload(IdentityApiFactory api, string cookie, string reviewId, string kind, string contentType, long size)
    {
        var (response, json) = await Ask(api, cookie, reviewId, kind, contentType, size);
        Xunit.Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new Upload(json.GetProperty("mediaId").GetString()!, json.GetProperty("uploadUrl").GetString()!);
    }

    /// <summary>Sends a whole file in one piece and returns the item as stored.</summary>
    public static async Task<MediaItem> UploadWhole(Setup s, string cookie, string reviewId, string kind, string contentType, byte[] file)
    {
        var upload = await StartUpload(s.Api, cookie, reviewId, kind, contentType, file.Length);
        var (response, _) = await Put(s.Api, upload.Url, file, 0, file.Length);
        Xunit.Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await s.Items.GetAsync(upload.MediaId))!;
    }

    public static async Task<string> NewPlaceAndReview(Setup s, IdentityApiFactory.SignedIn author, bool anonymous = false, string category = "compounds")
    {
        var entity = await SeedEntity(s.Api, category);
        s.Api.Ai.Enqueue(new Ratable.Modules.Moderation.Services.PrescreenAnswer(new(false, 0.02), new(false, 0.01), new(false, 0.03), new(false, 0.04), new(false, 0.0)));
        var (response, json) = await Send(s.Api, HttpMethod.Post, $"/api/entities/{entity.Id}/reviews", author.Cookie,
            new { answers = Answers(("security", 4)), text = $"A review with enough words in it to read {Guid.NewGuid():N}", isAnonymous = anonymous });
        Xunit.Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return json.GetProperty("id").GetString()!;
    }

    /// <summary>Makes a review public (the moderation flow has its own tests).</summary>
    public static async Task PublishReview(Setup s, string reviewId) => await SetReviewState(s.Api, reviewId, ReviewStatus.Published);

    public static async Task<IdentityApiFactory.SignedIn> Moderator(Setup s)
    {
        var admin = await s.Api.SignInAsync(AdminPhone);
        return admin;
    }

    public static string FolderOf(Setup s, MediaItem item) => Path.Combine(s.Storage, item.StorageKey[..2], item.StorageKey);

    public static ReviewRepository ReviewRepo(Setup s) => s.Api.Services.GetRequiredService<ReviewRepository>();
}
