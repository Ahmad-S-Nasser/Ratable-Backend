namespace Ratable.Shared.Data;

/// <summary>Loads configuration data. Seeders only insert what is missing and never overwrite edits made later.</summary>
public interface ISeeder
{
    Task SeedAsync(bool dryRun, CancellationToken cancellationToken = default);
}
