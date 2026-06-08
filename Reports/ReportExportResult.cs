namespace NetScopeDiagnosticCenter.Reports;

public sealed class ReportExportResult
{
    public string HtmlPath { get; set; } = string.Empty;
    public string TextPath { get; set; } = string.Empty;
    public string JsonPath { get; set; } = string.Empty;
}
