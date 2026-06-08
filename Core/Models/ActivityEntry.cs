namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class ActivityEntry
{
    public Guid EntryId { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public ActivitySourceModule SourceModule { get; set; } = ActivitySourceModule.Unknown;
    public ActivityStatus Status { get; set; } = ActivityStatus.Info;
    public string StepName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
}
