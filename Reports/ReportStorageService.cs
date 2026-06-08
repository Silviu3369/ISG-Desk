using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Reports;

public sealed class ReportStorageService
{
    private readonly AppStorageService _appStorage;
    private readonly HtmlReportBuilder _htmlReportBuilder;
    private readonly TextSummaryBuilder _textSummaryBuilder;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public ReportStorageService(
        AppStorageService appStorage,
        HtmlReportBuilder htmlReportBuilder,
        TextSummaryBuilder textSummaryBuilder)
    {
        _appStorage = appStorage;
        _htmlReportBuilder = htmlReportBuilder;
        _textSummaryBuilder = textSummaryBuilder;
    }

    /// <summary>
    /// Absolute path of the folder where report sets are written. Surfaced so the UI can
    /// offer an "Open Reports folder" action without taking its own AppStorageService dependency.
    /// </summary>
    public string ReportFolder => _appStorage.ReportFolder;

    public ReportExportResult SaveReportSet(NetworkDiagnosisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Directory.CreateDirectory(_appStorage.ReportFolder);
        var paths = CreateUniqueReportPaths();

        File.WriteAllText(paths.HtmlPath, _htmlReportBuilder.Build(result), Encoding.UTF8);
        File.WriteAllText(paths.TextPath, _textSummaryBuilder.Build(result), Encoding.UTF8);
        File.WriteAllText(paths.JsonPath, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);

        return paths;
    }

    private ReportExportResult CreateUniqueReportPaths()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var baseName = attempt == 0
                ? $"network-diagnostic-{timestamp}"
                : $"network-diagnostic-{timestamp}-{attempt:00}";

            var result = new ReportExportResult
            {
                HtmlPath = Path.Combine(_appStorage.ReportFolder, $"{baseName}.html"),
                TextPath = Path.Combine(_appStorage.ReportFolder, $"{baseName}.txt"),
                JsonPath = Path.Combine(_appStorage.ReportFolder, $"{baseName}.json")
            };

            if (!File.Exists(result.HtmlPath) &&
                !File.Exists(result.TextPath) &&
                !File.Exists(result.JsonPath))
            {
                return result;
            }
        }

        throw new IOException("Could not allocate a unique report file name.");
    }
}
