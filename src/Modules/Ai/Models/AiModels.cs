using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Ratable.Modules.Ai.Models;

public enum AiCallStatus { Completed, Deferred, Failed, CacheHit }

/// <summary>
/// One line in the AI spend log. It holds the facts needed to audit cost and nothing from the request or the answer:
/// no prompt, no text, no names (rule 1).
/// </summary>
public sealed class AiCall
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public DateTime At { get; set; }
    public string Feature { get; set; } = "";
    public string ItemReference { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Tier { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    /// <summary>Stored as a decimal so sums are exact.</summary>
    [BsonRepresentation(BsonType.Decimal128)] public decimal CostUsd { get; set; }
    public AiCallStatus Status { get; set; }
    public long DurationMs { get; set; }
    /// <summary>A short code such as "daily_cap", "provider_error" or "invalid_output". Never request content.</summary>
    public string? Error { get; set; }
}

/// <summary>A stored answer for an identical request. Expires on its own.</summary>
public sealed class AiCacheEntry
{
    /// <summary>SHA-256 of feature, model, system and prompt.</summary>
    [BsonId] public string Key { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public sealed record SpendSummary(decimal CostUsd, int Calls, int Deferred, int Failed, int CacheHits, long InputTokens, long OutputTokens);

public sealed record FeatureSpend(string Feature, decimal CostUsd, int Calls);
