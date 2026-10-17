namespace Ratable.Modules.Ai;

/// <summary>Which class of model a call needs. Configuration maps a tier to a model, so callers never name a model.</summary>
public enum AiTier
{
    /// <summary>Cheap and fast: moderation, classification, simple checks.</summary>
    Small,
    /// <summary>More capable: summaries, proof reading, judging near-duplicates.</summary>
    Standard,
}

public enum AiStatus
{
    Completed,
    /// <summary>The daily cap was reached. The model was not called. The caller must send the item to the human queue or keep it pending; never publish it.</summary>
    Deferred,
    /// <summary>The call could not be completed (provider error, not configured, unpriced model, bad output). Treat like Deferred for publishing: never publish on this.</summary>
    Failed,
}

public sealed record AiRequest
{
    /// <summary>What the call is for, for example "moderation", "proof_check", "video_frames". Used to group spend.</summary>
    public required string Feature { get; init; }

    /// <summary>The review, comment, video or other item this call is about. An id, never content or a name.</summary>
    public required string ItemReference { get; init; }

    public required string Prompt { get; init; }
    public string? System { get; init; }
    public AiTier Tier { get; init; } = AiTier.Small;

    /// <summary>Upper limit for the answer, including any thinking the model does.</summary>
    public int MaxOutputTokens { get; init; } = 2048;

    /// <summary>When true, an identical request (same feature, model, system, prompt) is answered from the stored result at no cost.</summary>
    public bool Cacheable { get; init; }
}

public sealed record AiUsage(string Model, long InputTokens, long OutputTokens, decimal CostUsd);

public sealed record AiResult<T>(AiStatus Status, T? Value = default, AiUsage? Usage = null, string? Error = null, bool FromCache = false)
{
    public bool Completed => Status == AiStatus.Completed;
    public bool IsDeferred => Status == AiStatus.Deferred;
}

/// <summary>
/// The only way to use an AI model (CLAUDE.md rule 2). Every call is logged with model, tokens and cost, and the daily cap
/// (Ai:DailyCapUsd) is enforced here. The methods never throw for a cap or a provider problem: check <see cref="AiResult{T}.Status"/>.
/// </summary>
public interface IAiClient
{
    Task<AiResult<string>> CompleteTextAsync(AiRequest request, CancellationToken cancellationToken = default);

    /// <summary>The model is told to answer with JSON matching T; the result is parsed and checked. Invalid output is a Failed result.</summary>
    Task<AiResult<T>> CompleteStructuredAsync<T>(AiRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One model vendor behind <see cref="IAiClient"/>. Adding a vendor means adding one of these; nothing else changes.</summary>
public interface IAiProvider
{
    string Name { get; }
    Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken cancellationToken = default);
}

public sealed record AiProviderRequest(string Model, string? System, string Prompt, int MaxOutputTokens, string Effort);

public sealed record AiProviderResponse(string Text, long InputTokens, long OutputTokens, bool Truncated);

/// <summary>Thrown by a provider for any failure. The message is logged, so it must not contain request content.</summary>
public sealed class AiProviderException(string message, Exception? inner = null) : Exception(message, inner);
