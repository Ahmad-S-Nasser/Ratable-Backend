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

    /// <summary>A contact request stays open this long, then closes and offers other nearby providers.</summary>
    public int RequestLifeDays { get; set; } = 7;
    public int MaxRequestsPerReaderPerDay { get; set; } = 5;
    public int MaxRequestsPerProviderPerDay { get; set; } = 30;
    public int MaxRequestTextLength { get; set; } = 300;

    /// <summary>An upgrade message of one kind is made at most once in this many days.</summary>
    public int NudgeIntervalDays { get; set; } = 7;

    /// <summary>Currencies the pricing page can show, in the order of the selector. Egypt first, then the Gulf.</summary>
    public List<string> Currencies { get; set; } = ["EGP", "SAR", "AED"];

    /// <summary>Paying for a year at once costs this much less than twelve months.</summary>
    public int AnnualDiscountPercent { get; set; } = 20;

    /// <summary>
    /// How many Egyptian pounds one Saudi riyal is worth, for showing the Egyptian price. Kashier converts from US dollars to pounds when it charges, so this rate only decides
    /// the price shown and must be reviewed when the pound moves. It is configuration, not a live rate.
    /// </summary>
    public decimal EgpPerSar { get; set; } = 14m;

    /// <summary>The Egyptian price is rounded to the nearest multiple of this, so it reads as a price.</summary>
    public int EgpRounding { get; set; } = 10;

    /// <summary>
    /// The plans shown on the pricing page. The base price is in Saudi riyals; the dirham price is the same figure, and the pound price is that amount converted at
    /// <see cref="EgpPerSar"/>. What a plan costs and includes is configuration; none of it may touch a score or a ranking. The figures are for the owner of the product to confirm.
    /// </summary>
    public List<PlanOptions> Plans { get; set; } =
    [
        new() { Id = "free", Features = ["claim", "reply", "dashboard", "alerts"], MonthlyPriceSar = 0 },
        new() { Id = "insights", Features = ["claim", "reply", "dashboard", "alerts", "insights", "reports"], MonthlyPriceSar = 99 },
        new() { Id = "contact", Features = ["claim", "reply", "dashboard", "alerts", "contact"], MonthlyPriceSar = 29 },
    ];

    /// <summary>The monthly price of a plan in each shown currency.</summary>
    public Dictionary<string, decimal> PricesOf(PlanOptions plan)
    {
        var prices = new Dictionary<string, decimal> { ["SAR"] = plan.MonthlyPriceSar, ["AED"] = plan.MonthlyPriceSar };
        var step = Math.Max(1, EgpRounding);
        prices["EGP"] = plan.MonthlyPriceSar == 0 ? 0 : Math.Round(plan.MonthlyPriceSar * EgpPerSar / step, MidpointRounding.AwayFromZero) * step;
        foreach (var (currency, amount) in plan.PriceOverrides) prices[currency] = amount;   // an explicit price wins
        return prices;
    }
}

public sealed class PlanOptions
{
    public string Id { get; set; } = "";
    public List<string> Features { get; set; } = [];

    /// <summary>The monthly price in Saudi riyals, before VAT.</summary>
    public decimal MonthlyPriceSar { get; set; }

    /// <summary>A fixed price for a currency, when it should not follow the conversion.</summary>
    public Dictionary<string, decimal> PriceOverrides { get; set; } = [];

    public bool ShowsContact => Features.Contains("contact");
}
