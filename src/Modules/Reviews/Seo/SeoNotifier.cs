using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ratable.Modules.Reviews.Seo;

/// <summary>
/// Every few minutes, finds the places whose page changed and (1) asks the web app to refresh them, (2) submits their addresses to IndexNow.
/// The time of the last successful run only moves forward after both steps worked, so a failure is retried on the next tick.
/// Nothing personal is sent: only page addresses.
/// </summary>
public sealed class SeoNotifier(
    PlaceChangeFeed feed, IOptions<SeoOptions> options, IHttpClientFactory httpFactory, TimeProvider time, ILogger<SeoNotifier> logger) : BackgroundService
{
    private DateTime _since = time.GetUtcNow().UtcDateTime;
    private SeoOptions Opt => options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Opt.Enabled)
        {
            logger.LogInformation("SEO notifier is off (Seo:Enabled is false)");
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(30, Opt.IntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "SEO notifier run failed; it will try again"); }
        }
    }

    /// <summary>One tick. Returns the addresses that were announced.</summary>
    public async Task<IReadOnlyList<string>> RunOnceAsync(CancellationToken ct = default)
    {
        var started = time.GetUtcNow().UtcDateTime;
        var changed = await feed.GetChangedAsync(_since, ct: ct);
        if (changed.Count == 0)
        {
            _since = started;
            return [];
        }

        var paths = changed
            .SelectMany(p => Opt.Languages.Select(l => PlaceChangeFeed.PagePath(l, p)))
            .Concat(changed.Select(p => p.CategoryId).Distinct().SelectMany(c => Opt.Languages.SelectMany(l => new[] { $"/{l}/{c}", $"/{l}/{c}/best" })))
            .Distinct().ToList();

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);

        if (Opt.RevalidateUrl.Length > 0)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Opt.RevalidateUrl) { Content = JsonContent.Create(new { paths }) };
            request.Headers.Add("X-Revalidate-Secret", Opt.RevalidateSecret);
            (await http.SendAsync(request, ct)).EnsureSuccessStatusCode();
        }

        if (Opt.IndexNowKey.Length > 0 && Opt.SiteUrl.Length > 0)
        {
            var body = new
            {
                host = new Uri(Opt.SiteUrl).Host,
                key = Opt.IndexNowKey,
                keyLocation = $"{Opt.SiteUrl}/indexnow-key.txt",
                urlList = paths.Select(p => Opt.SiteUrl + p).ToArray(),
            };
            var response = await http.PostAsJsonAsync(Opt.IndexNowEndpoint, body, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            response.EnsureSuccessStatusCode();
        }

        _since = started;
        logger.LogInformation("SEO notifier announced {Count} changed addresses", paths.Count);
        return paths;
    }
}
