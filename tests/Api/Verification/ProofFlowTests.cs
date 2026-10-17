using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Verification.Data;
using Ratable.Modules.Verification.Models;
using Ratable.Modules.Verification.Services;
using Ratable.Modules.Verification.Storage;
using Ratable.Shared.Data;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Verification;

/// <summary>The evidence rules, end to end through the API on a real database, with a fake AI (it reads the document) and a predictable audit sampler.</summary>
public sealed class ProofFlowTests
{
    private const string AdminPhone = "+201000000001";

    private sealed class FixedSampler(bool audit) : IProofSampler
    {
        public bool ShouldAudit(double rate) => audit;
    }

    private sealed record World(IdentityApiFactory Api, IdentityApiFactory.SignedIn Author, IdentityApiFactory.SignedIn Admin, string EntityId, string ReviewId)
    {
        public VerificationRepository Records => Api.Services.GetRequiredService<VerificationRepository>();
        public IProofStorage Storage => Api.Services.GetRequiredService<IProofStorage>();
        public string Today => Api.Time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static async Task<World> Setup(string category = "compounds", bool sampleAudit = false, Dictionary<string, string>? settings = null)
    {
        var s = new Dictionary<string, string> { ["Identity:BootstrapAdminPhone"] = AdminPhone };
        foreach (var (k, v) in settings ?? []) s[k] = v;
        var api = new IdentityApiFactory(s, fakeAi: true, configure: services => services.AddSingleton<IProofSampler>(new FixedSampler(sampleAudit)));
        var admin = await api.SignInAsync(AdminPhone);
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var author = await api.SignInAsync("+201111111111");
        var entity = await SeedEntity(api, category);
        var review = await CreateReview(api, author.Cookie!, entity.Id, new { answers = Answers(CriterionFor(category)), text = "my review" }, state: ReviewStatus.PendingModeration);
        return new World(api, author, admin, entity.Id, review);
    }

    private static (string, object) CriterionFor(string category) => category switch
    {
        "hotels-resorts" => ("cleanliness", 4),
        "appliances" => ("reliability", 4),
        "restaurants-cafes" => ("taste", 4),
        _ => ("security", 4),
    };

    private static ProofReading Good(World w, string? date = null) => new("receipt", "مدينتي", true, true, date ?? w.Today, true, false, false, 0.95);

    private static async Task<(HttpResponseMessage Response, JsonElement Json)> Upload(
        World w, byte[]? file = null, string? proofType = "lease_contract", ProofReading? reading = null, bool declaration = true, string? retention = null, bool? consent = null,
        IdentityApiFactory.SignedIn? as_ = null)
    {
        if (reading is not null) w.Api.Ai.Enqueue(reading);
        return await UploadProof(w.Api, (as_ ?? w.Author).Cookie, w.ReviewId, file ?? Png(), proofType, declaration, retention, consent);
    }

    private static async Task<Review> ReviewOf(World w) => (await w.Api.Services.GetRequiredService<ReviewRepository>().GetAsync(w.ReviewId))!;

    private static async Task<VerificationRecord> Only(World w) => (await w.Records.ListForReviewAsync(w.ReviewId)).Single();

    // ---- the Verified rule ----

    [MongoFact]
    public async Task A_clean_proof_is_approved_automatically_the_review_becomes_verified_and_the_file_is_deleted()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var file = Png();

        var (response, json) = await Upload(w, file, reading: Good(w));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Approved", json.GetProperty("status").GetString());
        var review = await ReviewOf(w);
        Assert.Equal(VerificationLevel.Verified, review.Verification.Level);
        Assert.Equal("lease_contract", review.Verification.Method);
        Assert.NotNull(review.Verification.VerifiedAt);

        var record = await Only(w);
        Assert.Equal(VerificationResult.Approved, record.Result);
        Assert.Equal("auto", record.DecidedBy);
        Assert.Equal("lease_contract", record.ProofType);                                      // what stays for good: type, date, result, hash
        Assert.NotNull(record.DecidedAt);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(file)), record.ProofHash);
        Assert.Null(record.StorageKey);                                                         // the file is gone (rule 5)
        Assert.NotNull(record.Retention.DeletedAt);
        Assert.Empty(Directory.Exists(w.Api.ProofDirectory) ? Directory.GetFiles(w.Api.ProofDirectory, "*", SearchOption.AllDirectories) : []);
    }

    [MongoFact]
    public async Task A_verified_review_counts_in_the_score_once_it_is_published_and_not_before()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, reading: Good(w));

        var before = (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/score")).Json;
        await SetReviewState(w.Api, w.ReviewId, ReviewStatus.Published);
        var after = (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/score")).Json;

        Assert.Equal(0, before.GetProperty("verifiedReviews").GetInt32()); // verified, but still waiting for moderation
        Assert.Equal(1, after.GetProperty("verifiedReviews").GetInt32());
        Assert.Equal(4.0, after.GetProperty("overall").GetDouble());
    }

    [MongoFact]
    public async Task Proof_stays_attached_when_the_review_is_edited()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, reading: Good(w));

        await Send(w.Api, HttpMethod.Put, $"/api/reviews/{w.ReviewId}", w.Author.Cookie, new { answers = Answers(("security", 2)), text = "changed my mind" });

        Assert.Equal(VerificationLevel.Verified, (await ReviewOf(w)).Verification.Level);
    }

    [MongoFact]
    public async Task The_ai_is_asked_through_the_ai_client_with_the_document_and_the_expected_place_and_no_user_identity()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var file = Pdf();

        await Upload(w, file, reading: Good(w));

        var asked = Assert.Single(w.Api.Ai.Requests, r => r.Feature == "proof_check");
        Assert.Equal("proof_check", asked.Feature);
        Assert.Equal((await Only(w)).Id, asked.ItemReference);
        var attachment = Assert.Single(asked.Attachments!);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal(file, attachment.Data);
        Assert.Contains("مدينتي", asked.Prompt);
        Assert.Contains("Madinaty", asked.Prompt);
        Assert.Contains("lease_contract", asked.Prompt);
        Assert.DoesNotContain(w.Author.Body.GetProperty("user").GetProperty("id").GetString()!, asked.Prompt + asked.System);
        Assert.DoesNotContain("1111111111", asked.Prompt + asked.System);
    }

    // ---- the checks that send a proof to a person ----

    public static IEnumerable<object[]> BadReadings() =>
    [
        [new ProofReading("receipt", "Other Place", false, true, "2026-10-17", true, false, false, 0.95), "name_mismatch"],
        [new ProofReading("receipt", "مدينتي", true, true, "2026-10-17", false, false, false, 0.95), "illegible"],
        [new ProofReading("receipt", "مدينتي", true, true, "2026-10-17", true, true, false, 0.95), "tampering_suspected"],
        [new ProofReading("selfie", "مدينتي", true, false, "2026-10-17", true, false, false, 0.95), "wrong_document_type"],
        [new ProofReading("receipt", "مدينتي", true, true, "2026-10-17", true, false, true, 0.95), "sensitive_data_visible"],
        [new ProofReading("receipt", "مدينتي", true, true, "2026-10-17", true, false, false, 0.5), "low_confidence"],
    ];

    [MongoFact]
    public async Task Anything_the_ai_finds_wrong_sends_the_proof_to_a_person_and_the_review_stays_unverified()
    {
        foreach (var args in BadReadings())
        {
            var reading = (ProofReading)args[0];
            var reason = (string)args[1];
            var w = await Setup();
            await using var _ = w.Api;
            w.Api.Ai.Enqueue(reading with { DocumentDate = w.Today });

            var (response, json) = await UploadProof(w.Api, w.Author.Cookie, w.ReviewId, Png(), "lease_contract");

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("UnderReview", json.GetProperty("status").GetString());
            var record = await Only(w);
            Assert.Equal(VerificationResult.Flagged, record.Result);
            Assert.Contains(reason, record.Reasons);
            Assert.Equal(VerificationLevel.Unverified, (await ReviewOf(w)).Verification.Level);
            Assert.NotNull(record.StorageKey);                      // the file stays: a person needs it to decide
            Assert.True(await w.Storage.ExistsAsync(record.StorageKey));
            Assert.DoesNotContain(reason, json.GetRawText());       // the author is not told how the checks work
        }
    }

    [MongoFact]
    public async Task Dates_out_of_range_missing_or_in_the_future_are_flagged_where_the_category_has_a_limit()
    {
        var cases = new (string Category, string ProofType, string? Date, string Reason)[]
        {
            ("hotels-resorts", "booking_confirmation", "2024-01-01", "date_out_of_range"),
            ("hotels-resorts", "invoice", null, "date_missing"),
            ("hotels-resorts", "invoice", "2099-01-01", "date_in_future"),
            ("appliances", "purchase_invoice", "2020-05-05", "date_out_of_range"),
        };
        foreach (var c in cases)
        {
            var w = await Setup(c.Category);
            await using var _ = w.Api;

            await Upload(w, proofType: c.ProofType, reading: new ProofReading("receipt", "مدينتي", true, true, c.Date, true, false, false, 0.95));

            Assert.Contains(c.Reason, (await Only(w)).Reasons);
            Assert.Equal(VerificationLevel.Unverified, (await ReviewOf(w)).Verification.Level);
        }
    }

    [MongoFact]
    public async Task A_recent_hotel_booking_is_accepted_and_an_old_receipt_is_fine_where_there_is_no_limit()
    {
        var hotel = await Setup("hotels-resorts");
        await using var _1 = hotel.Api;
        var cafe = await Setup("restaurants-cafes");
        await using var _2 = cafe.Api;

        await Upload(hotel, proofType: "booking_confirmation", reading: Good(hotel, date: hotel.Api.Time.GetUtcNow().AddMonths(-3).ToString("yyyy-MM-dd")));
        await Upload(cafe, proofType: "receipt", reading: Good(cafe, date: "2022-03-03"));

        Assert.Equal(VerificationResult.Approved, (await Only(hotel)).Result);
        Assert.Equal(VerificationResult.Approved, (await Only(cafe)).Result);
    }

    [MongoFact]
    public async Task The_same_file_used_again_is_flagged_even_on_another_account()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var file = Png(7);
        await Upload(w, file, reading: Good(w));

        w.Api.Time.Advance(TimeSpan.FromMinutes(2));
        var thief = await w.Api.SignInAsync("+201222222222");
        var otherReview = await CreateReview(w.Api, thief.Cookie!, w.EntityId, state: ReviewStatus.PendingModeration);
        w.Api.Ai.Enqueue(Good(w));
        var (response, _) = await UploadProof(w.Api, thief.Cookie, otherReview, file, "lease_contract");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var second = (await w.Records.ListForReviewAsync(otherReview)).Single();
        Assert.Equal(VerificationResult.Flagged, second.Result);
        Assert.Contains("duplicate_file", second.Reasons);
        var stolen = (await w.Api.Services.GetRequiredService<ReviewRepository>().GetAsync(otherReview))!;
        Assert.Equal(VerificationLevel.Unverified, stolen.Verification.Level);
    }

    [MongoFact]
    public async Task When_the_ai_is_capped_or_down_the_proof_goes_to_a_person_it_is_never_approved_by_default()
    {
        var capped = await Setup();
        await using var _1 = capped.Api;
        capped.Api.Ai.DeferAll = true;
        await Upload(capped);
        var down = await Setup();
        await using var _2 = down.Api;
        down.Api.Ai.FailAll = true;
        await Upload(down);

        var a = await Only(capped);
        var b = await Only(down);
        Assert.Equal(VerificationResult.Flagged, a.Result);
        Assert.Equal(["ai_deferred"], a.Reasons);
        Assert.Equal(VerificationResult.Flagged, b.Result);
        Assert.Equal(["ai_unavailable"], b.Reasons);
        Assert.Equal(VerificationLevel.Unverified, (await ReviewOf(capped)).Verification.Level);
        Assert.Equal(VerificationLevel.Unverified, (await ReviewOf(down)).Verification.Level);
    }

    // ---- moderators decide ----

    [MongoFact]
    public async Task A_moderator_approves_a_flagged_proof_the_review_becomes_verified_and_the_file_is_deleted()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, reading: Good(w) with { Confidence = 0.4 });
        var id = (await Only(w)).Id;

        var (response, json) = await Send(w.Api, HttpMethod.Post, $"/api/moderation/proofs/{id}/approve", w.Admin.Cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Approved", json.GetProperty("status").GetString());
        Assert.Equal(VerificationLevel.Verified, (await ReviewOf(w)).Verification.Level);
        var record = await Only(w);
        Assert.Equal(w.Admin.Body.GetProperty("user").GetProperty("id").GetString(), record.DecidedBy);
        Assert.Null(record.StorageKey);
        var log = await w.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "proof.approved")).ToListAsync();
        Assert.Single(log);
        Assert.Equal(id, log[0]["targetId"].AsString);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/proofs/{id}/approve", w.Admin.Cookie)).Response.StatusCode);
    }

    [MongoFact]
    public async Task A_rejection_needs_a_reason_the_author_sees_it_and_the_review_stays_unverified()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, reading: Good(w) with { MatchesEntity = false });
        var id = (await Only(w)).Id;

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/proofs/{id}/reject", w.Admin.Cookie, new { reason = " " })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(w.Api, HttpMethod.Post, $"/api/moderation/proofs/{id}/reject", w.Admin.Cookie, new { reason = "The document is for a different compound" })).Response.StatusCode);

        var mine = (await Send(w.Api, HttpMethod.Get, $"/api/me/proofs?reviewId={w.ReviewId}", w.Author.Cookie)).Json.EnumerateArray().Single();
        Assert.Equal("Rejected", mine.GetProperty("status").GetString());
        Assert.Equal("The document is for a different compound", mine.GetProperty("rejectionReason").GetString());
        Assert.Equal(VerificationLevel.Unverified, (await ReviewOf(w)).Verification.Level);
        Assert.Null((await Only(w)).StorageKey);
        Assert.Single(await w.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "proof.rejected")).ToListAsync());
    }

    [MongoFact]
    public async Task After_a_rejection_the_author_can_try_again_a_limited_number_of_times_and_not_while_one_is_waiting()
    {
        var w = await Setup();
        await using var _ = w.Api;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Upload(w, Png(attempt), reading: Good(w) with { MatchesEntity = false });
            var waiting = await Upload(w, Png(100 + attempt));   // one is already waiting
            Assert.Equal(HttpStatusCode.Conflict, waiting.Response.StatusCode);
            Assert.Equal("proof_already_pending", waiting.Json.GetProperty("error").GetString());
            var current = (await w.Records.ListForReviewAsync(w.ReviewId)).Last();
            await Send(w.Api, HttpMethod.Post, $"/api/moderation/proofs/{current.Id}/reject", w.Admin.Cookie, new { reason = "Not valid" });
        }

        var fourth = await Upload(w, Png(4), reading: Good(w));

        Assert.Equal(HttpStatusCode.Conflict, fourth.Response.StatusCode);
        Assert.Equal("too_many_attempts", fourth.Json.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task Once_verified_a_review_does_not_need_or_accept_another_proof()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, reading: Good(w));

        var again = await Upload(w, Png(2), reading: Good(w));

        Assert.Equal(HttpStatusCode.Conflict, again.Response.StatusCode);
        Assert.Equal("already_verified", again.Json.GetProperty("error").GetString());
    }

    [MongoFact]
    public async Task The_moderator_queue_lists_flagged_proofs_with_reasons_and_never_the_hash_or_storage_key()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, reading: Good(w) with { TamperingSuspected = true });
        var record = await Only(w);

        var (anonymous, _) = await Send(w.Api, HttpMethod.Get, "/api/moderation/proofs");
        var (forbidden, _) = await Send(w.Api, HttpMethod.Get, "/api/moderation/proofs", w.Author.Cookie);
        var (ok, queue) = await Send(w.Api, HttpMethod.Get, "/api/moderation/proofs", w.Admin.Cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var item = queue.EnumerateArray().Single();
        Assert.Equal("flagged", item.GetProperty("kind").GetString());
        Assert.Equal(["tampering_suspected"], item.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("receipt", item.GetProperty("documentType").GetString());
        var text = queue.GetRawText();
        Assert.DoesNotContain(record.ProofHash!, text);
        Assert.DoesNotContain(record.StorageKey!, text);
        Assert.DoesNotContain(w.Author.Body.GetProperty("user").GetProperty("id").GetString()!, text);
    }

    // ---- the random audit ----

    [MongoFact]
    public async Task An_approved_proof_picked_for_audit_is_verified_at_once_but_its_file_waits_for_the_human_check()
    {
        var w = await Setup(sampleAudit: true);
        await using var _ = w.Api;

        await Upload(w, reading: Good(w));

        var record = await Only(w);
        Assert.Equal(VerificationResult.Approved, record.Result);
        Assert.True(record.AuditSampled);
        Assert.True(record.AuditPending);
        Assert.NotNull(record.StorageKey);                                               // kept until a person has looked
        Assert.Equal(VerificationLevel.Verified, (await ReviewOf(w)).Verification.Level);
        var queue = (await Send(w.Api, HttpMethod.Get, "/api/moderation/proofs", w.Admin.Cookie)).Json.EnumerateArray().Single();
        Assert.Equal("audit", queue.GetProperty("kind").GetString());
        Assert.Equal(0, await w.Api.Services.GetRequiredService<VerificationService>().PurgeDueAsync()); // the cleanup does not touch it either
    }

    [MongoFact]
    public async Task Confirming_an_audit_deletes_the_file_and_overturning_it_takes_the_verified_level_back()
    {
        var confirmed = await Setup(sampleAudit: true);
        await using var _1 = confirmed.Api;
        await Upload(confirmed, reading: Good(confirmed));
        var overturned = await Setup(sampleAudit: true);
        await using var _2 = overturned.Api;
        await Upload(overturned, reading: Good(overturned));

        var ok = await Send(confirmed.Api, HttpMethod.Post, $"/api/moderation/proofs/{(await Only(confirmed)).Id}/approve", confirmed.Admin.Cookie);
        var noReason = await Send(overturned.Api, HttpMethod.Post, $"/api/moderation/proofs/{(await Only(overturned)).Id}/reject", overturned.Admin.Cookie, new { });
        var fail = await Send(overturned.Api, HttpMethod.Post, $"/api/moderation/proofs/{(await Only(overturned)).Id}/reject", overturned.Admin.Cookie, new { reason = "The document is edited" });

        Assert.Equal(HttpStatusCode.OK, ok.Response.StatusCode);
        var c = await Only(confirmed);
        Assert.False(c.AuditPending);
        Assert.Equal(VerificationResult.Approved, c.Result);
        Assert.Null(c.StorageKey);
        Assert.Equal(VerificationLevel.Verified, (await ReviewOf(confirmed)).Verification.Level);

        Assert.Equal(HttpStatusCode.BadRequest, noReason.Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fail.Response.StatusCode);
        var o = await Only(overturned);
        Assert.Equal(VerificationResult.Rejected, o.Result);
        Assert.False(o.AuditPending);
        Assert.Equal(VerificationLevel.Unverified, (await ReviewOf(overturned)).Verification.Level);
        Assert.Single(await overturned.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "proof.audit_failed")).ToListAsync());
    }

    [MongoFact]
    public async Task A_proof_not_picked_for_audit_is_not_in_the_queue()
    {
        var w = await Setup(sampleAudit: false);
        await using var _ = w.Api;
        await Upload(w, reading: Good(w));

        Assert.Empty((await Send(w.Api, HttpMethod.Get, "/api/moderation/proofs", w.Admin.Cookie)).Json.EnumerateArray());
        Assert.False((await Only(w)).AuditSampled);
    }

    // ---- keep or delete (rule 5) ----

    [MongoFact]
    public async Task Keeping_a_proof_needs_a_separate_explicit_consent_which_is_recorded()
    {
        var w = await Setup();
        await using var _ = w.Api;

        var without = await Upload(w, retention: "keep", reading: Good(w));
        var explicitNo = await Upload(w, retention: "keep", consent: false, reading: Good(w));
        Assert.Equal("keep_consent_required", without.Json.GetProperty("error").GetString());
        Assert.Equal("keep_consent_required", explicitNo.Json.GetProperty("error").GetString());
        Assert.Empty(await w.Records.ListForReviewAsync(w.ReviewId)); // nothing was stored without consent

        var (response, json) = await Upload(w, retention: "keep", consent: true, reading: Good(w));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Keep", json.GetProperty("fileRetention").GetString());
        Assert.True(json.GetProperty("fileStored").GetBoolean());
        var record = await Only(w);
        Assert.NotNull(record.StorageKey);
        Assert.Equal(record.CreatedAt.AddMonths(12), record.Retention.ExpiresAt);
        var consent = await w.Api.Database.GetCollection<ConsentRecord>(Collections.Consents).Find(FilterDefinition<ConsentRecord>.Empty).SingleAsync();
        Assert.Equal(record.Id, consent.VerificationId);
        Assert.Equal("keep_proof", consent.Kind);
        Assert.Null(consent.WithdrawnAt);
        Assert.Equal(VerificationLevel.Verified, (await ReviewOf(w)).Verification.Level); // the choice never affects Verified
    }

    [MongoFact]
    public async Task Choosing_to_delete_and_choosing_to_keep_both_give_the_same_verified_result()
    {
        var deleted = await Setup();
        await using var _1 = deleted.Api;
        var kept = await Setup();
        await using var _2 = kept.Api;

        await Upload(deleted, retention: "delete", reading: Good(deleted));
        await Upload(kept, retention: "keep", consent: true, reading: Good(kept));

        Assert.Equal(VerificationLevel.Verified, (await ReviewOf(deleted)).Verification.Level);
        Assert.Equal(VerificationLevel.Verified, (await ReviewOf(kept)).Verification.Level);
        Assert.Null((await Only(deleted)).StorageKey);
        Assert.NotNull((await Only(kept)).StorageKey);
    }

    [MongoFact]
    public async Task A_kept_file_is_encrypted_on_disk()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var file = Png(42);

        await Upload(w, file, retention: "keep", consent: true, reading: Good(w));

        var onDisk = Directory.GetFiles(w.Api.ProofDirectory, "*", SearchOption.AllDirectories).Single();
        Assert.False((await File.ReadAllBytesAsync(onDisk)).AsSpan().IndexOf(file.AsSpan(8)) >= 0);
        Assert.Equal(file, await w.Storage.ReadAsync((await Only(w)).StorageKey!));
    }

    [MongoFact]
    public async Task A_kept_file_is_deleted_when_its_keep_period_ends_and_not_before()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, retention: "keep", consent: true, reading: Good(w));
        var service = w.Api.Services.GetRequiredService<VerificationService>();

        w.Api.Time.Advance(TimeSpan.FromDays(300));
        Assert.Equal(0, await service.PurgeDueAsync());
        Assert.NotNull((await Only(w)).StorageKey);

        w.Api.Time.Advance(TimeSpan.FromDays(100)); // past 12 months
        Assert.Equal(1, await service.PurgeDueAsync());

        var record = await Only(w);
        Assert.Null(record.StorageKey);
        Assert.NotNull(record.Retention.DeletedAt);
        Assert.Equal(VerificationLevel.Verified, (await ReviewOf(w)).Verification.Level); // the badge stands on the recorded result
        Assert.NotNull(record.ProofHash);
    }

    [MongoFact]
    public async Task The_author_can_delete_a_kept_file_at_any_time_after_the_decision_and_the_consent_is_withdrawn()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, retention: "keep", consent: true, reading: Good(w));
        var id = (await Only(w)).Id;

        var (response, _) = await Send(w.Api, HttpMethod.Delete, $"/api/me/proofs/{id}/file", w.Author.Cookie);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var record = await Only(w);
        Assert.Null(record.StorageKey);
        Assert.Equal(VerificationResult.Approved, record.Result);
        Assert.Equal(VerificationLevel.Verified, (await ReviewOf(w)).Verification.Level);
        Assert.NotNull((await w.Api.Database.GetCollection<ConsentRecord>(Collections.Consents).Find(FilterDefinition<ConsentRecord>.Empty).SingleAsync()).WithdrawnAt);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w.Api, HttpMethod.Delete, $"/api/me/proofs/{id}/file", w.Author.Cookie)).Response.StatusCode); // twice is fine
    }

    [MongoFact]
    public async Task A_file_cannot_be_deleted_by_its_author_while_a_person_still_needs_it_and_not_by_anyone_else()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, reading: Good(w) with { Legible = false });
        var id = (await Only(w)).Id;
        w.Api.Time.Advance(TimeSpan.FromMinutes(2));
        var other = await w.Api.SignInAsync("+201222222222");

        Assert.Equal(HttpStatusCode.Conflict, (await Send(w.Api, HttpMethod.Delete, $"/api/me/proofs/{id}/file", w.Author.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Delete, $"/api/me/proofs/{id}/file", other.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(w.Api, HttpMethod.Delete, $"/api/me/proofs/{id}/file")).Response.StatusCode);
        Assert.NotNull((await Only(w)).StorageKey);
    }

    [MongoFact]
    public async Task Deleting_a_review_deletes_the_proof_file_kept_for_it_but_not_the_record()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, retention: "keep", consent: true, reading: Good(w));

        Assert.Equal(HttpStatusCode.NoContent, (await Send(w.Api, HttpMethod.Delete, $"/api/reviews/{w.ReviewId}", w.Author.Cookie)).Response.StatusCode);

        var record = await Only(w);
        Assert.Null(record.StorageKey);
        Assert.Empty(Directory.GetFiles(w.Api.ProofDirectory, "*", SearchOption.AllDirectories));
        Assert.Equal("lease_contract", record.ProofType);
        Assert.NotNull(record.ProofHash);
    }

    [MongoFact]
    public async Task The_cleanup_deletes_decided_files_set_to_delete_but_never_a_file_still_being_decided()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var service = w.Api.Services.GetRequiredService<VerificationService>();
        var userId = w.Author.Body.GetProperty("user").GetProperty("id").GetString()!;
        async Task<VerificationRecord> Seed(VerificationResult result, bool auditPending)
        {
            var key = LocalDiskProofStorage.NewKey();
            await w.Storage.SaveAsync(key, Png());
            var record = new VerificationRecord { UserId = userId, Type = VerificationType.Proof, Result = result, StorageKey = key, AuditPending = auditPending, ProofType = "receipt" };
            await w.Records.InsertAsync(record);
            return record;
        }
        var decided = await Seed(VerificationResult.Approved, false);
        var rejected = await Seed(VerificationResult.Rejected, false);
        var flagged = await Seed(VerificationResult.Flagged, false);
        var pending = await Seed(VerificationResult.Pending, false);
        var audit = await Seed(VerificationResult.Approved, true);

        Assert.Equal(2, await service.PurgeDueAsync());

        Assert.Null((await w.Records.GetAsync(decided.Id))!.StorageKey);
        Assert.Null((await w.Records.GetAsync(rejected.Id))!.StorageKey);
        foreach (var r in new[] { flagged, pending, audit }) Assert.NotNull((await w.Records.GetAsync(r.Id))!.StorageKey);
    }

    // ---- the moderator opens a file ----

    [MongoFact]
    public async Task Only_a_moderator_can_open_a_proof_file_and_every_opening_is_logged()
    {
        var w = await Setup();
        await using var _ = w.Api;
        var file = Png(9);
        await Upload(w, file, reading: Good(w) with { Legible = false });
        var id = (await Only(w)).Id;
        using var http = w.Api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"/api/moderation/proofs/{id}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, $"/api/moderation/proofs/{id}/file", w.Author.Cookie!))).StatusCode); // not even the author
        var response = await http.SendAsync(IdentityApiFactory.WithCookie(HttpMethod.Get, $"/api/moderation/proofs/{id}/file", w.Admin.Cookie!));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(file, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var log = await w.Api.Database.GetCollection<BsonDocument>(Collections.AuditLog).Find(new BsonDocument("action", "proof.viewed")).ToListAsync();
        Assert.Single(log);
        Assert.Equal(w.Admin.Body.GetProperty("user").GetProperty("id").GetString(), log[0]["actor"].AsString);
    }

    [MongoFact]
    public async Task A_deleted_file_cannot_be_opened()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, reading: Good(w));
        var id = (await Only(w)).Id;

        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Api, HttpMethod.Get, $"/api/moderation/proofs/{id}/file", w.Admin.Cookie)).Response.StatusCode);
    }

    // ---- what is refused at the door ----

    [MongoFact]
    public async Task Bad_uploads_are_refused_and_nothing_is_stored()
    {
        var w = await Setup(settings: new() { ["Verification:MaxFileBytes"] = "200" });
        await using var _ = w.Api;
        var windowsProgram = System.Text.Encoding.ASCII.GetBytes("MZ this is a windows program renamed to proof.pdf and long enough");
        var tooBig = Png(); Array.Resize(ref tooBig, 300);
        var cases = new (byte[]? File, string? Type, bool Declaration, string Error)[]
        {
            (null, "lease_contract", true, "invalid_file"),
            ([], "lease_contract", true, "invalid_file"),
            (windowsProgram, "lease_contract", true, "invalid_file"),
            (tooBig, "lease_contract", true, "invalid_file"),
            (Png(), "receipt", true, "invalid_proof_type"),            // not accepted for compounds
            (Png(), null, true, "invalid_proof_type"),
            (Png(), "photo_of_visit", true, "use_app_for_presence_proof"),
            (Png(), "lease_contract", false, "declaration_required"),
        };

        foreach (var c in cases)
        {
            var (response, json) = await UploadProof(w.Api, w.Author.Cookie, w.ReviewId, c.File, c.Type, c.Declaration, fileName: "proof.pdf");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(c.Error, json.GetProperty("error").GetString());
        }
        Assert.Empty(await w.Records.ListForReviewAsync(w.ReviewId));
        Assert.DoesNotContain(w.Api.Ai.Requests, r => r.Feature == "proof_check"); // the review itself was pre-screened, the refused proof never reached the AI
        Assert.False(Directory.Exists(w.Api.ProofDirectory) && Directory.GetFiles(w.Api.ProofDirectory, "*", SearchOption.AllDirectories).Length > 0);
    }

    [MongoFact]
    public async Task Only_the_author_of_an_existing_review_can_upload_a_proof_for_it()
    {
        var w = await Setup();
        await using var _ = w.Api;
        w.Api.Time.Advance(TimeSpan.FromMinutes(2));
        var other = await w.Api.SignInAsync("+201222222222");
        await SetReviewState(w.Api, w.ReviewId, ReviewStatus.Deleted);

        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadProof(w.Api, null, w.ReviewId, Png())).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(w, as_: other)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(w)).Response.StatusCode); // the review is deleted
        Assert.Equal(HttpStatusCode.NotFound, (await UploadProof(w.Api, w.Author.Cookie, "not-an-id", Png())).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadProof(w.Api, w.Author.Cookie, NewId(), Png())).Response.StatusCode);
    }

    [MongoFact]
    public async Task A_user_can_only_upload_so_many_proofs_a_day()
    {
        var w = await Setup(settings: new() { ["Verification:MaxUploadsPerDay"] = "2" });
        await using var _ = w.Api;
        var reviews = new List<string> { w.ReviewId };
        for (var i = 0; i < 2; i++) reviews.Add(await CreateReview(w.Api, w.Author.Cookie!, (await SeedEntity(w.Api)).Id, state: ReviewStatus.PendingModeration));

        var results = new List<HttpStatusCode>();
        foreach (var review in reviews)
        {
            w.Api.Ai.Enqueue(Good(w));
            results.Add((await UploadProof(w.Api, w.Author.Cookie, review, Png(results.Count + 1), "lease_contract")).Response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests], results);
    }

    // ---- nobody but moderators sees proofs ----

    [MongoFact]
    public async Task The_authors_own_view_never_shows_the_hash_the_storage_key_or_the_automatic_reasons()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, reading: Good(w) with { Legible = false });
        var record = await Only(w);

        var text = (await Send(w.Api, HttpMethod.Get, "/api/me/proofs", w.Author.Cookie)).Json.GetRawText();

        Assert.DoesNotContain(record.ProofHash!, text);
        Assert.DoesNotContain(record.StorageKey!, text);
        Assert.DoesNotContain("illegible", text);
        Assert.Contains("UnderReview", text);
    }

    [MongoFact]
    public async Task No_public_endpoint_shows_anything_about_a_proof()
    {
        var w = await Setup();
        await using var _ = w.Api;
        await Upload(w, reading: Good(w));
        await SetReviewState(w.Api, w.ReviewId, ReviewStatus.Published);
        var record = await Only(w);
        var urls = new[]
        {
            $"/api/entities/{w.EntityId}", $"/api/entities/{w.EntityId}/reviews", $"/api/entities/{w.EntityId}/score",
            $"/api/reviews/{w.ReviewId}/history", $"/api/reviews/{w.ReviewId}/comments", "/openapi/v1.json",
        };

        foreach (var url in urls)
        {
            var text = await (await Send(w.Api, HttpMethod.Get, url)).Response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(record.ProofHash!, text);
            Assert.DoesNotContain(record.Id, text);
        }
        var review = (await Send(w.Api, HttpMethod.Get, $"/api/entities/{w.EntityId}/reviews")).Json.EnumerateArray().Single();
        Assert.True(review.GetProperty("verified").GetBoolean()); // only the fact that it is verified is public
        Assert.DoesNotContain("lease_contract", review.GetRawText());
    }

    // ---- presence proof is an interface only ----

    [Fact]
    public async Task Presence_proof_is_not_available_yet_so_such_a_review_stays_unverified()
    {
        var presence = new Ratable.Modules.Verification.Presence.UnavailablePresenceProofService();

        var result = await presence.SubmitAsync(new Ratable.Modules.Verification.Presence.PresenceProofRequest(NewId(), Png(), true, true, DateTime.UtcNow));

        Assert.False(result.Accepted);
        Assert.Equal("not_available_yet", result.Reason);
    }
}
