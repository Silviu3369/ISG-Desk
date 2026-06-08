namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class PrinterInfo
{
    public string Name { get; set; } = string.Empty;
    public string ShareName { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public string PortHostAddress { get; set; } = string.Empty;
    public string PortProtocol { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public string PrinterStatus { get; set; } = "Unknown";
    public string Location { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public bool Shared { get; set; }
    public bool IsDefault { get; set; }
    public bool Installable { get; set; }
    public string InstallStatus { get; set; } = string.Empty;
    public string InstallError { get; set; } = string.Empty;
}
