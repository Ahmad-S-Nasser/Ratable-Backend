using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Api.Tests.Data;
using Ratable.Modules.Media.Models;
using Ratable.Modules.Media.Services;
using static Ratable.Api.Tests.Media.MediaKit;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Media;

public sealed class MediaUploadTests
{
    [MongoFact]
    public async Task Only_the_author_can_ask_to_upload_and_the_type_and_size_are_checked()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var other = await api.SignInAsync("+201222222222");
        var review = await NewPlaceAndReview(s, author);

        Assert.Equal(HttpStatusCode.NotFound, (await Ask(api, other.Cookie!, review, "Photo", "image/jpeg", 1000)).Response.StatusCode); // somebody else's review
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api, HttpMethod.Post, $"/api/reviews/{review}/media", null, new { kind = "Photo", contentType = "image/jpeg", sizeBytes = 10 })).Response.StatusCode);
        Assert.Equal("invalid_type", (await Ask(api, author.Cookie!, review, "Photo", "application/pdf", 1000)).Json.GetProperty("error").GetString());
        Assert.Equal("invalid_type", (await Ask(api, author.Cookie!, review, "Video", "image/jpeg", 1000)).Json.GetProperty("error").GetString());
        Assert.Equal("invalid_kind", (await Ask(api, author.Cookie!, review, "Audio", "audio/mpeg", 1000)).Json.GetProperty("error").GetString());
        Assert.Equal("invalid_size", (await Ask(api, author.Cookie!, review, "Photo", "image/jpeg", 0)).Json.GetProperty("error").GetString());
        Assert.Equal("too_large", (await Ask(api, author.Cookie!, review, "Video", "video/mp4", 100L * 1024 * 1024 + 1)).Json.GetProperty("error").GetString());
        Assert.Equal("too_large", (await Ask(api, author.Cookie!, review, "Photo", "image/jpeg", 10L * 1024 * 1024 + 1)).Json.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Created, (await Ask(api, author.Cookie!, review, "Video", "video/mp4", 100L * 1024 * 1024)).Response.StatusCode); // exactly the limit is allowed
    }

    [MongoFact]
    public async Task One_video_and_four_photos_per_review()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);

        await StartUpload(api, author.Cookie!, review, "Video", "video/mp4", 1000);
        Assert.Equal("limit_reached", (await Ask(api, author.Cookie!, review, "Video", "video/mp4", 1000)).Json.GetProperty("error").GetString());

        for (var i = 0; i < 4; i++) await StartUpload(api, author.Cookie!, review, "Photo", "image/jpeg", 1000);
        var (response, json) = await Ask(api, author.Cookie!, review, "Photo", "image/jpeg", 1000);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("limit_reached", json.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task A_file_can_be_sent_in_pieces_and_resumed_after_a_break()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);
        var file = Mp4(3000);
        var upload = await StartUpload(api, author.Cookie!, review, "Video", "video/mp4", file.Length);

        var first = await Put(api, upload.Url, file[..1000], 0, file.Length);
        Assert.Equal(HttpStatusCode.OK, first.Response.StatusCode);
        Assert.Equal(1000, first.Json.GetProperty("receivedBytes").GetInt64());
        Assert.Equal("Uploading", first.Json.GetProperty("status").GetString());

        // The connection broke: ask how much arrived, and start from there. Sending the wrong start is refused and says where to resume.
        var wrong = await Put(api, upload.Url, file[2000..], 2000, file.Length);
        Assert.Equal(HttpStatusCode.Conflict, wrong.Response.StatusCode);
        Assert.Equal(1000, wrong.Json.GetProperty("receivedBytes").GetInt64());
        var status = await Send(api, HttpMethod.Get, upload.Url);
        Assert.Equal(1000, status.Json.GetProperty("receivedBytes").GetInt64());

        Assert.Equal(HttpStatusCode.OK, (await Put(api, upload.Url, file[1000..2000], 1000, file.Length)).Response.StatusCode);
        var last = await Put(api, upload.Url, file[2000..], 2000, file.Length);
        Assert.Equal("Uploaded", last.Json.GetProperty("status").GetString());
        Assert.Equal(file.Length, new FileInfo(Path.Combine(FolderOf(s, (await s.Items.GetAsync(upload.MediaId))!), "upload.part")).Length);

        // A finished upload takes nothing more.
        Assert.Equal(HttpStatusCode.Conflict, (await Put(api, upload.Url, file[..10], 0, file.Length)).Response.StatusCode);
    }

    [MongoFact]
    public async Task A_renamed_file_is_refused_whatever_type_it_claims()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);

        var notAVideo = System.Text.Encoding.ASCII.GetBytes("MZ this is a program, not a video, but it says it is mp4");
        var upload = await StartUpload(api, author.Cookie!, review, "Video", "video/mp4", notAVideo.Length);
        var (response, json) = await Put(api, upload.Url, notAVideo, 0, notAVideo.Length);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("wrong_file_type", json.GetProperty("error").GetString());
        var item = (await s.Items.GetAsync(upload.MediaId))!;
        Assert.Equal(MediaStatus.Rejected, item.Status);
        Assert.False(Directory.Exists(FolderOf(s, item))); // nothing is kept

        // A real photo declared as a video is refused too.
        var photo = Jpeg();
        var asVideo = await StartUpload(api, author.Cookie!, review, "Video", "video/mp4", photo.Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(api, asVideo.Url, photo, 0, photo.Length)).Response.StatusCode);
    }

    [MongoFact]
    public async Task More_bytes_than_declared_are_refused()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);
        var upload = await StartUpload(api, author.Cookie!, review, "Video", "video/mp4", 1000);
        var (response, _) = await Put(api, upload.Url, Mp4(1500), 0, 1000, claimedLength: 1000); // says 1000 bytes, sends 1500
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [MongoFact]
    public async Task A_tampered_or_expired_upload_address_does_nothing()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);
        var upload = await StartUpload(api, author.Cookie!, review, "Photo", "image/jpeg", Jpeg().Length);

        var tampered = upload.Url[..^3] + "abc";
        Assert.Equal(HttpStatusCode.NotFound, (await Put(api, tampered, Jpeg(), 0, Jpeg().Length)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, "/api/media/upload/not-a-token")).Response.StatusCode);

        s.Api.Time.Advance(TimeSpan.FromHours(2)); // the link lasts an hour
        Assert.Equal(HttpStatusCode.NotFound, (await Put(api, upload.Url, Jpeg(), 0, Jpeg().Length)).Response.StatusCode);
    }

    [MongoFact]
    public async Task A_photo_loses_its_location_and_device_data_and_no_user_file_name_is_kept()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);
        var withExif = Jpeg(ExifSegment("GPSLatitude=30.0444;Make=SecretPhone"));
        Assert.Contains("SecretPhone", System.Text.Encoding.ASCII.GetString(withExif));

        var item = await UploadWhole(s, author.Cookie!, review, "Photo", "image/jpeg", withExif);

        var folder = FolderOf(s, item);
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).ToList();
        Assert.Equal(["image.jpg"], files); // the original upload is gone
        var stored = await File.ReadAllBytesAsync(Path.Combine(folder, "image.jpg"));
        Assert.DoesNotContain("SecretPhone", System.Text.Encoding.ASCII.GetString(stored));
        Assert.DoesNotContain("GPS", System.Text.Encoding.ASCII.GetString(stored));
        Assert.Matches("^[a-f0-9]{32}$", item.StorageKey); // a random key, never a name the user sent
    }

    [MongoFact]
    public async Task The_author_sees_their_media_and_can_delete_it_with_its_files()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var other = await api.SignInAsync("+201222222222");
        var review = await NewPlaceAndReview(s, author);
        var item = await UploadWhole(s, author.Cookie!, review, "Photo", "image/jpeg", Jpeg());

        var mine = await Send(api, HttpMethod.Get, $"/api/me/media?reviewId={review}", author.Cookie);
        Assert.Equal(item.Id, Assert.Single(mine.Json.EnumerateArray()).GetProperty("id").GetString());
        Assert.Empty((await Send(api, HttpMethod.Get, "/api/me/media", other.Cookie)).Json.EnumerateArray());

        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Delete, $"/api/me/media/{item.Id}", other.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(api, HttpMethod.Delete, $"/api/me/media/{item.Id}", author.Cookie)).Response.StatusCode);
        Assert.False(Directory.Exists(FolderOf(s, item)));
        Assert.Null(await s.Items.GetAsync(item.Id));
    }

    [MongoFact]
    public async Task Deleting_a_review_deletes_its_media_and_files()
    {
        using var s = NewSetup();
        await using var api = s.Api;
        var author = await api.SignInAsync("+201111111111");
        var review = await NewPlaceAndReview(s, author);
        var item = await UploadWhole(s, author.Cookie!, review, "Photo", "image/jpeg", Jpeg());

        Assert.Equal(HttpStatusCode.NoContent, (await Send(api, HttpMethod.Delete, $"/api/reviews/{review}", author.Cookie)).Response.StatusCode);

        Assert.Null(await s.Items.GetAsync(item.Id));
        Assert.False(Directory.Exists(FolderOf(s, item)));
    }

    [Fact]
    public void Upload_tokens_are_signed_and_expire()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = Microsoft.Extensions.Options.Options.Create(new MediaOptions { SigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) });
        var tokens = new UploadTokens(options, time);
        var token = tokens.Create("507f1f77bcf86cd799439011", time.GetUtcNow().UtcDateTime.AddMinutes(10));

        Assert.Equal("507f1f77bcf86cd799439011", tokens.Validate(token));
        Assert.Null(tokens.Validate(token + "x"));
        Assert.Null(tokens.Validate("a." + token.Split('.')[1])); // another payload with this signature
        Assert.Null(tokens.Validate(""));
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(tokens.Validate(token));

        var other = new UploadTokens(Microsoft.Extensions.Options.Options.Create(new MediaOptions { SigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) }), time);
        Assert.Null(other.Validate(tokens.Create("507f1f77bcf86cd799439011", time.GetUtcNow().UtcDateTime.AddMinutes(10)))); // another key
    }
}
