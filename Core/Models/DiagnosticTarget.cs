namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class DiagnosticTarget
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string SharePath { get; set; } = string.Empty;
    public List<int> Ports { get; set; } = [];
    public string Purpose { get; set; } = string.Empty;

    public override string ToString()
    {
        var label = string.IsNullOrWhiteSpace(Name) ? Host : Name;
        return string.IsNullOrWhiteSpace(Purpose) ? label : $"{label} ({Purpose})";
    }
}
