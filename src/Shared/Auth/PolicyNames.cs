namespace Ratable.Shared.Auth;

/// <summary>Authorization policy names. The Identity module defines what each one means; other modules only refer to them by name.</summary>
public static class PolicyNames
{
    public const string SignedIn = "SignedIn";
    public const string Moderator = "Moderator";
    public const string Admin = "Admin";
    public const string BusinessOwner = "BusinessOwner";
}
