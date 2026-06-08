using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Infrastructure;

public sealed class SecureCredentialService
{
    // DPAPI entropy — namespace-style label tied to the product. Changing this invalidates
    // any previously-encrypted credential files on disk (user re-enters SNMP creds once).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ISG Desk.SnmpV3");
    private static readonly byte[] EntropyFull = Encoding.UTF8.GetBytes("ISG Desk.SnmpFull");
    private readonly AppStorageService _appStorage;
    private readonly string _snmpV3Path;
    private readonly string _snmpFullPath;

    public SecureCredentialService(AppStorageService appStorage)
    {
        _appStorage = appStorage;
        _appStorage.EnsureFolders();
        _snmpV3Path = _appStorage.GetDataFilePath("snmpv3-credential.bin");
        _snmpFullPath = _appStorage.GetDataFilePath("snmp-credential.bin");
    }

    public bool HasSavedSnmpV3Credential => File.Exists(_snmpV3Path);

    public void SaveSnmpV3(SnmpSessionOptions options)
    {
        _appStorage.EnsureFolders();
        var dto = new StoredSnmpV3Credential
        {
            UserName = options.UserName,
            AuthProtocol = options.AuthProtocol,
            AuthPassword = options.AuthPassword,
            PrivacyProtocol = options.PrivacyProtocol,
            PrivacyPassword = options.PrivacyPassword
        };
        var json = JsonSerializer.Serialize(dto);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json),
            Entropy,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_snmpV3Path, protectedBytes);
    }

    public SnmpSessionOptions? LoadSnmpV3()
    {
        if (!File.Exists(_snmpV3Path))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_snmpV3Path);
            var jsonBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            var dto = JsonSerializer.Deserialize<StoredSnmpV3Credential>(Encoding.UTF8.GetString(jsonBytes));
            if (dto is null)
            {
                return null;
            }

            return new SnmpSessionOptions
            {
                Protocol = SnmpProtocolVersion.V3AuthPriv,
                UserName = dto.UserName,
                AuthProtocol = dto.AuthProtocol,
                AuthPassword = dto.AuthPassword,
                PrivacyProtocol = dto.PrivacyProtocol,
                PrivacyPassword = dto.PrivacyPassword
            };
        }
        catch
        {
            return null;
        }
    }

    public void ForgetSnmpV3()
    {
        if (File.Exists(_snmpV3Path))
        {
            File.Delete(_snmpV3Path);
        }
    }

    public bool HasSavedSnmpCredentials => File.Exists(_snmpFullPath);

    public void SaveSnmpCredentials(StoredSnmpCredential credential)
    {
        _appStorage.EnsureFolders();
        var json = JsonSerializer.Serialize(credential);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json),
            EntropyFull,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_snmpFullPath, protectedBytes);
    }

    public StoredSnmpCredential? LoadSnmpCredentials()
    {
        if (!File.Exists(_snmpFullPath))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_snmpFullPath);
            var jsonBytes = ProtectedData.Unprotect(
                protectedBytes,
                EntropyFull,
                DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredSnmpCredential>(Encoding.UTF8.GetString(jsonBytes));
        }
        catch
        {
            return null;
        }
    }

    public void ForgetSnmpCredentials()
    {
        if (File.Exists(_snmpFullPath))
        {
            File.Delete(_snmpFullPath);
        }
    }

    public sealed class StoredSnmpCredential
    {
        public string Protocol { get; set; } = SnmpProtocolVersion.V2C;
        public string Community { get; set; } = "public";
        public string UserName { get; set; } = string.Empty;
        public string AuthProtocol { get; set; } = "SHA256";
        public string AuthPassword { get; set; } = string.Empty;
        public string PrivacyProtocol { get; set; } = "AES128";
        public string PrivacyPassword { get; set; } = string.Empty;
    }

    private sealed class StoredSnmpV3Credential
    {
        public string UserName { get; set; } = string.Empty;
        public string AuthProtocol { get; set; } = "SHA256";
        public string AuthPassword { get; set; } = string.Empty;
        public string PrivacyProtocol { get; set; } = "AES128";
        public string PrivacyPassword { get; set; } = string.Empty;
    }
}
