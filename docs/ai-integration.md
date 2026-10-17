# Connecting the external AI service

The AI services are built outside this repository. The backend never talks to a model directly: every module calls `IAiClient`
(`Ratable.Shared.Ai`), which adds the daily cap (`Ai:DailyCapUsd`), the cost log (`ai_calls`), the optional cache and the Deferred path.

To connect the service, implement one interface and register it before `AddAiModule`:

```csharp
public interface IAiProvider
{
    string Name { get; }
    Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken cancellationToken = default);
}
// request:  Model, System, Prompt, MaxOutputTokens, Effort
// response: Text, InputTokens, OutputTokens, Truncated
```

- Throw `AiProviderException` for any failure; its message is logged, so it must not contain request content.
- Return real token counts. Cost is computed from `Ai:Prices` (US dollars per million tokens) keyed by the model name in the request. A model without a price is refused, so spend is never untracked. If the service bills differently, say so and we will add a cost field to the response.
- `Ai:Models` maps the tier (Small or Standard) to the model name sent to the service. The service may map or ignore it.
- Until a provider is registered, every call returns `Failed: provider_not_configured`, and callers must send the item to the human queue.
- Callers that need richer calls (images, video frames, audio) will need new request types; agree them with the module that needs them (video review, proof reading).
