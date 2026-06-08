using System.IO;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Infrastructure;

namespace NetScopeDiagnosticCenter.Tests.Infrastructure;

/// <summary>
/// Tests for SnmpCredentialStore exercise the public ObservableObject API and the
/// Save/Load round-trip through SecureCredentialService. Each test uses an isolated
/// settings folder under TempPath to avoid touching the real LocalAppData.
/// </summary>
public class SnmpCredentialStoreTests : IDisposable
{
    private readonly string _isolatedAppData;

    public SnmpCredentialStoreTests()
    {
        _isolatedAppData = Path.Combine(Path.GetTempPath(), "NetScopeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_isolatedAppData);
    }

    public void Dispose()
    {
        try { Directory.Delete(_isolatedAppData, recursive: true); } catch { /* best-effort */ }
    }

    private (SnmpCredentialStore store, SecureCredentialService cred) BuildStore()
    {
        var storage = new AppStorageService(_isolatedAppData);
        var cred = new SecureCredentialService(storage);
        return (new SnmpCredentialStore(cred), cred);
    }

    [Fact]
    public void Defaults_AreV2cWithPublic()
    {
        var (store, _) = BuildStore();
        store.Protocol.Should().Be(SnmpProtocolVersion.V2C);
        store.Community.Should().Be("public");
        store.IsV3.Should().BeFalse();
    }

    [Fact]
    public void IsV3_SwitchesWhenProtocolChanged()
    {
        var (store, _) = BuildStore();
        store.IsV3.Should().BeFalse();
        store.Protocol = SnmpProtocolVersion.V3AuthPriv;
        store.IsV3.Should().BeTrue();
    }

    [Fact]
    public void BuildSessionOptions_PassesAllFields()
    {
        var (store, _) = BuildStore();
        store.Protocol = SnmpProtocolVersion.V3AuthPriv;
        store.UserName = "monitor";
        store.AuthPassword = "auth-pass";
        store.PrivacyPassword = "priv-pass";

        var options = store.BuildSessionOptions("192.168.1.1");

        options.Target.Should().Be("192.168.1.1");
        options.Protocol.Should().Be(SnmpProtocolVersion.V3AuthPriv);
        options.UserName.Should().Be("monitor");
        options.AuthPassword.Should().Be("auth-pass");
        options.PrivacyPassword.Should().Be("priv-pass");
    }

    [Fact]
    public void SaveLoad_RoundTripsAllFields()
    {
        var (store, cred) = BuildStore();
        store.Protocol = SnmpProtocolVersion.V2C;
        store.Community = "secret-community";
        store.UserName = "u1";
        store.AuthProtocol = "SHA512";
        store.AuthPassword = "a1";
        store.PrivacyProtocol = "AES256";
        store.PrivacyPassword = "p1";
        store.Save();

        var (reloaded, _) = BuildStore();
        reloaded.Load();

        reloaded.Protocol.Should().Be(SnmpProtocolVersion.V2C);
        reloaded.Community.Should().Be("secret-community");
        reloaded.UserName.Should().Be("u1");
        reloaded.AuthProtocol.Should().Be("SHA512");
        reloaded.AuthPassword.Should().Be("a1");
        reloaded.PrivacyProtocol.Should().Be("AES256");
        reloaded.PrivacyPassword.Should().Be("p1");
    }

    [Fact]
    public void Load_NoSavedCredentials_LeavesDefaults()
    {
        var (store, _) = BuildStore();
        store.Load();
        store.Protocol.Should().Be(SnmpProtocolVersion.V2C);
        store.Community.Should().Be("public");
    }

    [Fact]
    public void Load_LegacyV3Credential_MigratedToNewFormatAndLegacyDeleted()
    {
        var (_, cred) = BuildStore();
        cred.SaveSnmpV3(new SnmpSessionOptions
        {
            Protocol = SnmpProtocolVersion.V3AuthPriv,
            UserName = "legacy-user",
            AuthProtocol = "SHA256",
            AuthPassword = "legacy-auth",
            PrivacyProtocol = "AES128",
            PrivacyPassword = "legacy-priv"
        });
        cred.HasSavedSnmpV3Credential.Should().BeTrue();

        var (store, cred2) = BuildStore();
        store.Load();

        // Migrated to v3 protocol with legacy fields
        store.Protocol.Should().Be(SnmpProtocolVersion.V3AuthPriv);
        store.UserName.Should().Be("legacy-user");
        store.AuthPassword.Should().Be("legacy-auth");
        store.PrivacyPassword.Should().Be("legacy-priv");

        // Legacy file should now be gone
        cred2.HasSavedSnmpV3Credential.Should().BeFalse();

        // And new format should exist
        cred2.HasSavedSnmpCredentials.Should().BeTrue();
    }

    [Fact]
    public void Forget_RemovesSavedCredentialsAndClearsInMemorySecrets()
    {
        var (store, cred) = BuildStore();
        store.Protocol = SnmpProtocolVersion.V3AuthPriv;
        store.Community = "secret";
        store.UserName = "monitor";
        store.AuthPassword = "auth-secret";
        store.PrivacyPassword = "priv-secret";
        store.Save();
        cred.HasSavedSnmpCredentials.Should().BeTrue();

        store.Forget();
        cred.HasSavedSnmpCredentials.Should().BeFalse();
        store.Protocol.Should().Be(SnmpProtocolVersion.V2C);
        store.Community.Should().Be("public");
        store.UserName.Should().BeEmpty();
        store.AuthPassword.Should().BeEmpty();
        store.PrivacyPassword.Should().BeEmpty();
    }
}
