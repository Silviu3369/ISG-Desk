namespace NetScopeDiagnosticCenter.Core;

public sealed class ServiceTargetCandidate
{
    public string Host { get; init; } = string.Empty;
    public List<int> OpenPorts { get; init; } = [];
    public string OpenPortsText => OpenPorts.Count == 0 ? "-" : string.Join(", ", OpenPorts);
    public string Source { get; init; } = string.Empty;
    public string Confidence { get; init; } = "Medium";
}
