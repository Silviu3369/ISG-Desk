namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class WorkflowInput
{
    public string ShareTarget { get; set; } = string.Empty;
    public string DomainControllerOverride { get; set; } = string.Empty;
    public string ServiceTarget { get; set; } = string.Empty;
    public string ServicePorts { get; set; } = string.Empty;
}
