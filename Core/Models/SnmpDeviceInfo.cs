namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class SnmpDeviceInfo
{
    public string Address { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string SysDescr { get; set; } = string.Empty;
    public string SysName { get; set; } = string.Empty;
    public string SysLocation { get; set; } = string.Empty;
    public string SysContact { get; set; } = string.Empty;
    public string SysUpTime { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string PrinterName { get; set; } = string.Empty;
    public string PrinterSerialNumber { get; set; } = string.Empty;
    public string PrinterStatus { get; set; } = string.Empty;
    public string SuppliesSummary { get; set; } = string.Empty;

    /// <summary>Structured per-consumable levels (toner C/M/Y/K, drum, waste) from the Printer-MIB.</summary>
    public List<SnmpSupplyInfo> Supplies { get; set; } = [];

    /// <summary>Lifetime page counter (<c>prtMarkerLifeCount</c>), null when the printer doesn't expose it.</summary>
    public long? PageCount { get; set; }

    /// <summary>Active printer alerts (<c>prtAlertDescription</c>) — paper jam, cover open, low toner, etc.</summary>
    public List<string> ActiveAlerts { get; set; } = [];

    public bool PrinterConfirmed { get; set; }
    public string Error { get; set; } = string.Empty;

    // ---- UI convenience (computed) ----
    public bool HasSupplies => Supplies.Count > 0;
    public bool HasAlerts => ActiveAlerts.Count > 0;
    public bool HasPageCount => PageCount.HasValue;
    public string AddressDisplay => ValueOrUnknown(Address);
    public string SysDescrDisplay => ValueOrUnknown(SysDescr);
    public string SysNameDisplay => ValueOrUnknown(SysName);
    public string SysLocationDisplay => ValueOrUnknown(SysLocation);
    public string SysContactDisplay => ValueOrUnknown(SysContact);
    public string SysUpTimeDisplay => ValueOrUnknown(SysUpTime);
    public string ProtocolDisplay => ValueOrUnknown(Protocol);
    public string PrinterNameDisplay => ValueOrUnknown(PrinterName);
    public string PrinterSerialNumberDisplay => ValueOrUnknown(PrinterSerialNumber);
    public string PrinterStatusDisplay => ValueOrUnknown(PrinterStatus);
    public string SuppliesSummaryDisplay => ValueOrUnknown(SuppliesSummary);
    public string PageCountText => PageCount.HasValue ? $"{PageCount.Value:N0} pages" : "Unknown";

    private static string ValueOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
}
