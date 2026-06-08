namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class PrinterScanRequest
{
    public string Input { get; set; } = string.Empty;
    public int MaxHosts { get; set; } = 254;
    public List<int> Ports { get; set; } = [.. DiagnosticConstants.PrinterPorts];
}
