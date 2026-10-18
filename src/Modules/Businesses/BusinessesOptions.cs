namespace Ratable.Modules.Businesses;

/// <summary>Bound from the "Businesses" section.</summary>
public sealed class BusinessesOptions
{
    public const string Section = "Businesses";

    /// <summary>Email domains of free mail services. A claim from one is flagged for the moderator; it is not refused.</summary>
    public List<string> FreeEmailDomains { get; set; } =
        ["gmail.com", "googlemail.com", "yahoo.com", "hotmail.com", "outlook.com", "live.com", "msn.com", "icloud.com", "aol.com", "proton.me", "protonmail.com", "gmx.com", "mail.com", "yandex.com"];

    public int MaxDocumentBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxOpenClaimsPerUser { get; set; } = 3;
    public int MaxClaimsPerUserPerDay { get; set; } = 5;
    public int MinReplyLength { get; set; } = 2;
    public int MaxReplyLength { get; set; } = 1000;
    public int TrendMonths { get; set; } = 6;

    /// <summary>The plans shown on the pricing page. What a plan costs and includes is configuration; none of it may touch a score or a ranking.</summary>
    public List<PlanOptions> Plans { get; set; } =
    [
        new() { Id = "free", Features = ["claim", "reply", "dashboard", "alerts"], PriceEgpMonthly = 0 },
        new() { Id = "insights", Features = ["claim", "reply", "dashboard", "alerts", "insights", "reports"] },
        new() { Id = "contact", Features = ["claim", "reply", "dashboard", "alerts", "contact"] },
    ];
}

public sealed class PlanOptions
{
    public string Id { get; set; } = "";
    public List<string> Features { get; set; } = [];
    /// <summary>Null until the price is decided (open decision). The pricing page then says the price is coming.</summary>
    public decimal? PriceEgpMonthly { get; set; }
    public bool ShowsContact => Features.Contains("contact");
}
