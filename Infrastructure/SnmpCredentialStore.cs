using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.UI;

namespace NetScopeDiagnosticCenter.Infrastructure;

/// <summary>
/// Single shared SNMP credential set used by Printers and Network Devices modules.
/// Persisted encrypted via DPAPI through SecureCredentialService.
/// </summary>
public sealed class SnmpCredentialStore : ObservableObject
{
    private readonly SecureCredentialService _credentialService;

    private string _protocol = SnmpProtocolVersion.V2C;
    private string _community = "public";
    private string _userName = string.Empty;
    private string _authProtocol = "SHA256";
    private string _authPassword = string.Empty;
    private string _privacyProtocol = "AES128";
    private string _privacyPassword = string.Empty;

    public SnmpCredentialStore(SecureCredentialService credentialService)
    {
        _credentialService = credentialService;
    }

    public bool HasSavedCredentials =>
        _credentialService.HasSavedSnmpCredentials || _credentialService.HasSavedSnmpV3Credential;

    public string Protocol
    {
        get => _protocol;
        set
        {
            if (SetProperty(ref _protocol, value))
            {
                OnPropertyChanged(nameof(IsV3));
            }
        }
    }

    public string Community
    {
        get => _community;
        set => SetProperty(ref _community, value);
    }

    public string UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value);
    }

    public string AuthProtocol
    {
        get => _authProtocol;
        set => SetProperty(ref _authProtocol, value);
    }

    public string AuthPassword
    {
        get => _authPassword;
        set => SetProperty(ref _authPassword, value);
    }

    public string PrivacyProtocol
    {
        get => _privacyProtocol;
        set => SetProperty(ref _privacyProtocol, value);
    }

    public string PrivacyPassword
    {
        get => _privacyPassword;
        set => SetProperty(ref _privacyPassword, value);
    }

    public bool IsV3 => Protocol == SnmpProtocolVersion.V3AuthPriv;

    public SnmpSessionOptions BuildSessionOptions(string target) => new()
    {
        Protocol = Protocol,
        Target = target,
        Community = Community,
        UserName = UserName,
        AuthProtocol = AuthProtocol,
        AuthPassword = AuthPassword,
        PrivacyProtocol = PrivacyProtocol,
        PrivacyPassword = PrivacyPassword
    };

    public void Load()
    {
        // Try the new full-credential format first.
        var saved = _credentialService.LoadSnmpCredentials();
        if (saved is not null)
        {
            ApplyStored(saved);
            OnPropertyChanged(nameof(HasSavedCredentials));
            return;
        }

        // Migration: older versions stored only v3 credentials in a separate legacy file.
        // If a legacy v3 credential exists, import it, save in the new format, and delete legacy.
        if (_credentialService.HasSavedSnmpV3Credential)
        {
            var legacy = _credentialService.LoadSnmpV3();
            if (legacy is not null)
            {
                Protocol = SnmpProtocolVersion.V3AuthPriv;
                Community = "public";
                UserName = legacy.UserName;
                AuthProtocol = string.IsNullOrWhiteSpace(legacy.AuthProtocol) ? "SHA256" : legacy.AuthProtocol;
                AuthPassword = legacy.AuthPassword;
                PrivacyProtocol = string.IsNullOrWhiteSpace(legacy.PrivacyProtocol) ? "AES128" : legacy.PrivacyProtocol;
                PrivacyPassword = legacy.PrivacyPassword;
                Save();
                _credentialService.ForgetSnmpV3();
                OnPropertyChanged(nameof(HasSavedCredentials));
            }
        }
    }

    private void ApplyStored(SecureCredentialService.StoredSnmpCredential saved)
    {
        Protocol = string.IsNullOrWhiteSpace(saved.Protocol) ? SnmpProtocolVersion.V2C : saved.Protocol;
        Community = string.IsNullOrWhiteSpace(saved.Community) ? "public" : saved.Community;
        UserName = saved.UserName ?? string.Empty;
        AuthProtocol = string.IsNullOrWhiteSpace(saved.AuthProtocol) ? "SHA256" : saved.AuthProtocol;
        AuthPassword = saved.AuthPassword ?? string.Empty;
        PrivacyProtocol = string.IsNullOrWhiteSpace(saved.PrivacyProtocol) ? "AES128" : saved.PrivacyProtocol;
        PrivacyPassword = saved.PrivacyPassword ?? string.Empty;
    }

    public void Save()
    {
        _credentialService.SaveSnmpCredentials(new SecureCredentialService.StoredSnmpCredential
        {
            Protocol = Protocol,
            Community = Community,
            UserName = UserName,
            AuthProtocol = AuthProtocol,
            AuthPassword = AuthPassword,
            PrivacyProtocol = PrivacyProtocol,
            PrivacyPassword = PrivacyPassword
        });
        OnPropertyChanged(nameof(HasSavedCredentials));
    }

    public void Forget()
    {
        _credentialService.ForgetSnmpCredentials();
        _credentialService.ForgetSnmpV3();
        Protocol = SnmpProtocolVersion.V2C;
        Community = "public";
        UserName = string.Empty;
        AuthProtocol = "SHA256";
        AuthPassword = string.Empty;
        PrivacyProtocol = "AES128";
        PrivacyPassword = string.Empty;
        OnPropertyChanged(nameof(HasSavedCredentials));
    }
}
