using System.Net;
using System.Text.Json;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Media.Models;
using Ratable.Modules.Media.Processing;
using static Ratable.Api.Tests.Media.MediaKit;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Media;

public sealed class MediaPublicTests
{
    private static FrameAnalysis Clean(params AiRegion[] regions) => new([.. regions], regions.Length, 0, 0, false, false, false, 0.9, 1, []);

    private static SpeechAnalysis Speech() => new("كلام عربي", "English words", false, false, "ar");

    /// <summary>A published video of a review, made the way the product makes one: upload, process, a moderator approves.</summary>
    private static async Task<(MediaItem Item, string ReviewId, IdentityApiFactory.SignedIn Author, IdentityApiFactory.SignedIn Admin)> Approved(Setup s, bool anonymous)
    {
        var author = await s.Api.SignInAsync("+201111111111");
        var admin = await Moderator(s);
        var review = await NewPlaceAndReview(s, author, anonymous);
        var item = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());
        s.Api.Ai.Enqueue(Clean(new AiRegion("face", 0, 20, 0.3, 0.2, 0.2, 0.3)));
        s.Api.Ai.Enqueue(Speech());
        await s.Pipeline.ProcessAsync((await s.Items.ClaimNextUploadedAsync())!);
        var response = await Send(s.Api, HttpMethod.Post, $"/api/moderation/media/{item.Id}/approve", admin.Cookie);
        Assert.Equal(HttpStatusCode.OK, response.Response.StatusCode);
        await PublishReview(s, review);
        return ((await s.Items.GetAsync(item.Id))!, review, author, admin);
    }

    [MongoFact]
    public async Task Media_is_never_public_before_a_person_approves_it()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);
        await PublishReview(s, review);
        var item = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());
        api.Ai.Enqueue(Clean());
        api.Ai.Enqueue(Speech());
        await s.Pipeline.ProcessAsync((await s.Items.ClaimNextUploadedAsync())!);
        Assert.Equal(MediaStatus.PendingModeration, (await s.Items.GetAsync(item.Id))!.Status);

        // The review is public, the video has been processed, and still nothing about the video can be reached.
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, $"/api/media/{item.Id}/poster")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, $"/api/media/{item.Id}/stream/index.m3u8")).Response.StatusCode);
        Assert.Empty((await Send(api, HttpMethod.Get, "/api/media/feed")).Json.EnumerateArray());
        var entityId = (await ReviewRepo(s).GetAsync(review))!.EntityId;
        var list = await Send(api, HttpMethod.Get, $"/api/entities/{entityId}/reviews");
        Assert.Empty(Assert.Single(list.Json.EnumerateArray()).GetProperty("media").EnumerateArray());
    }

    [MongoFact]
    public async Task An_approved_video_is_streamed_and_listed_with_its_review_and_the_feed()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var (item, review, _, _) = await Approved(s, anonymous: false);
        var entityId = (await ReviewRepo(s).GetAsync(review))!.EntityId;

        var poster = await Send(api, HttpMethod.Get, $"/api/media/{item.Id}/poster");
        Assert.Equal(HttpStatusCode.OK, poster.Response.StatusCode);
        Assert.Equal("image/jpeg", poster.Response.Content.Headers.ContentType!.MediaType);
        var playlist = await api.CreateClient().GetAsync($"/api/media/{item.Id}/stream/index.m3u8");
        Assert.Equal("application/vnd.apple.mpegurl", playlist.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync($"/api/media/{item.Id}/stream/v0_seg_000.ts")).StatusCode);
        // Only the known stream files exist; nothing else in the folder can be asked for.
        Assert.Equal(HttpStatusCode.NotFound, (await api.CreateClient().GetAsync($"/api/media/{item.Id}/stream/upload.part")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.CreateClient().GetAsync($"/api/media/{item.Id}/stream/..%2Fposter.jpg")).StatusCode);
        // The cover candidates other than the chosen cover were deleted on approval.
        Assert.Empty(Directory.GetFiles(FolderOf(s, item), "poster_*"));

        var list = await Send(api, HttpMethod.Get, $"/api/entities/{entityId}/reviews");
        var media = Assert.Single(Assert.Single(list.Json.EnumerateArray()).GetProperty("media").EnumerateArray());
        Assert.Equal("Video", media.GetProperty("kind").GetString());
        Assert.Equal($"/api/media/{item.Id}/stream/index.m3u8", media.GetProperty("playlistUrl").GetString());
        Assert.Equal("English words", media.GetProperty("captions").GetProperty("en").GetString());

        var feed = Assert.Single((await Send(api, HttpMethod.Get, "/api/media/feed")).Json.EnumerateArray());
        Assert.Equal(entityId, feed.GetProperty("entityId").GetString());
        Assert.Equal(item.Id, feed.GetProperty("media").GetProperty("id").GetString());
    }

    [MongoFact]
    public async Task A_hidden_review_takes_its_media_out_of_public_view()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var (item, review, _, _) = await Approved(s, anonymous: false);

        await SetReviewState(api, review, Ratable.Modules.Reviews.Models.ReviewStatus.Hidden);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, $"/api/media/{item.Id}/poster")).Response.StatusCode);
        Assert.Empty((await Send(api, HttpMethod.Get, "/api/media/feed")).Json.EnumerateArray());
    }

    [MongoFact]
    public async Task An_anonymous_author_never_leaks_through_the_media_responses_the_files_or_the_captions()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var (item, review, author, _) = await Approved(s, anonymous: true);
        var entityId = (await ReviewRepo(s).GetAsync(review))!.EntityId;
        var userId = IdOf(author);

        // Give the author a public name, so a leak would be visible.
        await Send(api, HttpMethod.Put, "/api/me/profile", author.Cookie, new { displayName = "Secret Sara" });

        var bodies = new List<string>();
        foreach (var url in new[] { $"/api/entities/{entityId}/reviews", "/api/media/feed", $"/api/reviews/{review}/history" })
        {
            var response = await api.CreateClient().GetAsync(url);
            bodies.Add(await response.Content.ReadAsStringAsync());
            bodies.Add(string.Join(' ', response.Headers.Select(h => $"{h.Key}={string.Join(',', h.Value)}")));
        }
        bodies.Add((await api.CreateClient().GetAsync($"/api/media/{item.Id}/stream/index.m3u8")).Content.ReadAsStringAsync().Result);
        foreach (var body in bodies)
        {
            Assert.DoesNotContain("Secret Sara", body);
            Assert.DoesNotContain(userId, body);
            Assert.DoesNotContain("1111111", body); // no part of the phone number
        }

        // The stored item and its folder carry no name or id of the author either: only random keys.
        var folderNames = Directory.GetFileSystemEntries(FolderOf(s, item), "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.All(folderNames, n => Assert.DoesNotContain("Sara", n));
        Assert.Matches("^[a-f0-9]{32}$", item.StorageKey);

        // The public item has exactly these fields: no user, no file name, no upload detail.
        var feed = (await Send(api, HttpMethod.Get, "/api/media/feed")).Json.EnumerateArray().Single();
        var fields = feed.GetProperty("media").EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal(["captions", "durationSeconds", "height", "id", "imageUrl", "kind", "playlistUrl", "posterUrl", "width"], fields);
    }

    [MongoFact]
    public async Task A_video_that_was_not_protected_is_hidden_the_moment_its_review_becomes_anonymous()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var (item, review, author, _) = await Approved(s, anonymous: false);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Get, $"/api/media/{item.Id}/poster")).Response.StatusCode);

        var response = await Send(api, HttpMethod.Put, $"/api/reviews/{review}/anonymity", author.Cookie, new { isAnonymous = true });
        Assert.Equal(HttpStatusCode.OK, response.Response.StatusCode);
        await PublishReview(s, review); // switching may send the review back to moderation; make it public again to prove the video stays hidden

        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, $"/api/media/{item.Id}/poster")).Response.StatusCode);
        Assert.Empty((await Send(api, HttpMethod.Get, "/api/media/feed")).Json.EnumerateArray());
        var entityId = (await ReviewRepo(s).GetAsync(review))!.EntityId;
        var list = await Send(api, HttpMethod.Get, $"/api/entities/{entityId}/reviews");
        Assert.Empty(Assert.Single(list.Json.EnumerateArray()).GetProperty("media").EnumerateArray());
    }

    [MongoFact]
    public async Task Moderators_see_the_analysis_and_decide_and_a_video_without_the_ai_check_cannot_be_approved()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var admin = await Moderator(s);
        var reader = await api.SignInAsync("+201333333333");
        var review = await NewPlaceAndReview(s, author);
        var item = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());
        api.Ai.Enqueue(Clean());
        api.Ai.Enqueue(Speech());
        await s.Pipeline.ProcessAsync((await s.Items.ClaimNextUploadedAsync())!);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Get, "/api/moderation/media")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, HttpMethod.Get, "/api/moderation/media", reader.Cookie)).Response.StatusCode);

        var queue = (await Send(api, HttpMethod.Get, "/api/moderation/media", admin.Cookie)).Json.EnumerateArray().Single();
        Assert.Equal(item.Id, queue.GetProperty("id").GetString());
        Assert.Equal("Completed", queue.GetProperty("analysisStatus").GetString());
        Assert.False(queue.TryGetProperty("userId", out _)); // moderators never see who wrote it

        // A moderator opens the file (logged), then rejects without a reason (refused) and with one.
        var file = await api.CreateClient().SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, $"/api/moderation/media/{item.Id}/file/poster.jpg", admin.Cookie!, null));
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.CreateClient().SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, $"/api/moderation/media/{item.Id}/file/upload.part", admin.Cookie!, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Post, $"/api/moderation/media/{item.Id}/reject", admin.Cookie, new { reason = "" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Post, $"/api/moderation/media/{item.Id}/reject", admin.Cookie, new { reason = "Shows a private address" })).Response.StatusCode);

        var rejected = (await s.Items.GetAsync(item.Id))!;
        Assert.Equal(MediaStatus.Rejected, rejected.Status);
        Assert.False(Directory.Exists(FolderOf(s, rejected)));
        var own = (await Send(api, HttpMethod.Get, $"/api/me/media?reviewId={review}", author.Cookie)).Json.EnumerateArray().Single();
        Assert.Equal("Shows a private address", own.GetProperty("moderatorReason").GetString()); // the author is told why

        var audit = api.Database.GetCollection<MongoDB.Bson.BsonDocument>("audit_log");
        var actions = (await audit.Find(MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty).ToListAsync()).Select(d => d["action"].AsString).ToList();
        Assert.Contains("media.viewed", actions);
        Assert.Contains("media.rejected", actions);

        // An item whose analysis never completed cannot be approved, whatever a moderator clicks.
        var stuck = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());
        var raw = (await s.Items.GetAsync(stuck.Id))!;
        raw.Status = MediaStatus.PendingModeration;
        raw.Analysis = new MediaAnalysis { Status = AnalysisStatus.Unavailable };
        await s.Items.ReplaceAsync(raw);
        var (response, json) = await Send(api, HttpMethod.Post, $"/api/moderation/media/{stuck.Id}/approve", admin.Cookie);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("analysis_required", json.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task A_video_that_is_not_protected_for_an_anonymous_author_cannot_be_approved()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var admin = await Moderator(s);
        var review = await NewPlaceAndReview(s, author, anonymous: true);
        var item = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());
        var raw = (await s.Items.GetAsync(item.Id))!;
        raw.Status = MediaStatus.PendingModeration; // as if it had been processed without protection
        raw.HasStream = true;
        raw.Analysis = new MediaAnalysis { Status = AnalysisStatus.Completed };
        raw.ProtectedForAnonymous = false;
        await s.Items.ReplaceAsync(raw);

        var (response, json) = await Send(api, HttpMethod.Post, $"/api/moderation/media/{item.Id}/approve", admin.Cookie);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_protected_for_anonymous", json.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task The_media_queue_is_counted_in_the_moderator_overview()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var admin = await Moderator(s);
        var review = await NewPlaceAndReview(s, author);
        var item = await UploadWhole(s, author.Cookie!, review, "Photo", "image/jpeg", Jpeg());
        api.Ai.Enqueue(new PhotoAnalysis(0, 0, 0, false, false, false, 0.9, []));
        await s.Pipeline.ProcessAsync((await s.Items.ClaimNextUploadedAsync())!);

        var overview = (await Send(api, HttpMethod.Get, "/api/moderation/overview", admin.Cookie)).Json;
        var media = overview.GetProperty("queues").EnumerateArray().Single(q => q.GetProperty("name").GetString() == "media");
        Assert.Equal(1, media.GetProperty("count").GetInt32());
        Assert.NotNull(item);
    }
}
