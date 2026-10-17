namespace Ratable.Shared.Data;

/// <summary>A user-facing string in both languages. Arabic is the primary one.</summary>
public sealed class LocalizedText
{
    public string Ar { get; set; } = "";
    public string En { get; set; } = "";

    public LocalizedText() { }
    public LocalizedText(string ar, string en) { Ar = ar; En = en; }
}
