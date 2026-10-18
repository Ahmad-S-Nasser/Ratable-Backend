using Ratable.Shared.Ai;

namespace Ratable.Modules.Ai;

/// <summary>
/// The seam for the external AI service (built outside this repository). Implement this one interface, register it in DI before AddAiModule,
/// and <see cref="AiClient"/> adds the daily cap, cost log, cache and Deferred handling around it. See docs/ai-integration.md.
/// </summary>
public interface IAiProvider
{
    string Name { get; }
    Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken cancellationToken = default);
}

public sealed record AiProviderRequest(string Model, string? System, string Prompt, int MaxOutputTokens, string Effort, IReadOnlyList<AiAttachment>? Attachments = null);

public sealed record AiProviderResponse(string Text, long InputTokens, long OutputTokens, bool Truncated);

/// <summary>Thrown by a provider for any failure. The message is logged, so it must not contain request content.</summary>
public class AiProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The AI service says its daily budget is used up (HTTP 429 deferred_cap_reached). AiClient turns this into a Deferred result.</summary>
public sealed class AiProviderCapReachedException() : AiProviderException("deferred_cap_reached");
