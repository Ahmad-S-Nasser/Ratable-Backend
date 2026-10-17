using MongoDB.Driver;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Data;
using Ratable.Shared.Verification;

namespace Ratable.Modules.Reviews.Data;

/// <summary>Sets or clears the Verified level on a review. It changes only the verification fields: not the status, not the revision, not the text.</summary>
public sealed class ReviewVerifier(IMongoDatabase database) : IReviewVerifier
{
    private readonly IMongoCollection<Review> _reviews = database.GetCollection<Review>(Collections.Reviews);

    public async Task<bool> SetVerifiedAsync(string reviewId, string method, DateTime verifiedAt, CancellationToken ct = default)
    {
        if (!MongoDB.Bson.ObjectId.TryParse(reviewId, out _)) return false;
        var result = await _reviews.UpdateOneAsync(r => r.Id == reviewId,
            Builders<Review>.Update.Set(r => r.Verification, new ReviewVerification { Level = VerificationLevel.Verified, Method = method, VerifiedAt = verifiedAt }), cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<bool> SetUnverifiedAsync(string reviewId, CancellationToken ct = default)
    {
        if (!MongoDB.Bson.ObjectId.TryParse(reviewId, out _)) return false;
        var result = await _reviews.UpdateOneAsync(r => r.Id == reviewId,
            Builders<Review>.Update.Set(r => r.Verification, new ReviewVerification { Level = VerificationLevel.Unverified }), cancellationToken: ct);
        return result.MatchedCount == 1;
    }
}
