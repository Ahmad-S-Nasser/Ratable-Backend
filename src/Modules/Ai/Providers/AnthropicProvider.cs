using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace Ratable.Modules.Ai.Providers;

/// <summary>The one real adapter. Uses the official Anthropic SDK; nothing else in the code base talks to Claude.</summary>
public sealed class AnthropicProvider(IOptions<AiOptions> options) : IAiProvider
{
    private readonly Lazy<AnthropicClient?> _client = new(() =>
    {
        var key = options.Value.Anthropic.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        return string.IsNullOrWhiteSpace(key) ? null : new AnthropicClient { ApiKey = key };
    });

    public string Name => "anthropic";

    public async Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
    {
        var client = _client.Value ?? throw new AiProviderException("provider_not_configured");

        Message response;
        try
        {
            response = await client.Messages.Create(new MessageCreateParams
            {
                Model = request.Model,
                MaxTokens = request.MaxOutputTokens,
                System = request.System is { Length: > 0 } system ? (MessageCreateParamsSystem)system : null,
                OutputConfig = new OutputConfig { Effort = ToEffort(request.Effort) },
                Messages = [new() { Role = Role.User, Content = request.Prompt }],
            }, cancellationToken);
        }
        catch (AnthropicApiException ex)
        {
            // Keep only the status in the message: the body can echo parts of the request.
            throw new AiProviderException($"anthropic_http_{(int)ex.StatusCode}", ex);
        }

        if (response.StopReason == "refusal") throw new AiProviderException("refusal");

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return new AiProviderResponse(text, response.Usage.InputTokens, response.Usage.OutputTokens, response.StopReason == "max_tokens");
    }

    private static Effort ToEffort(string value) => value.ToLowerInvariant() switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Low,
    };
}

/// <summary>Used when no provider is configured. Every call fails visibly instead of silently approving anything.</summary>
public sealed class NoAiProvider : IAiProvider
{
    public string Name => "none";

    public Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken cancellationToken = default) =>
        throw new AiProviderException("provider_not_configured");
}
