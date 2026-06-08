namespace NetScopeDiagnosticCenter.Core;

/// <summary>
/// Single source of truth for Wi-Fi signal strength classification.
/// Replaces duplicate threshold logic in DiagnosticEngine, RuleEngine, LinkQualityAnalyzer
/// and the upcoming WifiAnalyzerEngine.
/// </summary>
public static class WifiSignalClassifier
{
    /// <summary>Minimum signal % considered "Excellent".</summary>
    public const int ExcellentThreshold = 80;

    /// <summary>Minimum signal % considered "Good".</summary>
    public const int GoodThreshold = 60;

    /// <summary>Minimum signal % considered "Fair".</summary>
    public const int FairThreshold = 40;

    /// <summary>Minimum signal % considered "Weak".</summary>
    public const int WeakThreshold = 20;

    /// <summary>
    /// Classifies a signal percentage into a human-readable quality label.
    /// </summary>
    public static string Classify(int? signalPercent)
    {
        if (!signalPercent.HasValue)
            return "Unknown";

        return signalPercent.Value switch
        {
            >= ExcellentThreshold => "Excellent",
            >= GoodThreshold => "Good",
            >= FairThreshold => "Fair",
            >= WeakThreshold => "Weak",
            _ => "Very Weak"
        };
    }

    /// <summary>
    /// Returns the diagnostic severity for a given signal percentage.
    /// </summary>
    public static string GetSeverity(int? signalPercent)
    {
        if (!signalPercent.HasValue)
            return "Unknown";

        return signalPercent.Value switch
        {
            >= GoodThreshold => "OK",
            >= FairThreshold => "Warning",
            _ => "Critical"
        };
    }

    /// <summary>
    /// Returns the health score penalty for a given signal percentage.
    /// Used by HealthScoreCalculator and WifiAnalyzerEngine.
    /// </summary>
    public static int GetHealthPenalty(int? signalPercent)
    {
        if (!signalPercent.HasValue)
            return 0;

        return signalPercent.Value switch
        {
            >= ExcellentThreshold => 0,
            >= GoodThreshold => 5,
            >= FairThreshold => 20,
            >= WeakThreshold => 40,
            _ => 60
        };
    }
}
