using Microsoft.Extensions.Logging.Abstractions;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;

namespace Ratable.Api.Tests.Data;

public sealed class CategorySeedFileTests
{
    private static readonly string[] Expected =
        ["appliances", "cars", "compounds", "hotels-resorts", "restaurants-cafes", "schools-nurseries"];

    [Fact]
    public void The_six_launch_categories_are_embedded()
    {
        Assert.Equal(Expected, CategorySeeder.LoadEmbedded().Select(c => c.Id).Order());
    }

    [Fact]
    public void Every_label_exists_in_arabic_and_english()
    {
        foreach (var c in CategorySeeder.LoadEmbedded())
        {
            AssertBoth(c.Name.Ar, c.Name.En, c.Id);
            Assert.NotEmpty(c.Criteria);
            Assert.NotEmpty(c.AcceptedProofTypes);
            Assert.NotEmpty(c.SchemaOrgType);
            foreach (var f in c.EntityFields) AssertBoth(f.Label.Ar, f.Label.En, $"{c.Id}/{f.Key}");
            foreach (var crit in c.Criteria)
            {
                AssertBoth(crit.Label.Ar, crit.Label.En, $"{c.Id}/{crit.Id}");
                foreach (var o in crit.Options) AssertBoth(o.Label.Ar, o.Label.En, $"{c.Id}/{crit.Id}/{o.Value}");
            }
        }
    }

    [Fact]
    public void Criterion_ids_are_unique_and_choices_have_options_scored_one_to_five()
    {
        foreach (var c in CategorySeeder.LoadEmbedded())
        {
            Assert.Equal(c.Criteria.Count, c.Criteria.Select(x => x.Id).Distinct().Count());
            foreach (var crit in c.Criteria)
            {
                if (crit.AnswerType == AnswerType.Rating5) Assert.Empty(crit.Options);
                else
                {
                    Assert.True(crit.Options.Count >= 2, $"{c.Id}/{crit.Id} needs options");
                    Assert.All(crit.Options, o => Assert.InRange(o.Score, 1, 5));
                }
            }
        }
    }

    private static void AssertBoth(string ar, string en, string where)
    {
        Assert.False(string.IsNullOrWhiteSpace(ar), $"{where}: Arabic label is missing");
        Assert.False(string.IsNullOrWhiteSpace(en), $"{where}: English label is missing");
    }
}

[Collection(MongoCollection.Name)]
public sealed class CategorySeederTests(MongoFixture fixture)
{
    private CategorySeeder Seeder(CategoryRepository repo) => new(repo, NullLogger<CategorySeeder>.Instance);

    [MongoFact]
    public async Task Seeding_inserts_the_six_categories_and_is_repeatable()
    {
        var repo = new CategoryRepository(fixture.Database);
        await Seeder(repo).SeedAsync(dryRun: false);
        await Seeder(repo).SeedAsync(dryRun: false);

        Assert.Equal(6, (await repo.ListActiveAsync()).Count);
    }

    [MongoFact]
    public async Task Seeding_never_overwrites_an_edited_category()
    {
        var repo = new CategoryRepository(fixture.Database);
        await Seeder(repo).SeedAsync(dryRun: false);
        var edited = (await repo.GetAsync("cars"))!;
        edited.Criteria.RemoveAt(0);
        edited.AnonymousByDefault = true;
        await repo.ReplaceAsync(edited);

        await Seeder(repo).SeedAsync(dryRun: false);

        var after = (await repo.GetAsync("cars"))!;
        Assert.Equal(edited.Criteria.Count, after.Criteria.Count);
        Assert.True(after.AnonymousByDefault);
    }

    [MongoFact]
    public async Task A_dry_run_writes_nothing()
    {
        var db = fixture.Database.Client.GetDatabase($"ratable_tests_dry_{Guid.NewGuid():N}");
        try
        {
            var repo = new CategoryRepository(db);
            await Seeder(repo).SeedAsync(dryRun: true);
            Assert.Empty(await repo.ListActiveAsync());
        }
        finally
        {
            await db.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName);
        }
    }
}
