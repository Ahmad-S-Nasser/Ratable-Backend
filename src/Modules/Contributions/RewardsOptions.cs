using Ratable.Shared.Data;

namespace Ratable.Modules.Contributions;

public sealed class RankDefinition
{
    public string Id { get; set; } = "";
    public int MinPoints { get; set; }
    public LocalizedText Title { get; set; } = new();
}

public sealed class BadgeDefinition
{
    public string Id { get; set; } = "";
    public LocalizedText Title { get; set; } = new();
    public LocalizedText Description { get; set; } = new();
    /// <summary>The kind of effort that counts toward it (a ContributionActions value).</summary>
    public string Action { get; set; } = "";
    public int Threshold { get; set; }
}

public sealed class MissionDefinition
{
    public string Id { get; set; } = "";
    public LocalizedText Title { get; set; } = new();
    public string Action { get; set; } = "";
    public int Target { get; set; }
}

/// <summary>
/// Bound from "Rewards". Ranks, badges, missions and the point values are configuration, not code (rule 16). The defaults below are the plan's numbers:
/// written review 10, photo 5, video 20, verified visit 15, first review of a place 25, helpful vote 2 up to 20 a day; ranks Explorer 0, Reviewer 100,
/// Expert 500, Ambassador 2,000. The streak counts weeks, not days.
/// </summary>
public sealed class RewardsOptions
{
    public const string Section = "Rewards";

    public Dictionary<string, int> Points { get; set; } = new()
    {
        ["written_review"] = 10,
        ["photo"] = 5,
        ["video"] = 20,
        ["verified_visit"] = 15,
        ["first_review"] = 25,
        ["helpful_vote"] = 2,
    };

    /// <summary>The most points a person can earn from one kind of action in a day (UTC). An action not listed has no daily cap.</summary>
    public Dictionary<string, int> DailyCaps { get; set; } = new() { ["helpful_vote"] = 20 };

    /// <summary>A review needs at least this much text to count as "written".</summary>
    public int MinWrittenLength { get; set; } = 20;

    public DayOfWeek WeekStartsOn { get; set; } = DayOfWeek.Saturday;

    public List<RankDefinition> Ranks { get; set; } =
    [
        new() { Id = "explorer", MinPoints = 0, Title = new("مستكشف", "Explorer") },
        new() { Id = "reviewer", MinPoints = 100, Title = new("مراجع", "Reviewer") },
        new() { Id = "expert", MinPoints = 500, Title = new("خبير", "Expert") },
        new() { Id = "ambassador", MinPoints = 2000, Title = new("سفير", "Ambassador") },
    ];

    public List<BadgeDefinition> Badges { get; set; } =
    [
        new() { Id = "first_words", Action = "written_review", Threshold = 1, Title = new("أول كلماتك", "First words"), Description = new("كتبت أول تقييم منشور.", "You wrote your first published review.") },
        new() { Id = "storyteller", Action = "written_review", Threshold = 10, Title = new("حكّاء", "Storyteller"), Description = new("10 تقييمات مكتوبة منشورة.", "10 published written reviews.") },
        new() { Id = "photographer", Action = "photo", Threshold = 10, Title = new("مصوّر", "Photographer"), Description = new("10 صور منشورة.", "10 published photos.") },
        new() { Id = "filmmaker", Action = "video", Threshold = 3, Title = new("صانع فيديو", "Filmmaker"), Description = new("3 فيديوهات منشورة.", "3 published videos.") },
        new() { Id = "verified_visitor", Action = "verified_visit", Threshold = 5, Title = new("زائر موثّق", "Verified visitor"), Description = new("5 زيارات موثقة.", "5 verified visits.") },
        new() { Id = "pioneer", Action = "first_review", Threshold = 3, Title = new("رائد", "Pioneer"), Description = new("كنت أول من قيّم 3 أماكن.", "You were the first to review 3 places.") },
        new() { Id = "helpful", Action = "helpful_vote", Threshold = 25, Title = new("مفيد", "Helpful"), Description = new("25 شخصًا وجدوا تقييماتك مفيدة.", "25 people found your reviews helpful.") },
    ];

    public List<MissionDefinition> Missions { get; set; } =
    [
        new() { Id = "write_two", Action = "written_review", Target = 2, Title = new("اكتب تقييمين هذا الأسبوع", "Write two reviews this week") },
        new() { Id = "add_photos", Action = "photo", Target = 3, Title = new("أضف 3 صور هذا الأسبوع", "Add three photos this week") },
        new() { Id = "verify_visit", Action = "verified_visit", Target = 1, Title = new("وثّق زيارة هذا الأسبوع", "Verify a visit this week") },
    ];

    /// <summary>The actions that keep a weekly streak alive: effort, never opinion.</summary>
    public string[] StreakActions { get; set; } = ["written_review", "photo", "video", "verified_visit", "first_review"];
}
