namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class WifiInfo
{
    public string InterfaceName { get; set; } = "Unknown";
    public string InterfaceDescription { get; set; } = "Unknown";
    public string Ssid { get; set; } = "Unknown";
    public string Bssid { get; set; } = "Unknown";
    public int? SignalPercent { get; set; }
    public string RadioType { get; set; } = "Unknown";
    public int? Channel { get; set; }
    public string ReceiveRateMbps { get; set; } = "Unknown";
    public string TransmitRateMbps { get; set; } = "Unknown";
    public string Authentication { get; set; } = "Unknown";
    public string CollectorStatus { get; set; } = "OK";
}
