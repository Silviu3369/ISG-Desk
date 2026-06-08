namespace NetScopeDiagnosticCenter.Core.Models;

public sealed class SnmpSessionOptions
{
    public string Protocol { get; set; } = SnmpProtocolVersion.V2C;
    public string Target { get; set; } = string.Empty;
    public string Community { get; set; } = "public";
    public string UserName { get; set; } = string.Empty;
    public string AuthProtocol { get; set; } = "SHA256";
    public string AuthPassword { get; set; } = string.Empty;
    public string PrivacyProtocol { get; set; } = "AES128";
    public string PrivacyPassword { get; set; } = string.Empty;
    public int TimeoutMs { get; set; } = 2500;
}

public static class SnmpProtocolVersion
{
    public const string V2C = "SNMP v2c";
    public const string V3AuthPriv = "SNMP v3 authPriv";
}
