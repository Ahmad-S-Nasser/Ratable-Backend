namespace Ratable.Modules.Ai;

/// <summary>Bound from the "Ai" configuration section. The API key comes from the environment, never from the repository.</summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>The most the system may spend in one UTC day, in US dollars. About $100 a month is roughly 3.3.</summary>
    public decimal DailyCapUsd { get; set; } = 3.0m;

    /// <summary>"anthropic", or empty for none (every call is then Failed with provider_not_configured; nothing is ever approved by default).</summary>
    public string Provider { get; set; } = "";

    /// <summary>Anthropic API key. Set Ai__Anthropic__ApiKey (or the standard ANTHROPIC_API_KEY).</summary>
    public AnthropicOptions Anthropic { get; set; } = new();

    /// <summary>Tier to model id.</summary>
    public Dictionary<string, string> Models { get; set; } = new() { ["Small"] = "claude-haiku-5-5", ["Standard"] = "claude-sonnet-5-5" };

    /// <summary>Tier to effort (low, medium, high, max). Lower effort means less thinking and a smaller bill.</summary>
    public Dictionary<string, string> Effort { get; set; } = new() { ["Small"] = "low", ["Standard"] = "medium" };

    /// <summary>Model id to price in US dollars per million tokens. A model with no price here cannot be called, so spend is never untracked.</summary>
    public Dictionary<string, ModelPrice> Prices { get; set; } = [];

    public int CacheDays { get; set; } = 30;

    /// <summary>Hard limit on MaxOutputTokens for any call.</summary>
    public int MaxOutputTokensLimit { get; set; } = 8192;
}

public sealed class AnthropicOptions
{
    public string ApiKey { get; set; } = "";
}

public sealed class ModelPrice
{
    public decimal InputPerMillionUsd { get; set; }
    public decimal OutputPerMillionUsd { get; set; }
}
