using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ratable.Modules.Ai;
using Ratable.Modules.Ai.Providers;
using Ratable.Shared.Ai;

namespace Ratable.Api.Tests.Ai;

public sealed class RatableAiProviderTests
{
    private sealed class Handler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return script[Math.Min(Seen.Count - 1, script.Length - 1)](request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static RatableAiProvider Make(Handler h)
    {
        var o = new RatableAiOptions { BaseUrl = "https://ai.example.test:11000", ServiceKey = "test-key-not-real", TimeoutSeconds = 5 };
        var client = new HttpClient(h);
        RatableAiProvider.Configure(client, o);
        var ai = new AiOptions { Models = new() { ["Small"] = "flash", ["Standard"] = "pro" } };
        return new RatableAiProvider(client, Options.Create(ai));
    }

    [Fact]
    public async Task Sends_bearer_tier_and_attachments_and_reads_tokens()
    {
        var h = new Handler(_ => Json(HttpStatusCode.OK, """{"text":"{\"ok\":true}","inputTokens":12,"outputTokens":3,"truncated":false,"modelVersion":"x"}"""));
        var r = await Make(h).CompleteAsync(new AiProviderRequest("pro", "sys", "hello", 100, "medium", [new AiAttachment("image/jpeg", [1, 2, 3])]));

        Assert.Equal("{\"ok\":true}", r.Text);
        Assert.Equal(12, r.InputTokens);
        Assert.Equal(3, r.OutputTokens);
        var (req, body) = Assert.Single(h.Seen);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("/v1/complete", req.RequestUri!.AbsolutePath);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Standard", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), doc.RootElement.GetProperty("attachments")[0].GetProperty("dataBase64").GetString());
    }

    [Fact]
    public async Task Budget_reached_is_a_cap_exception()
    {
        var h = new Handler(_ => Json((HttpStatusCode)429, """{"error":"deferred_cap_reached"}"""));
        await Assert.ThrowsAsync<AiProviderCapReachedException>(() => Make(h).CompleteAsync(new AiProviderRequest("flash", null, "p", 10, "low")));
        Assert.Single(h.Seen);
    }

    [Fact]
    public async Task Server_error_is_retried_once_and_then_fails_without_content_in_the_message()
    {
        var h = new Handler(_ => Json(HttpStatusCode.BadGateway, """{"error":"provider_error"}"""));
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Make(h).CompleteAsync(new AiProviderRequest("flash", null, "secret review text", 10, "low")));
        Assert.Equal(2, h.Seen.Count);
        Assert.Equal("ai_service_502", ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public async Task Bad_request_is_not_retried()
    {
        var h = new Handler(_ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_input"}"""));
        await Assert.ThrowsAsync<AiProviderException>(() => Make(h).CompleteAsync(new AiProviderRequest("flash", null, "p", 10, "low")));
        Assert.Single(h.Seen);
    }
}
