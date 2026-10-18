using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Modules.Contributions.Data;
using Ratable.Modules.Contributions.Models;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;
using Ratable.Modules.Identity.Data;
using Ratable.Modules.Identity.Models;
using Ratable.Modules.Moderation.Data;
using Ratable.Modules.Moderation.Models;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using Ratable.Modules.Verification.Data;
using Ratable.Modules.Verification.Models;
using Ratable.Shared.Data;

namespace Ratable.Api.Tests.Data;

[Collection(MongoCollection.Name)]
public sealed class IndexRuleTests(MongoFixture fixture)
{
    private IMongoDatabase Db => fixture.Database;
    private static string NewId() => ObjectId.GenerateNewId().ToString();

    private static async Task AssertDuplicateAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<MongoWriteException>(action);
        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError.Category);
    }

    // ---- Rule 4: one review per account per entity, every edit is a dated revision ----

    [MongoFact]
    public async Task A_user_cannot_review_the_same_entity_twice()
    {
        var repo = new ReviewRepository(Db);
        string entity = NewId(), user = NewId();
        await repo.InsertAsync(new Review { EntityId = entity, UserId = user, CategoryId = "compounds" });

        await AssertDuplicateAsync(() => repo.InsertAsync(new Review { EntityId = entity, UserId = user, CategoryId = "compounds" }));
    }

    [MongoFact]
    public async Task A_deleted_review_still_blocks_a_second_one()
    {
        var repo = new ReviewRepository(Db);
        string entity = NewId(), user = NewId();
        await repo.InsertAsync(new Review { EntityId = entity, UserId = user, Status = ReviewStatus.Deleted, DeletedAt = DateTime.UtcNow });

        await AssertDuplicateAsync(() => repo.InsertAsync(new Review { EntityId = entity, UserId = user }));
    }

    [MongoFact]
    public async Task Different_users_and_different_entities_can_review()
    {
        var repo = new ReviewRepository(Db);
        string entity = NewId(), user = NewId();
        await repo.InsertAsync(new Review { EntityId = entity, UserId = user });
        await repo.InsertAsync(new Review { EntityId = entity, UserId = NewId() });
        await repo.InsertAsync(new Review { EntityId = NewId(), UserId = user });
    }

    [MongoFact]
    public async Task Revision_numbers_are_unique_per_review()
    {
        var repo = new ReviewRevisionRepository(Db);
        string review = NewId();
        await repo.AppendAsync(new ReviewRevision { ReviewId = review, RevisionNumber = 1, ChangedBy = NewId() });
        await repo.AppendAsync(new ReviewRevision { ReviewId = review, RevisionNumber = 2, ChangedBy = NewId() });

        await AssertDuplicateAsync(() => repo.AppendAsync(new ReviewRevision { ReviewId = review, RevisionNumber = 2, ChangedBy = NewId() }));
        Assert.Equal([1, 2], (await repo.ListAsync(review)).Select(r => r.RevisionNumber));
    }

    [Fact]
    public void Append_only_repositories_expose_no_way_to_change_or_remove_rows()
    {
        foreach (var type in new[] { typeof(ReviewRevisionRepository), typeof(ContributionRepository), typeof(AuditLogRepository) })
        {
            var forbidden = type.GetMethods().Where(m => m.DeclaringType == type &&
                new[] { "Update", "Replace", "Delete", "Remove", "Upsert" }.Any(p => m.Name.StartsWith(p, StringComparison.Ordinal)));
            Assert.Empty(forbidden);
        }
    }

    // ---- Rule 16: the ledger ----

    [MongoFact]
    public async Task A_ledger_entry_can_be_reversed_only_once()
    {
        var repo = new ContributionRepository(Db);
        var user = NewId();
        var earned = new ContributionEvent { UserId = user, Action = "written_review", Points = 10, Reason = "Written review" };
        await repo.AppendAsync(earned);
        Assert.Equal(10, await repo.TotalPointsAsync(user));

        await repo.ReverseAsync(earned, "Review removed");
        Assert.Equal(0, await repo.TotalPointsAsync(user));

        Assert.False(await repo.ReverseAsync(earned, "Review removed again")); // a second reversal is refused by the unique index and writes nothing
        Assert.Equal(0, await repo.TotalPointsAsync(user));
    }

    // ---- Identity ----

    [MongoFact]
    public async Task One_account_per_phone_number()
    {
        var repo = new UserRepository(Db);
        await repo.InsertAsync(new User { PhoneHash = "hash-1" });

        await AssertDuplicateAsync(() => repo.InsertAsync(new User { PhoneHash = "hash-1" }));
    }

    [MongoFact]
    public async Task Accounts_without_a_phone_or_login_do_not_collide()
    {
        var repo = new UserRepository(Db);
        await repo.InsertAsync(new User());
        await repo.InsertAsync(new User());
    }

    [MongoFact]
    public async Task One_account_per_social_login()
    {
        var repo = new UserRepository(Db);
        await repo.InsertAsync(new User { Logins = [new ExternalLogin { Provider = "google", Subject = "g-1" }] });
        await repo.InsertAsync(new User { Logins = [new ExternalLogin { Provider = "apple", Subject = "g-1" }] });

        await AssertDuplicateAsync(() => repo.InsertAsync(new User { Logins = [new ExternalLogin { Provider = "google", Subject = "g-1" }] }));
    }

    // ---- Moderation ----

    [MongoFact]
    public async Task A_user_can_report_an_item_only_once()
    {
        var repo = new ReportRepository(Db);
        string reporter = NewId(), target = NewId();
        await repo.InsertAsync(new Report { ReporterId = reporter, TargetType = ReportTarget.Review, TargetId = target, Reason = "abuse" });

        await AssertDuplicateAsync(() => repo.InsertAsync(new Report { ReporterId = reporter, TargetType = ReportTarget.Review, TargetId = target, Reason = "spam" }));
    }

    // ---- Verification (rule 5) ----

    [MongoFact]
    public async Task Decided_proofs_set_to_delete_are_due_for_deletion_and_pending_ones_are_not()
    {
        var repo = new VerificationRepository(Db);
        var user = NewId();
        var decided = new VerificationRecord { UserId = user, Type = VerificationType.Proof, Result = VerificationResult.Approved, StorageKey = "private/a" };
        var pending = new VerificationRecord { UserId = user, Type = VerificationType.Proof, Result = VerificationResult.Pending, StorageKey = "private/b" };
        var keptNotExpired = new VerificationRecord
        {
            UserId = user,
            Type = VerificationType.Proof,
            Result = VerificationResult.Approved,
            StorageKey = "private/c",
            Retention = new ProofRetention { Mode = RetentionMode.Keep, ExpiresAt = DateTime.UtcNow.AddDays(30) },
        };
        var keptExpired = new VerificationRecord
        {
            UserId = user,
            Type = VerificationType.Proof,
            Result = VerificationResult.Approved,
            StorageKey = "private/d",
            Retention = new ProofRetention { Mode = RetentionMode.Keep, ExpiresAt = DateTime.UtcNow.AddDays(-1) },
        };
        foreach (var r in new[] { decided, pending, keptNotExpired, keptExpired }) await repo.InsertAsync(r);

        var due = (await repo.ListDueForDeletionAsync(DateTime.UtcNow)).Where(v => v.UserId == user).Select(v => v.StorageKey).Order().ToList();

        Assert.Equal(["private/a", "private/d"], due);
    }

    // ---- Entities: geo, text, unique place id ----

    private static Entity NewEntity(string nameAr, string nameEn, double lon, double lat, params string[] alternates) => new()
    {
        CategoryId = "compounds",
        Name = new LocalizedText(nameAr, nameEn),
        AlternateNames = [.. alternates],
        Location = GeoJson.Point(GeoJson.Geographic(lon, lat)),
    };

    [MongoFact]
    public async Task Nearby_search_returns_only_entities_inside_the_distance_nearest_first()
    {
        var repo = new EntityRepository(Db);
        // Cairo (Tahrir), about 1.2 km away, and Alexandria (far).
        var near = NewEntity("قريب", "Near", 31.2400, 30.0450);
        var nearer = NewEntity("أقرب", "Nearer", 31.2357, 30.0444);
        var far = NewEntity("بعيد", "Far", 29.9187, 31.2001);
        foreach (var e in new[] { near, nearer, far }) await repo.InsertAsync(e);

        var result = await repo.NearAsync(31.2357, 30.0444, maxMeters: 5000);

        Assert.Equal([nearer.Id, near.Id], result.Select(e => e.Id));
    }

    [MongoFact]
    public async Task Text_search_finds_an_entity_by_arabic_name_english_name_and_alternate_name()
    {
        var repo = new EntityRepository(Db);
        var e = NewEntity("مدينتي", "Madinaty", 31.6, 30.1, "Madinety", "مدينتى");
        await repo.InsertAsync(e);

        foreach (var term in new[] { "مدينتي", "Madinaty", "Madinety" })
            Assert.Contains(await repo.SearchTextAsync(term), x => x.Id == e.Id);
    }

    [MongoFact]
    public async Task Two_entities_cannot_share_a_google_place_id_but_many_can_have_none()
    {
        var repo = new EntityRepository(Db);
        var a = NewEntity("أ", "A", 31.0, 30.0); a.GooglePlaceId = "place-1";
        var b = NewEntity("ب", "B", 31.1, 30.1); b.GooglePlaceId = "place-1";
        await repo.InsertAsync(a);
        await repo.InsertAsync(NewEntity("ج", "C", 31.2, 30.2));
        await repo.InsertAsync(NewEntity("د", "D", 31.3, 30.3));

        await AssertDuplicateAsync(() => repo.InsertAsync(b));
    }

    [MongoFact]
    public async Task Creating_the_indexes_twice_changes_nothing()
    {
        foreach (var initializer in MongoFixture.Initializers)
            await initializer.EnsureIndexesAsync(Db);
        foreach (var initializer in MongoFixture.Initializers)
            await initializer.EnsureIndexesAsync(Db);
    }
}
