using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Reviews.Seo;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Reviews;

public sealed class SeoNotifierTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Url, string Body, string? Secret)> Calls { get; } = [];
        public bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.RequestUri!.ToString(), body, request.Headers.TryGetValues("X-Revalidate-Secret", out var v) ? v.Single() : null));
            return new HttpResponseMessage(Fail ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        }
    }

    private sealed class Factory(Recorder handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static IdentityApiFactory Api(Recorder recorder) => new(
        new Dictionary<string, string>
        {
            ["Seo:SiteUrl"] = "https://ratable.test",
            ["Seo:IndexNowKey"] = "abc123key",
            ["Seo:RevalidateUrl"] = "http://web.test/internal/revalidate",
            ["Seo:RevalidateSecret"] = "s3cret",
        },
        configure: services => services.AddSingleton<IHttpClientFactory>(new Factory(recorder)));

    [MongoFact]
    public async Task A_changed_place_is_announced_to_the_web_app_and_to_IndexNow_once()
    {
        var recorder = new Recorder();
        await using var api = Api(recorder);
        var notifier = api.Services.GetRequiredService<SeoNotifier>();
        // The tests run on a fake clock; the stored documents carry real times, so the clock is moved to match.
        var clock = (Microsoft.Extensions.Time.Testing.FakeTimeProvider)api.Services.GetRequiredService<TimeProvider>();
        clock.SetUtcNow(DateTimeOffset.UtcNow);
        Assert.Empty(await notifier.RunOnceAsync()); // nothing changed since the start

        var entity = await SeedEntity(api);
        await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(new Review
        {
            EntityId = entity.Id,
            UserId = ObjectId.GenerateNewId().ToString(),
            CategoryId = "compounds",
            Status = ReviewStatus.Published,
            Verification = new ReviewVerification { Level = VerificationLevel.Verified },
            Answers = [new ReviewAnswer { CriterionId = "security", Score = 4 }],
        });

        clock.SetUtcNow(DateTimeOffset.UtcNow.AddMinutes(1));
        var paths = await notifier.RunOnceAsync();

        Assert.Contains(paths, p => p.StartsWith($"/en/compounds/{entity.Id}-"));
        Assert.Contains(paths, p => p.StartsWith($"/ar/compounds/{entity.Id}-"));
        Assert.Contains("/en/compounds", paths);
        Assert.Contains("/ar/compounds/best", paths);
        Assert.Equal(2, recorder.Calls.Count);
        var revalidate = recorder.Calls[0];
        Assert.Equal("http://web.test/internal/revalidate", revalidate.Url);
        Assert.Equal("s3cret", revalidate.Secret);
        var indexNow = JsonDocument.Parse(recorder.Calls[1].Body).RootElement;
        Assert.Equal("ratable.test", indexNow.GetProperty("host").GetString());
        Assert.Equal("abc123key", indexNow.GetProperty("key").GetString());
        Assert.Equal("https://ratable.test/indexnow-key.txt", indexNow.GetProperty("keyLocation").GetString());
        Assert.All(indexNow.GetProperty("urlList").EnumerateArray(), u => Assert.StartsWith("https://ratable.test/", u.GetString()));

        // The same change is not announced again.
        clock.SetUtcNow(DateTimeOffset.UtcNow.AddMinutes(2));
        recorder.Calls.Clear();
        Assert.Empty(await notifier.RunOnceAsync());
        Assert.Empty(recorder.Calls);
    }

    [MongoFact]
    public async Task A_failed_announcement_is_retried_on_the_next_run()
    {
        var recorder = new Recorder();
        await using var api = Api(recorder);
        var notifier = api.Services.GetRequiredService<SeoNotifier>();
        await SeedEntity(api);

        recorder.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => notifier.RunOnceAsync());

        recorder.Fail = false;
        Assert.NotEmpty(await notifier.RunOnceAsync());
    }

    [Fact]
    public void The_page_address_uses_the_stable_id_and_falls_back_to_the_other_languages_slug()
    {
        var place = new ChangedPlace("507f1f77bcf86cd799439011", "hotels-resorts", new Dictionary<string, string> { ["en"] = "nile-view" });
        Assert.Equal("/en/hotels-resorts/507f1f77bcf86cd799439011-nile-view", PlaceChangeFeed.PagePath("en", place));
        Assert.Equal("/ar/hotels-resorts/507f1f77bcf86cd799439011-nile-view", PlaceChangeFeed.PagePath("ar", place));
        Assert.Equal("/ar/x/1-place", PlaceChangeFeed.PagePath("ar", new ChangedPlace("1", "x", new Dictionary<string, string>())));
    }
}
