namespace Ratable.Modules.Reviews.Seo;

/// <summary>
/// Telling search engines and the web app which pages changed. Off until SiteUrl and the web app's revalidation address are set.
/// Environment variables: Seo__Enabled, Seo__SiteUrl, Seo__IndexNowKey, Seo__RevalidateUrl, Seo__RevalidateSecret.
/// </summary>
public sealed class SeoOptions
{
    public bool Enabled { get; set; }

    /// <summary>The public origin, for example https://ratable.example (no trailing slash).</summary>
    public string SiteUrl { get; set; } = "";

    /// <summary>The IndexNow key. The web app serves it at /indexnow-key.txt. Empty means no IndexNow submission.</summary>
    public string IndexNowKey { get; set; } = "";

    public string IndexNowEndpoint { get; set; } = "https://api.indexnow.org/IndexNow";

    /// <summary>The web app's internal address that refreshes cached pages, for example http://localhost:3000/internal/revalidate.</summary>
    public string RevalidateUrl { get; set; } = "";

    /// <summary>Shared secret sent in the X-Revalidate-Secret header.</summary>
    public string RevalidateSecret { get; set; } = "";

    public int IntervalSeconds { get; set; } = 300;

    public string[] Languages { get; set; } = ["ar", "en"];
}
