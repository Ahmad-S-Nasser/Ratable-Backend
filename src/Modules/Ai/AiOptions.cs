namespace Ratable.Modules.Ai;

/// <summary>Bound from the "Ai" configuration section. Credentials for the external AI service are its own concern and come from the environment.</summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>The most the system may spend in one UTC day, in US dollars. About $100 a month is roughly 3.3.</summary>
    public decimal DailyCapUsd { get; set; } = 3.0m;

    /// <summary>Tier to the model name sent to the external service (it may map or ignore it).</summary>
    public Dictionary<string, string> Models { get; set; } = new() { ["Small"] = "gemini-2.5-flash", ["Standard"] = "gemini-2.5-pro" };

    /// <summary>Tier to effort (low, medium, high, max). Lower effort means less thinking and a smaller bill.</summary>
    public Dictionary<string, string> Effort { get; set; } = new() { ["Small"] = "low", ["Standard"] = "medium" };

    /// <summary>Model id to price in US dollars per million tokens. A model with no price here cannot be called, so spend is never untracked.</summary>
    public Dictionary<string, ModelPrice> Prices { get; set; } = [];

    public int CacheDays { get; set; } = 30;

    /// <summary>Hard limit on MaxOutputTokens for any call.</summary>
    public int MaxOutputTokensLimit { get; set; } = 8192;
}

public sealed class ModelPrice
{
    public decimal InputPerMillionUsd { get; set; }
    public decimal OutputPerMillionUsd { get; set; }
}
