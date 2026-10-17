namespace Ratable.Modules.Ai.Providers;

/// <summary>Used until the external AI service is registered. Every call fails visibly instead of silently approving anything.</summary>
public sealed class NoAiProvider : IAiProvider
{
    public string Name => "none";

    public Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken cancellationToken = default) =>
        throw new AiProviderException("provider_not_configured");
}
