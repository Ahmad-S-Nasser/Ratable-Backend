using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ratable.Modules.Ai.Providers;

/// <summary>Bound from the "RatableAi" section. The service key comes from the environment (RatableAi__ServiceKey) and is never written to a file.</summary>
public sealed class RatableAiOptions
{
    public const string Section = "RatableAi";

    public string BaseUrl { get; set; } = "";
    public string ServiceKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Accept a self-signed certificate. Honoured only in the Development environment, never in production.</summary>
    public bool AllowUntrustedCertInDev { get; set; }

    public bool Configured => Uri.TryCreate(BaseUrl, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(ServiceKey);
}

/// <summary>
/// IAiProvider for the Ratable AI microservice (POST /v1/complete, bearer token). Only the tier, system prompt, prompt, limits and attachments are sent;
/// nothing is logged from the request or the answer. Everything goes through IAiClient, which keeps the cap, the cost log and the Deferred path.
/// </summary>
public sealed class RatableAiProvider(HttpClient http, IOptions<AiOptions> ai) : IAiProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => "ratable-ai";

    public async Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
    {
        var body = new CompleteRequest(
            TierFor(request.Model),
            request.System,
            request.Prompt,
            request.MaxOutputTokens,
            request.Effort,
            request.Attachments?.Select(a => new WireAttachment(a.ContentType, Convert.ToBase64String(a.Data))).ToList());

        // One retry for a timeout or a 5xx, as the integration guide recommends. A 429 or a 4xx is final.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/complete") { Content = JsonContent.Create(body, options: Json) };
                message.Headers.TryAddWithoutValidation("X-Correlation-ID", $"req_{Guid.NewGuid():N}");
                using var response = await http.SendAsync(message, cancellationToken);

                if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new AiProviderCapReachedException();
                if ((int)response.StatusCode >= 500 && attempt == 1) continue;
                if (!response.IsSuccessStatusCode) throw new AiProviderException($"ai_service_{(int)response.StatusCode}");

                var result = await response.Content.ReadFromJsonAsync<CompleteResponse>(Json, cancellationToken)
                             ?? throw new AiProviderException("ai_service_empty");
                return new AiProviderResponse(result.Text ?? "", result.InputTokens, result.OutputTokens, result.Truncated);
            }
            catch (HttpRequestException) when (attempt == 1)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt == 1)
            {
            }
            catch (HttpRequestException ex)
            {
                throw new AiProviderException("ai_service_unreachable", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AiProviderException("ai_service_timeout", ex);
            }
            catch (JsonException ex)
            {
                throw new AiProviderException("ai_service_bad_response", ex);
            }
        }
    }

    /// <summary>The service takes the tier name ("Small" or "Standard"); our configuration maps a tier to a model name used for pricing.</summary>
    private string TierFor(string model) =>
        ai.Value.Models.FirstOrDefault(m => string.Equals(m.Value, model, StringComparison.OrdinalIgnoreCase)).Key ?? "Small";

    private sealed record WireAttachment(string ContentType, string DataBase64);

    private sealed record CompleteRequest(string Model, string? System, string Prompt, int MaxOutputTokens, string Effort, List<WireAttachment>? Attachments);

    private sealed record CompleteResponse(string? Text, long InputTokens, long OutputTokens, bool Truncated, string? ModelVersion);

    public static void Configure(HttpClient client, RatableAiOptions o)
    {
        client.BaseAddress = new Uri(o.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 120));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.ServiceKey);
    }
}
