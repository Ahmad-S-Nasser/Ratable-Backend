using Ratable.Modules.Verification.Data;
using Ratable.Shared.Moderation;

namespace Ratable.Modules.Verification.Services;

public sealed class ProofQueueCounter(VerificationRepository records) : IQueueCounter
{
    public string Name => "proofs";

    public async Task<long> CountAsync(CancellationToken ct = default) => (await records.ListQueueAsync(1000, ct)).Count;
}
