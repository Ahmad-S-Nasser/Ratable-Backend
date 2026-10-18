using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Api.Tests.Data;
using Ratable.Modules.Media.Models;
using Ratable.Modules.Media.Processing;
using static Ratable.Api.Tests.Media.MediaKit;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Media;

public sealed class MediaPipelineTests
{
    private static FrameAnalysis Frames(
        int faces = 0, int children = 0, int plates = 0, int? best = null, bool nudity = false, params AiRegion[] regions) =>
        new([.. regions], faces, children, plates, nudity, false, false, 0.8, best, []);

    private static SpeechAnalysis Speech(string ar = "المكان ممتاز", string en = "The place is excellent", bool abusive = false, bool personal = false) =>
        new(ar, en, abusive, personal, "ar");

    private static AiRegion Region(string kind, double start = 0, double end = 20) => new(kind, start, end, 0.3, 0.2, 0.2, 0.3);

    private static async Task<MediaItem> Run(Setup s, MediaItem item)
    {
        var claimed = await s.Items.ClaimNextUploadedAsync();
        Assert.Equal(item.Id, claimed!.Id);
        await s.Pipeline.ProcessAsync(claimed);
        return (await s.Items.GetAsync(item.Id))!;
    }

    [MongoFact]
    public async Task A_video_is_protected_analysed_captioned_and_waits_for_a_person()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author, anonymous: false);
        var item = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());

        api.Ai.Enqueue(Frames(faces: 3, children: 1, plates: 1, best: 3, regions: [Region("face"), Region("child_face"), Region("plate")]));
        api.Ai.Enqueue(Speech());
        var done = await Run(s, item);

        Assert.Equal(MediaStatus.PendingModeration, done.Status);
        Assert.Equal(AnalysisStatus.Completed, done.Analysis.Status);
        // A named author: children's faces and plates are always blurred, an adult's own face is not, and the voice is not changed.
        Assert.Equal(["child_face", "plate"], s.Processor.LastPlan!.Regions.Select(r => r.Kind).OrderBy(k => k));
        Assert.False(s.Processor.LastPlan.ChangeVoice);
        Assert.True(done.BlurApplied);
        Assert.False(done.VoiceChanged);
        Assert.Equal("المكان ممتاز", done.Captions!.Ar);
        Assert.Equal("The place is excellent", done.Captions.En);
        Assert.Equal(20, done.DurationSeconds);
        Assert.True(done.HasStream);
        // The auto cover is the AI's best frame; the original upload and working files are gone.
        var folder = FolderOf(s, done);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9, 103 }, await File.ReadAllBytesAsync(Path.Combine(folder, "poster.jpg")));
        Assert.False(File.Exists(Path.Combine(folder, "upload.part")));
        Assert.Empty(Directory.GetFiles(folder, "frame_*"));
        Assert.False(File.Exists(Path.Combine(folder, "audio.mp3")));
        // The AI was asked through IAiClient with the sampled pictures and the sound.
        Assert.Equal(["video_frames", "video_audio"], api.Ai.Requests.Select(r => r.Feature).Where(f => f.StartsWith("video")));
    }

    [MongoFact]
    public async Task For_an_anonymous_author_every_face_is_blurred_and_the_voice_is_changed()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author, anonymous: true);
        var item = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());

        api.Ai.Enqueue(Frames(faces: 2, regions: [Region("face"), Region("face", 5, 9)]));
        api.Ai.Enqueue(Speech());
        var done = await Run(s, item);

        Assert.Equal(MediaStatus.PendingModeration, done.Status);
        Assert.Equal(2, s.Processor.LastPlan!.Regions.Count(r => r.Kind == "face"));
        Assert.True(s.Processor.LastPlan.ChangeVoice);
        Assert.True(done.ProtectedForAnonymous);
        Assert.True(done.VoiceChanged);
    }

    [MongoFact]
    public async Task An_anonymous_video_whose_faces_cannot_be_placed_is_refused_and_deleted()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author, anonymous: true);
        var item = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());

        api.Ai.Enqueue(Frames(faces: 2)); // faces seen, but no regions to blur
        api.Ai.Enqueue(Speech());
        var done = await Run(s, item);

        Assert.Equal(MediaStatus.Rejected, done.Status);
        Assert.Equal("anonymous_video_not_protected", done.RejectionReason);
        Assert.False(Directory.Exists(FolderOf(s, done)));
    }

    [MongoFact]
    public async Task A_child_or_a_plate_that_cannot_be_hidden_refuses_the_video_even_for_a_named_author()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);

        var withChild = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());
        api.Ai.Enqueue(Frames(children: 1));
        api.Ai.Enqueue(Speech());
        Assert.Equal("children_cannot_be_protected", (await Run(s, withChild)).RejectionReason);
    }

    [MongoFact]
    public async Task A_video_the_ai_could_not_check_is_retried_later_and_never_reaches_a_person_or_the_public()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);
        var item = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());

        api.Ai.FailAll = true; // the daily cap, or the service is down
        var first = await Run(s, item);
        Assert.Equal(MediaStatus.Uploaded, first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.NotNull(first.NextAttemptAt);
        Assert.Null(await s.Items.ClaimNextUploadedAsync()); // not due yet

        // It keeps failing: after the last attempt the video is marked failed, never queued for a person.
        for (var i = 0; i < 5; i++)
        {
            var due = (await s.Items.GetAsync(item.Id))!;
            due.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
            await s.Items.ReplaceAsync(due);
            var claimed = await s.Items.ClaimNextUploadedAsync();
            await s.Pipeline.ProcessAsync(claimed!);
        }
        var final = (await s.Items.GetAsync(item.Id))!;
        Assert.Equal(MediaStatus.Failed, final.Status);
        Assert.Equal("analysis_unavailable", final.FailureReason);
    }

    [MongoFact]
    public async Task A_video_longer_than_a_minute_or_without_picture_is_refused()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);

        s.Processor.Probe = new(75, 1280, 720, true, true);
        var tooLong = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());
        Assert.Equal("too_long", (await Run(s, tooLong)).RejectionReason);

        s.Processor.Probe = new(30, 0, 0, false, true);
        await Send(api, HttpMethod.Delete, $"/api/me/media/{tooLong.Id}", author.Cookie); // frees the slot (a refused video does not count, but tidy anyway)
        var noPicture = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());
        Assert.Equal("not_a_video", (await Run(s, noPicture)).RejectionReason);
    }

    [MongoFact]
    public async Task Missing_ffmpeg_is_a_clear_failure_not_a_silent_skip()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);
        var item = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());
        s.Processor.Unavailable = true;

        var done = await Run(s, item);

        Assert.Equal(MediaStatus.Failed, done.Status);
        Assert.Equal("processor_unavailable", done.FailureReason);
    }

    [MongoFact]
    public async Task A_transcript_with_personal_data_or_abuse_is_not_stored_and_is_flagged()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);
        var item = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());
        api.Ai.Enqueue(Frames());
        api.Ai.Enqueue(Speech(personal: true));

        var done = await Run(s, item);

        Assert.Null(done.Captions);
        Assert.Contains("personal data in speech", done.Analysis.Concerns);
        Assert.True(done.Analysis.PersonalDataInSpeech);
    }

    [MongoFact]
    public async Task A_photo_goes_to_a_person_even_when_the_ai_is_down_but_a_photo_with_faces_cannot_be_approved()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var admin = await Moderator(s);
        var review = await NewPlaceAndReview(s, author);

        var plain = await UploadWhole(s, author.Cookie!, review, "Photo", "image/jpeg", Jpeg());
        api.Ai.FailAll = true;
        var unchecked_ = await Run(s, plain);
        Assert.Equal(MediaStatus.PendingModeration, unchecked_.Status);
        Assert.Equal(AnalysisStatus.Unavailable, unchecked_.Analysis.Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Post, $"/api/moderation/media/{plain.Id}/approve", admin.Cookie)).Response.StatusCode);

        api.Ai.FailAll = false;
        var withFace = await UploadWhole(s, author.Cookie!, review, "Photo", "image/jpeg", Jpeg());
        api.Ai.Enqueue(new PhotoAnalysis(1, 0, 0, false, false, false, 0.9, []));
        await Run(s, withFace);
        var (response, json) = await Send(api, HttpMethod.Post, $"/api/moderation/media/{withFace.Id}/approve", admin.Cookie);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("faces_or_plates_visible", json.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task The_author_can_choose_the_cover_from_the_protected_candidates()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);
        var item = await UploadWhole(s, author.Cookie!, review, "Video", "video/mp4", Mp4());
        api.Ai.Enqueue(Frames(best: 2));
        api.Ai.Enqueue(Speech());
        var done = await Run(s, item);
        var poster = Path.Combine(FolderOf(s, done), "poster.jpg");
        Assert.Equal(102, (await File.ReadAllBytesAsync(poster))[4]); // the AI's pick

        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Put, $"/api/me/media/{item.Id}/cover", author.Cookie, new { choice = "first" })).Response.StatusCode);
        Assert.Equal(100, (await File.ReadAllBytesAsync(poster))[4]);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Put, $"/api/me/media/{item.Id}/cover", author.Cookie, new { choice = "frame", frameIndex = 5 })).Response.StatusCode);
        Assert.Equal(105, (await File.ReadAllBytesAsync(poster))[4]);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Put, $"/api/me/media/{item.Id}/cover", author.Cookie, new { choice = "frame", frameIndex = 99 })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Put, $"/api/me/media/{item.Id}/cover", (await api.SignInAsync("+201222222222")).Cookie, new { choice = "first" })).Response.StatusCode);
    }
}
