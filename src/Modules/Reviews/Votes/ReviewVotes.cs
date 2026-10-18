using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Shared.Auth;
using Ratable.Shared.Data;
using Ratable.Shared.Rewards;

namespace Ratable.Modules.Reviews.Votes;

/// <summary>One person found one review helpful. The voter is stored so a person cannot vote twice, but is never shown to anyone, not even the author.</summary>
public sealed class ReviewVote
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonRepresentation(BsonType.ObjectId)] public string ReviewId { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string UserId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ReviewVoteRepository(IMongoDatabase database) : MongoRepository<ReviewVote>(database, Collections.ReviewVotes)
{
    public async Task<ReviewVote?> GetAsync(string reviewId, string userId, CancellationToken ct = default) =>
        await Collection.Find(v => v.ReviewId == reviewId && v.UserId == userId).FirstOrDefaultAsync(ct);

    /// <summary>Adds the vote. False when this person already voted (the unique index decides, so two quick taps count once).</summary>
    public async Task<ReviewVote?> TryAddAsync(string reviewId, string userId, CancellationToken ct = default)
    {
        var vote = new ReviewVote { ReviewId = reviewId, UserId = userId };
        try
        {
            await Collection.InsertOneAsync(vote, cancellationToken: ct);
            return vote;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return null;
        }
    }

    public Task RemoveAsync(string id, CancellationToken ct = default) => Collection.DeleteOneAsync(v => v.Id == id, ct);

    public async Task<Dictionary<string, int>> CountsAsync(IReadOnlyCollection<string> reviewIds, CancellationToken ct = default)
    {
        if (reviewIds.Count == 0) return [];
        var groups = await Collection.Aggregate().Match(v => reviewIds.Contains(v.ReviewId)).Group(v => v.ReviewId, g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return groups.ToDictionary(g => g.Key, g => g.Count);
    }

    public async Task<List<string>> VotedAsync(string userId, IReadOnlyCollection<string> reviewIds, CancellationToken ct = default) =>
        await Collection.Find(v => v.UserId == userId && reviewIds.Contains(v.ReviewId)).Project(v => v.ReviewId).ToListAsync(ct);

    public Task DeleteForReviewAsync(string reviewId, CancellationToken ct = default) => Collection.DeleteManyAsync(v => v.ReviewId == reviewId, ct);
}

public sealed class ReviewVoteIndexInitializer : IIndexInitializer
{
    public async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var c = database.GetCollection<ReviewVote>(Collections.ReviewVotes);
        var k = Builders<ReviewVote>.IndexKeys;
        await c.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<ReviewVote>(k.Ascending(v => v.ReviewId).Ascending(v => v.UserId), new CreateIndexOptions { Name = "one_vote_each", Unique = true }),
            new CreateIndexModel<ReviewVote>(k.Ascending(v => v.UserId), new CreateIndexOptions { Name = "user" }),
        ], ct);
    }
}

public sealed record HelpfulState(string ReviewId, bool Voted, int Count);
public sealed record ErrorBody(string Error);

public static class ReviewVoteEndpoints
{
    public static IEndpointRouteBuilder MapReviewVoteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/reviews/{id}/helpful", Add).WithTags("Reviews").RequireAuthorization(PolicyNames.SignedIn).Produces<HelpfulState>().Produces<ErrorBody>(400).Produces(404);
        app.MapDelete("/api/reviews/{id}/helpful", Remove).WithTags("Reviews").RequireAuthorization(PolicyNames.SignedIn).Produces<HelpfulState>().Produces(404);
        app.MapGet("/api/me/helpful", Mine).WithTags("Reviews").RequireAuthorization(PolicyNames.SignedIn).Produces<List<string>>();
        return app;
    }

    private static string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>"Helpful" earns the author 2 points (up to 20 a day). You cannot mark your own review, and a review that is not public cannot be voted on.</summary>
    private static async Task<IResult> Add(string id, ClaimsPrincipal user, ReviewRepository reviews, ReviewVoteRepository votes, IEnumerable<IContributionRecorder> recorders, CancellationToken ct)
    {
        var review = await reviews.GetAsync(id, ct);
        if (review is not { Status: ReviewStatus.Published }) return Results.NotFound();
        var voter = UserId(user);
        if (review.UserId == voter) return Results.BadRequest(new ErrorBody("own_review"));

        var vote = await votes.TryAddAsync(review.Id, voter, ct);
        if (vote is not null)
            foreach (var recorder in recorders)
                await recorder.RecordAsync(review.UserId, ContributionActions.HelpfulVote, vote.Id, review.Id, review.EntityId, cancellationToken: ct);
        return Results.Ok(new HelpfulState(review.Id, true, (await votes.CountsAsync([review.Id], ct)).GetValueOrDefault(review.Id)));
    }

    private static async Task<IResult> Remove(string id, ClaimsPrincipal user, ReviewRepository reviews, ReviewVoteRepository votes, IEnumerable<IContributionRecorder> recorders, CancellationToken ct)
    {
        var review = await reviews.GetAsync(id, ct);
        if (review is null) return Results.NotFound();
        var vote = await votes.GetAsync(review.Id, UserId(user), ct);
        if (vote is not null)
        {
            await votes.RemoveAsync(vote.Id, ct);
            foreach (var recorder in recorders) await recorder.ReverseReferenceAsync(vote.Id, "vote_removed", ct);
        }
        return Results.Ok(new HelpfulState(review.Id, false, (await votes.CountsAsync([review.Id], ct)).GetValueOrDefault(review.Id)));
    }

    /// <summary>Which of these reviews the signed-in person marked helpful (so the buttons show the right state). Their own votes only.</summary>
    private static async Task<IResult> Mine(string? reviewIds, ClaimsPrincipal user, ReviewVoteRepository votes, CancellationToken ct)
    {
        var ids = (reviewIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(i => ObjectId.TryParse(i, out _)).Take(60).ToList();
        return Results.Ok(await votes.VotedAsync(UserId(user), ids, ct));
    }
}
