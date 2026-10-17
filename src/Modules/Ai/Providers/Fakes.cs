using Ratable.Shared.Ai;
using System.Collections.Concurrent;

namespace Ratable.Modules.Ai.Providers;

/// <summary>
/// A scripted provider for tests of <see cref="AiClient"/> itself (cap, cost, cache, logging). It never leaves the process.
/// </summary>
public sealed class FakeAiProvider : IAiProvider
{
    private readonly ConcurrentQueue<AiProviderResponse> _script = new();
    private readonly ConcurrentQueue<AiProviderRequest> _requests = new();

    public string Name => "fake";

    /// <summary>Used when the script is empty.</summary>
    public AiProviderResponse Default { get; set; } = new("ok", 1000, 200, false);

    /// <summary>When set, every call throws it.</summary>
    public Exception? Throw { get; set; }

    /// <summary>Makes each call take this long, so tests can overlap calls.</summary>
    public TimeSpan Delay { get; set; }

    public int Calls => _requests.Count;
    public IReadOnlyCollection<AiProviderRequest> Requests => _requests.ToArray();

    public FakeAiProvider Enqueue(AiProviderResponse response)
    {
        _script.Enqueue(response);
        return this;
    }

    public async Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
    {
        _requests.Enqueue(request);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (Throw is not null) throw Throw;
        return _script.TryDequeue(out var next) ? next : Default;
    }
}

/// <summary>
/// The fake <see cref="IAiClient"/> for every other module's tests. No test may call a real model.
/// Script answers with <see cref="Enqueue"/> or force a cap with <see cref="DeferAll"/>; see what was asked with <see cref="Requests"/>.
/// </summary>
public sealed class FakeAiClient : IAiClient
{
    private readonly ConcurrentQueue<object> _script = new();
    private readonly ConcurrentQueue<AiRequest> _requests = new();

    public IReadOnlyCollection<AiRequest> Requests => _requests.ToArray();

    /// <summary>When true every call returns Deferred, as if the daily cap was reached. The "model" is not called.</summary>
    public bool DeferAll { get; set; }

    /// <summary>When true every call returns Failed.</summary>
    public bool FailAll { get; set; }

    public FakeAiClient Enqueue<T>(T value)
    {
        _script.Enqueue(value!);
        return this;
    }

    public Task<AiResult<string>> CompleteTextAsync(AiRequest request, CancellationToken cancellationToken = default) => Answer<string>(request, "fake answer");

    public Task<AiResult<T>> CompleteStructuredAsync<T>(AiRequest request, CancellationToken cancellationToken = default) => Answer<T>(request, default);

    private Task<AiResult<T>> Answer<T>(AiRequest request, T? fallback)
    {
        _requests.Enqueue(request);
        if (DeferAll) return Task.FromResult(new AiResult<T>(AiStatus.Deferred, Error: "daily_cap"));
        if (FailAll) return Task.FromResult(new AiResult<T>(AiStatus.Failed, Error: "provider_error"));
        var value = _script.TryDequeue(out var next) && next is T typed ? typed : fallback;
        return Task.FromResult(new AiResult<T>(AiStatus.Completed, value, new AiUsage("fake", 0, 0, 0m)));
    }
}
