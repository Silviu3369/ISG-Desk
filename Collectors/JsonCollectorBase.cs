using System.Text.Json;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Collectors;

public abstract class JsonCollectorBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    protected JsonCollectorBase(PowerShellRunner powerShellRunner)
    {
        PowerShellRunner = powerShellRunner;
    }

    protected PowerShellRunner PowerShellRunner { get; }

    protected async Task<T?> RunCollectorAsync<T>(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var result = await PowerShellRunner.RunJsonScriptAsync(script, timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(result.Output, JsonOptions);
    }

    protected async Task<(T? Data, PowerShellExecutionResult Execution)> RunCollectorWithExecutionAsync<T>(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var result = await PowerShellRunner.RunJsonScriptAsync(script, timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return (default, result);
        }

        return (JsonSerializer.Deserialize<T>(result.Output, JsonOptions), result);
    }

    /// <summary>
    /// Safely embeds an untrusted string as a PowerShell single-quoted literal: doubles
    /// embedded single quotes (the only metachar inside a single-quoted PS string) and
    /// strips control characters / newlines so a value can never break out of the literal
    /// or inject a new statement. The single audited choke-point for all collectors —
    /// callers MUST wrap the result in single quotes: <c>'{PsSingleQuote(value)}'</c>.
    /// </summary>
    protected static string PsSingleQuote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray());
        if (cleaned.Length > 256) cleaned = cleaned[..256];
        return cleaned.Replace("'", "''", StringComparison.Ordinal);
    }
}
