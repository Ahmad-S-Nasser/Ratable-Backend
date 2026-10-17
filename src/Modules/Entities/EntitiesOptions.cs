namespace Ratable.Modules.Entities;

/// <summary>Bound from the "Entities" configuration section.</summary>
public sealed class EntitiesOptions
{
    public const string Section = "Entities";

    /// <summary>Two places with the same name are only candidates for being one place if they are this close (meters). Chains have many branches.</summary>
    public double DuplicateRadiusMeters { get; set; } = 5000;

    /// <summary>Minimum name score (0 to 1) for a place to be a duplicate candidate.</summary>
    public double CandidateThreshold { get; set; } = 0.6;

    public int MaxCandidates { get; set; } = 5;
    public int MaxSubmissionsPerDay { get; set; } = 10;
    public int MaxNameLength { get; set; } = 120;
    public int MaxFieldLength { get; set; } = 200;
    public int MaxAlternateNames { get; set; } = 5;
}
