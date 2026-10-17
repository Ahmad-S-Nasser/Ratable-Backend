using Ratable.Shared.Authors;

namespace Ratable.Api.Tests.Anonymity;

public sealed class AuthorPresenterTests
{
    private sealed class RecordingProfiles(Dictionary<string, PublicProfile> known) : IPublicProfiles
    {
        public List<string> Requested { get; } = [];

        public Task<IReadOnlyDictionary<string, PublicProfile>> GetAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default)
        {
            Requested.AddRange(userIds);
            return Task.FromResult<IReadOnlyDictionary<string, PublicProfile>>(known.Where(k => userIds.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value));
        }
    }

    private static readonly Dictionary<string, PublicProfile> Known = new()
    {
        ["named"] = new PublicProfile("  Sara Ahmed ", "https://cdn.example.com/s.png"),
        ["secret"] = new PublicProfile("SECRET-NAME", "https://cdn.example.com/secret.png"),
        ["noname"] = new PublicProfile(null, "https://cdn.example.com/n.png"),
        ["blank"] = new PublicProfile("   ", null),
    };

    [Fact]
    public async Task An_anonymous_item_has_no_author_and_the_authors_profile_is_never_even_looked_up()
    {
        var profiles = new RecordingProfiles(Known);

        var result = await new AuthorPresenter(profiles).PresentAsync([new AuthorSlot("r1", "secret", IsAnonymous: true)]);

        Assert.Null(result["r1"]);
        Assert.Empty(profiles.Requested);
    }

    [Fact]
    public async Task A_named_item_shows_the_public_profile_name_and_avatar()
    {
        var result = await new AuthorPresenter(new RecordingProfiles(Known)).PresentAsync([new AuthorSlot("r1", "named", false)]);

        Assert.Equal(new PublicAuthor("Sara Ahmed", "https://cdn.example.com/s.png"), result["r1"]);
    }

    [Theory]
    [InlineData("noname")]
    [InlineData("blank")]
    [InlineData("unknown-account")]
    public async Task A_named_item_whose_author_has_no_public_name_is_shown_as_anonymous(string userId)
    {
        var result = await new AuthorPresenter(new RecordingProfiles(Known)).PresentAsync([new AuthorSlot("r1", userId, false)]);

        Assert.Null(result["r1"]);
    }

    [Fact]
    public async Task In_a_mixed_batch_only_the_named_authors_are_looked_up_once_each()
    {
        var profiles = new RecordingProfiles(Known);

        var result = await new AuthorPresenter(profiles).PresentAsync(
        [
            new AuthorSlot("a", "secret", true),
            new AuthorSlot("b", "named", false),
            new AuthorSlot("c", "named", false),
            new AuthorSlot("d", "secret", true),
        ]);

        Assert.Equal(["named"], profiles.Requested);
        Assert.Null(result["a"]);
        Assert.Null(result["d"]);
        Assert.Equal("Sara Ahmed", result["b"]!.Name);
        Assert.Equal("Sara Ahmed", result["c"]!.Name);
    }

    [Fact]
    public async Task The_same_author_can_be_named_on_one_item_and_anonymous_on_another()
    {
        var result = await new AuthorPresenter(new RecordingProfiles(Known)).PresentAsync(
            [new AuthorSlot("named-review", "named", false), new AuthorSlot("anonymous-review", "named", true)]);

        Assert.NotNull(result["named-review"]);
        Assert.Null(result["anonymous-review"]);
    }

    [Fact]
    public async Task Nothing_to_present_looks_nothing_up()
    {
        var profiles = new RecordingProfiles(Known);

        var result = await new AuthorPresenter(profiles).PresentAsync([]);

        Assert.Empty(result);
        Assert.Empty(profiles.Requested);
    }
}
