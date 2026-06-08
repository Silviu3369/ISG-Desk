using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;

namespace NetScopeDiagnosticCenter.Tests.Core.Wifi;

public class WifiSecurityProfileTests
{
    [Fact]
    public void From_OpenNetwork_LabelsAsOpenAndFlagsInsecure()
    {
        var p = WifiSecurityProfile.From(Dot11AuthAlgorithm.Open, Dot11CipherAlgorithm.None, securityEnabled: false);
        p.Label.Should().Be("Open");
        p.IsOpen.Should().BeTrue();
        p.IsLegacyInsecure.Should().BeTrue();
        p.IsModernSecure.Should().BeFalse();
        p.HasForwardSecrecy.Should().BeFalse();
    }

    [Fact]
    public void From_SecurityDisabledFlag_TreatsAsOpenEvenWithAuthValue()
    {
        // Driver reports RSNA but security flag is false → treat as Open (Windows convention).
        var p = WifiSecurityProfile.From(Dot11AuthAlgorithm.Rsna, Dot11CipherAlgorithm.Ccmp, securityEnabled: false);
        p.IsOpen.Should().BeTrue();
        p.Label.Should().Be("Open");
    }

    [Fact]
    public void From_Wpa2Personal_LabelsAndFlagsAsModern()
    {
        var p = WifiSecurityProfile.From(Dot11AuthAlgorithm.RsnaPsk, Dot11CipherAlgorithm.Ccmp, securityEnabled: true);
        p.Label.Should().Be("WPA2-Personal");
        p.IsOpen.Should().BeFalse();
        p.IsLegacyInsecure.Should().BeFalse();
        p.IsModernSecure.Should().BeTrue();
        p.HasForwardSecrecy.Should().BeFalse();  // WPA2 lacks SAE
    }

    [Fact]
    public void From_Wpa2PersonalWithTkip_LabelsTkipFallback()
    {
        // Legacy AP forced TKIP fallback — still WPA2-Personal but explicit TKIP note.
        var p = WifiSecurityProfile.From(Dot11AuthAlgorithm.RsnaPsk, Dot11CipherAlgorithm.Tkip, securityEnabled: true);
        p.Label.Should().Be("WPA2-Personal (TKIP)");
        // TKIP cipher → legacy insecure even with WPA2 auth.
        p.IsLegacyInsecure.Should().BeTrue();
    }

    [Fact]
    public void From_Wpa3Personal_LabelsAndFlagsForwardSecrecy()
    {
        var p = WifiSecurityProfile.From(Dot11AuthAlgorithm.Wpa3Sae, Dot11CipherAlgorithm.Ccmp, securityEnabled: true);
        p.Label.Should().Be("WPA3-Personal");
        p.IsModernSecure.Should().BeTrue();
        p.HasForwardSecrecy.Should().BeTrue();
    }

    [Fact]
    public void From_Wpa3Enterprise_LabelsAndFlagsForwardSecrecy()
    {
        var p = WifiSecurityProfile.From(Dot11AuthAlgorithm.Wpa3Ent192, Dot11CipherAlgorithm.Gcmp256, securityEnabled: true);
        p.Label.Should().Be("WPA3-Enterprise (192-bit)");
        p.HasForwardSecrecy.Should().BeTrue();
    }

    [Fact]
    public void From_EnhancedOpen_LabelsOweAndFlagsForwardSecrecy()
    {
        // OWE = Opportunistic Wireless Encryption (Wi-Fi Alliance "Enhanced Open").
        var p = WifiSecurityProfile.From(Dot11AuthAlgorithm.Owe, Dot11CipherAlgorithm.Ccmp, securityEnabled: true);
        p.Label.Should().Be("Enhanced Open (OWE)");
        p.HasForwardSecrecy.Should().BeTrue();
        p.IsOpen.Should().BeFalse();  // OWE encrypts despite "open" name
    }

    [Fact]
    public void From_Wpa2Enterprise_LabelsCorrectly()
    {
        var p = WifiSecurityProfile.From(Dot11AuthAlgorithm.Rsna, Dot11CipherAlgorithm.Ccmp, securityEnabled: true);
        p.Label.Should().Be("WPA2-Enterprise");
        p.IsModernSecure.Should().BeTrue();
    }

    [Fact]
    public void From_LegacyWpaPersonal_FlagsAsLegacyInsecure()
    {
        var p = WifiSecurityProfile.From(Dot11AuthAlgorithm.WpaPsk, Dot11CipherAlgorithm.Tkip, securityEnabled: true);
        p.Label.Should().Contain("WPA-Personal");
        p.IsLegacyInsecure.Should().BeTrue();
        p.IsModernSecure.Should().BeFalse();
        p.HasForwardSecrecy.Should().BeFalse();
    }

    [Fact]
    public void From_WepSharedKey_FlagsAsLegacyInsecure()
    {
        var p = WifiSecurityProfile.From(Dot11AuthAlgorithm.SharedKey, Dot11CipherAlgorithm.Wep, securityEnabled: true);
        p.Label.Should().Be("WEP");
        p.IsLegacyInsecure.Should().BeTrue();
        p.HasForwardSecrecy.Should().BeFalse();
    }
}
