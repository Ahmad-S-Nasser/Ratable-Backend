namespace Ratable.Shared.Data;

public sealed class MongoOptions
{
    public const string Section = "Mongo";

    /// <summary>Set through the Mongo__ConnectionString environment variable. Never committed.</summary>
    public string ConnectionString { get; set; } = "";

    public string Database { get; set; } = "ratable";

    /// <summary>Create indexes and seed configuration data when the API starts.</summary>
    public bool BootstrapOnStartup { get; set; } = true;
}
