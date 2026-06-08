namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class LinkQualityThresholds
{
    public static LinkQualityThresholds Default { get; } = new();

    public int PingTimeoutMs { get; set; } = 1200;
    public int DnsTimeoutMs { get; set; } = 2000;

    public double GatewayWarningLatencyMs { get; set; } = 20;
    public double GatewayCriticalLatencyMs { get; set; } = 80;
    public double InternetWarningLatencyMs { get; set; } = 120;
    public double InternetCriticalLatencyMs { get; set; } = 250;
    public double TargetWarningLatencyMs { get; set; } = 100;
    public double TargetCriticalLatencyMs { get; set; } = 250;

    public double WarningJitterMs { get; set; } = 30;
    public double CriticalJitterMs { get; set; } = 50;
    public double WarningPacketLossPercent { get; set; } = 0;
    public double CriticalLocalPacketLossPercent { get; set; } = 1;
    public double CriticalInternetPacketLossPercent { get; set; } = 5;

    public double DnsWarningLatencyMs { get; set; } = 750;
    public double DnsCriticalLatencyMs { get; set; } = 1500;

    public string Summary =>
        $"Gateway warn/critical {GatewayWarningLatencyMs:N0}/{GatewayCriticalLatencyMs:N0} ms; " +
        $"Internet warn/critical {InternetWarningLatencyMs:N0}/{InternetCriticalLatencyMs:N0} ms; " +
        $"jitter warn/critical {WarningJitterMs:N0}/{CriticalJitterMs:N0} ms; " +
        $"DNS warn/critical {DnsWarningLatencyMs:N0}/{DnsCriticalLatencyMs:N0} ms.";

    public string GatewaySummary =>
        $"Gateway: warning over {GatewayWarningLatencyMs:N0} ms, critical over {GatewayCriticalLatencyMs:N0} ms or loss over {CriticalLocalPacketLossPercent:N1}%.";

    public string InternetSummary =>
        $"Internet: warning over {InternetWarningLatencyMs:N0} ms, critical over {InternetCriticalLatencyMs:N0} ms or loss over {CriticalInternetPacketLossPercent:N1}%.";

    public string TargetSummary =>
        $"Target: warning over {TargetWarningLatencyMs:N0} ms, critical over {TargetCriticalLatencyMs:N0} ms or loss over {CriticalLocalPacketLossPercent:N1}%.";

    public string DnsSummary =>
        $"DNS: warning over {DnsWarningLatencyMs:N0} ms, critical over {DnsCriticalLatencyMs:N0} ms or no usable address.";

    public string StabilitySummary =>
        $"Jitter warning over {WarningJitterMs:N0} ms, critical over {CriticalJitterMs:N0} ms; any packet loss is a warning.";
}

public enum LinkQualityPingCategory
{
    Target,
    Gateway,
    Internet
}
