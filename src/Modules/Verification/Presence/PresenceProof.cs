namespace Ratable.Modules.Verification.Presence;

/// <summary>
/// What the mobile app will send for presence proof (launch plan, "Location and photo as proof"): a live photo taken inside the app at the place,
/// plus the RESULT of the location check. The coordinates themselves never reach the server and are never stored.
/// </summary>
public sealed record PresenceProofRequest(
    string ReviewId,
    byte[] LivePhoto,
    /// <summary>The app took the photo with its own camera (gallery photos are blocked by the app and by device attestation).</summary>
    bool CapturedLiveInApp,
    /// <summary>The phone was within about 100 m of the place. A result only, never a position.</summary>
    bool LocationMatchesEntity,
    DateTime VisitedAt);

public sealed record PresenceProofResult(bool Accepted, string? Reason);

/// <summary>
/// Presence proof is an interface only for now: the live photo and location check are filled in by the mobile app (phase 2). The rules it must
/// implement: location alone is never enough, only with a photo; the photo is live and shows something that names the place; the same photo is not
/// reused; the review is started within <c>Verification:PresenceMaxHoursAfterVisit</c> of the visit; it earns the lighter "Visited" badge, which also
/// counts in scores; the photo is deleted after the decision like any other proof; it is a poor fit for compounds, schools and cars.
/// </summary>
public interface IPresenceProofService
{
    Task<PresenceProofResult> SubmitAsync(PresenceProofRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The default until the app exists: presence proof is not accepted, so such a review stays Unverified.</summary>
public sealed class UnavailablePresenceProofService : IPresenceProofService
{
    public Task<PresenceProofResult> SubmitAsync(PresenceProofRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PresenceProofResult(false, "not_available_yet"));
}
