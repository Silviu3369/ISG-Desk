using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using NetScopeDiagnosticCenter.Collectors.Wifi;
using NetScopeDiagnosticCenter.Core.Models;
using NetScopeDiagnosticCenter.Core.Models.Wifi;
using NetScopeDiagnosticCenter.Core.Wifi;
using NetScopeDiagnosticCenter.Infrastructure;
using NetScopeDiagnosticCenter.Infrastructure.Wlan;
using NetScopeDiagnosticCenter.UI;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.Tests.UI.ViewModels;

public sealed class WifiAnalyzerViewModelTests
{
    [Fact]
    public async Task ScanCommand_IsDisabledUntilMonitoringStarts()
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);

        vm.StartMonitoringCommand.CanExecute(null).Should().BeTrue();
        vm.ScanNetworksCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task StartMonitoring_StartsSamplerAndRunsInitialScan()
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out var sampler);

        await PrimeAdapterAsync(vm);
        await ((AsyncRelayCommand)vm.StartMonitoringCommand).ExecuteAsync(null);

        vm.IsMonitoring.Should().BeTrue();
        sampler.IsRunning.Should().BeTrue();
        scanner.ForceScanRequests.Should().Be(1);
        vm.HasScanData.Should().BeTrue();
        vm.IsScanning.Should().BeFalse();
        vm.Status.Should().Contain("Scan complete");
    }

    [Theory]
    [InlineData("WPA2")]
    [InlineData("20 MHz")]
    [InlineData("Ch 6")]
    [InlineData("Test Vendor")]
    public async Task NetworkFilter_MatchesVisibleNetworkDisplayFields(string filter)
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);
        await ((AsyncRelayCommand)vm.StartMonitoringCommand).ExecuteAsync(null);

        vm.NetworkFilter = filter;

        vm.FilteredAccessPoints.Should().ContainSingle();
        vm.NetworkFilterSummary.Should().Be("1 of 1 networks");
    }

    [Fact]
    public async Task SavedProfiles_AreNormalizedAndSummarized()
    {
        var scanner = new FakeWifiScanner
        {
            SavedProfileNames = [" Office ", "guest", "OFFICE", "", "IoT"]
        };
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);
        await ((AsyncRelayCommand)vm.StartMonitoringCommand).ExecuteAsync(null);

        vm.SavedProfiles.Select(profile => profile.DisplayName)
            .Should().Equal("guest", "IoT", "Office");
        vm.HasSavedProfiles.Should().BeTrue();
        vm.NoSavedProfiles.Should().BeFalse();
        vm.SavedProfilesSummary.Should().Be("3 saved Wi-Fi profiles");
    }

    [Fact]
    public async Task SavedProfiles_EmptyAfterScan_HasClearEmptyState()
    {
        var scanner = new FakeWifiScanner
        {
            SavedProfileNames = []
        };
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);
        await ((AsyncRelayCommand)vm.StartMonitoringCommand).ExecuteAsync(null);

        vm.SavedProfiles.Should().BeEmpty();
        vm.HasSavedProfiles.Should().BeFalse();
        vm.NoSavedProfiles.Should().BeTrue();
        vm.SavedProfilesSummary.Should().Be("No saved Wi-Fi profiles found on this adapter.");
        vm.SavedProfilesEmptyText.Should().Be("No saved Wi-Fi profiles found on this adapter.");
    }

    [Fact]
    public async Task SavedProfileHygiene_IsBuiltFromSavedProfilesAndVisibleAps()
    {
        var scanner = new FakeWifiScanner
        {
            CurrentConnection = MakeConnectedConnection(),
            SavedProfileNames = ["Office", "Airport Free WiFi", "Coffee Guest"],
            VisibleAccessPoints =
            [
                MakeVisibleAccessPoint(isCurrent: true),
                MakeVisibleAccessPoint(
                    bssid: "AA:BB:CC:00:00:22",
                    security: WifiSecurityProfile.From(Dot11AuthAlgorithm.Open, Dot11CipherAlgorithm.None, false))
                    with { Ssid = "Coffee Guest" },
            ],
        };
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);
        await ((AsyncRelayCommand)vm.StartMonitoringCommand).ExecuteAsync(null);

        vm.HasSavedProfileHygieneItems.Should().BeTrue();
        vm.SavedProfileHygiene.Items.Should().HaveCount(3);
        vm.SavedProfileHygiene.Items.Should().Contain(item =>
            item.ProfileName == "Office" &&
            item.Severity == "OK" &&
            item.State == "Current");
        vm.SavedProfileHygiene.Items.Should().Contain(item =>
            item.ProfileName == "Coffee Guest" &&
            item.Severity == "Warning" &&
            item.Security.Contains("Open", StringComparison.OrdinalIgnoreCase));
        vm.SavedProfileHygiene.Items.Should().Contain(item =>
            item.ProfileName == "Airport Free WiFi" &&
            item.Severity == "Warning");
        vm.SavedProfileHygieneSummary.Should().Contain("warning");
    }

    [Fact]
    public async Task ForgetSavedProfile_RequiresConfirmationBlocksActiveAndRefreshesProfiles()
    {
        var scanner = new FakeWifiScanner
        {
            CurrentConnection = MakeConnectedConnection(),
            SavedProfileNames = ["Office", "OldLab"],
            VisibleAccessPoints = [MakeVisibleAccessPoint(isCurrent: true)],
        };
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);
        await ((AsyncRelayCommand)vm.StartMonitoringCommand).ExecuteAsync(null);

        vm.SelectedSavedProfileHygieneItem = vm.SavedProfileHygiene.Items
            .Single(item => item.ProfileName == "Office");
        vm.ConfirmForgetSavedProfile = true;
        vm.ForgetSelectedSavedProfileCommand.CanExecute(null).Should().BeFalse();
        scanner.ForgottenProfiles.Should().BeEmpty();

        vm.SelectedSavedProfileHygieneItem = vm.SavedProfileHygiene.Items
            .Single(item => item.ProfileName == "OldLab");
        vm.ForgetSelectedSavedProfileCommand.CanExecute(null).Should().BeFalse();
        scanner.ForgottenProfiles.Should().BeEmpty();

        vm.ConfirmForgetSavedProfile = true;
        await ((AsyncRelayCommand)vm.ForgetSelectedSavedProfileCommand).ExecuteAsync(null);

        scanner.ForgottenProfiles.Should().ContainSingle().Which.Should().Be("OldLab");
        vm.SavedProfiles.Select(profile => profile.DisplayName).Should().ContainSingle().Which.Should().Be("Office");
        vm.SelectedSavedProfileHygieneItem.Should().BeNull();
        vm.ConfirmForgetSavedProfile.Should().BeFalse();
        vm.SavedProfileForgetStatus.Should().Contain("Forgot saved profile 'OldLab'");
    }

    [Fact]
    public async Task ActiveScans_RecordChannelHistoryAndBeforeAfterSummary()
    {
        var scanner = new FakeWifiScanner
        {
            CurrentConnection = MakeConnectedConnection(),
            VisibleAccessPoints =
            [
                MakeVisibleAccessPoint(channel: 6, rssi: -58, isCurrent: true),
                MakeVisibleAccessPoint(channel: 6, rssi: -72, bssid: "AA:BB:CC:00:00:02"),
                MakeVisibleAccessPoint(channel: 11, rssi: -80, bssid: "AA:BB:CC:00:00:03"),
            ],
        };
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);
        await ((AsyncRelayCommand)vm.StartMonitoringCommand).ExecuteAsync(null);

        vm.ChannelHistory.Should().ContainSingle();
        vm.HasChannelHistory.Should().BeTrue();
        vm.ChannelBeforeAfterSummary.Title.Should().Be("Baseline captured");

        scanner.CurrentConnection = MakeConnectedConnection() with
        {
            Channel = 11,
            CenterFrequencyMhz = 2462,
            RssiDbm = -51,
            SignalLevel = WifiFrequencyHelper.ClassifySignal(-51),
            CapturedAt = DateTimeOffset.Now,
        };
        scanner.VisibleAccessPoints =
        [
            MakeVisibleAccessPoint(channel: 11, rssi: -51, isCurrent: true),
        ];

        await ((AsyncRelayCommand)vm.ScanNetworksCommand).ExecuteAsync(null);

        vm.ChannelHistory.Should().HaveCount(2);
        vm.ChannelHistory[0].ChannelDisplay.Should().Be("2.4 GHz ch 11");
        vm.ChannelBeforeAfterSummary.Title.Should().Contain("Channel changed");
        vm.ChannelBeforeAfterSummary.After.Should().Contain("ch 11");
        vm.ChannelBeforeAfterSummary.Delta.Should().Contain("RSSI +7 dB");
        vm.ChannelBeforeAfterSummary.SeverityDisplay.Should().Be("OK");
    }

    [Fact]
    public void RouterSnmpProtocolSwitch_ExposesV3CredentialInputs()
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        vm.RouterSelectedSnmpProtocol.Should().Be(SnmpProtocolVersion.V2C);
        vm.IsRouterSnmpV2Selected.Should().BeTrue();
        vm.IsRouterSnmpV3Selected.Should().BeFalse();

        vm.RouterSelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;

        vm.IsRouterSnmpV2Selected.Should().BeFalse();
        vm.IsRouterSnmpV3Selected.Should().BeTrue();
        vm.RouterSnmpAuthProtocols.Should().Contain("SHA256");
        vm.RouterSnmpPrivacyProtocols.Should().Contain("AES128");
    }

    [Fact]
    public void AutoDetectCommand_IsAvailableWithoutManualSourceSetup()
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        vm.AutoDetectWirelessDevicesCommand.CanExecute(null).Should().BeTrue();
        vm.CanAutoDetectWirelessDevices.Should().BeTrue();
    }

    [Fact]
    public void DeviceInventoryFilters_ApplyToWirelessSnapshotOnly()
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        vm.LanDevices.Add(new WifiLanDevice(
            IpAddress: "192.168.0.1",
            MacAddress: "20:B8:2B:6D:0E:61",
            HostName: "Default gateway",
            Vendor: "Router Vendor",
            IsGateway: true,
            IsThisPc: false,
            IsWifiAccessPoint: true,
            AccessPointBssid: "20:B8:2B:6D:0E:61"));
        vm.LanDevices.Add(new WifiLanDevice(
            IpAddress: "192.168.0.20",
            MacAddress: "8C:55:70:B6:F4:27",
            HostName: "LivingRoom-TV",
            Vendor: "Samsung Electronics",
            IsGateway: false,
            IsThisPc: false,
            NameSource: "SSDP / UPnP"));
        vm.LanDevices.Add(new WifiLanDevice(
            IpAddress: "192.168.0.21",
            MacAddress: "D0:96:86:0C:3D:92",
            HostName: null,
            Vendor: "Samsung Electronics",
            IsGateway: false,
            IsThisPc: false,
            NameSource: null));
        vm.LanDevices.Add(new WifiLanDevice(
            IpAddress: "192.168.0.30",
            MacAddress: "AA:BB:CC:DD:EE:FF",
            HostName: "Office-printer",
            Vendor: "Printer Vendor",
            IsGateway: false,
            IsThisPc: false,
            NameSource: "DNS cache"));

        InvokeRebuildDeviceInventory(vm, recordPresence: true);

        vm.DeviceInventory.Select(item => item.IpAddress)
            .Should().Equal("192.168.0.1", "192.168.0.20", "192.168.0.21");
        vm.DeviceInventory.Should().NotContain(item => item.DeviceType == "Printer");

        vm.SetDeviceInventoryFilterCommand.Execute("Confirmed");
        vm.DeviceInventory.Should().ContainSingle()
            .Which.WirelessStatusDisplay.Should().Be("Wi-Fi infrastructure");

        vm.SetDeviceInventoryFilterCommand.Execute("Likely");
        vm.DeviceInventory.Select(item => item.IpAddress)
            .Should().Equal("192.168.0.20", "192.168.0.21");

        vm.SetDeviceInventoryFilterCommand.Execute("Needs name");
        vm.DeviceInventory.Should().ContainSingle()
            .Which.DisplayName.Should().Be("Unnamed device");

        vm.SetDeviceInventoryFilterCommand.Execute("Phones/TV/IoT");
        vm.DeviceInventory.Select(item => item.DeviceType)
            .Should().Equal("TV / Media", "Phone / Tablet");

        vm.SetDeviceInventoryFilterCommand.Execute("bad-filter");
        vm.DeviceInventoryFilter.Should().Be("All");
        vm.DeviceInventory.Should().HaveCount(3);
    }

    [Fact]
    public void ClearDeviceSession_ClearsCurrentEvidenceAndKeepsFiltersReady()
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        vm.LanDevices.Add(new WifiLanDevice(
            IpAddress: "192.168.0.20",
            MacAddress: "8C:55:70:B6:F4:27",
            HostName: "LivingRoom-TV",
            Vendor: "Samsung Electronics",
            IsGateway: false,
            IsThisPc: false,
            NameSource: "SSDP / UPnP"));
        vm.RouterClients.Add(new WifiRouterClient(
            IpAddress: "192.168.0.20",
            MacAddress: "8C:55:70:B6:F4:27",
            HostName: "LivingRoom-TV",
            Vendor: "Samsung Electronics",
            InterfaceIndex: "wlan0",
            IsAlsoDetectedLocally: true,
            Source: "AP wireless client association table",
            IsWirelessAssociation: true));

        InvokeRebuildDeviceInventory(vm, recordPresence: true);
        vm.SelectedDeviceInventoryItem = vm.DeviceInventory.Single();
        vm.SetDeviceInventoryFilterCommand.Execute("Likely");

        vm.ClearDeviceSessionCommand.Execute(null);

        vm.LanDevices.Should().BeEmpty();
        vm.RouterClients.Should().BeEmpty();
        vm.DeviceInventory.Should().BeEmpty();
        vm.SelectedDeviceInventoryItem.Should().BeNull();
        vm.DeviceInventoryFilter.Should().Be("All");
        vm.DeviceInventorySummary.Should().Contain("No wireless clients");
        vm.DeviceInventoryFilterSummary.Should().Contain("Filters are ready");
        vm.LanScanStatus.Should().Contain("Device session cleared");
        vm.RouterClientStatus.Should().Contain("Device session cleared");
    }

    [Fact]
    public void AutoDetectSnmpOptions_FallsBackToPublicV2WhenV3CredentialsAreIncomplete()
    {
        var scanner = new FakeWifiScanner
        {
            CurrentConnection = MakeConnectedConnection()
        };
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        vm.RouterSelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.RouterSnmpV3UserName = "monitor";
        vm.RouterSnmpV3AuthPassword = "";
        vm.RouterSnmpV3PrivacyPassword = "";

        var options = InvokeBuildAutoDetectRouterSnmpOptions(vm);

        options.Protocol.Should().Be(SnmpProtocolVersion.V2C);
        options.Community.Should().Be("public");
        options.TimeoutMs.Should().Be(2500);
    }

    [Fact]
    public async Task ReadRouterClients_SnmpV3MissingCredentials_DoesNotStartSnmpRead()
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        vm.RouterSelectedSnmpProtocol = SnmpProtocolVersion.V3AuthPriv;
        vm.RouterSnmpV3UserName = "monitor";
        vm.RouterSnmpV3AuthPassword = "auth-secret";
        vm.RouterSnmpV3PrivacyPassword = "";

        await ((AsyncRelayCommand)vm.ReadRouterClientsCommand).ExecuteAsync(null);

        vm.RouterClientStatus.Should().Contain("SNMPv3 authPriv requires username");
        vm.IsRouterClientScanning.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshConnection_UpdatesSnapshotWithoutActiveScan()
    {
        var scanner = new FakeWifiScanner
        {
            CurrentConnection = MakeConnectedConnection()
        };
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);

        vm.RefreshConnectionCommand.CanExecute(null).Should().BeTrue();
        await ((AsyncRelayCommand)vm.RefreshConnectionCommand).ExecuteAsync(null);

        scanner.ForceScanRequests.Should().Be(0);
        vm.Connection.Ssid.Should().Be("Office");
        vm.ConnectionStateLabel.Should().Be("Connected");
        vm.ConnectionBssidText.Should().Be("AA:BB:CC:00:00:01");
        vm.ConnectionVendorText.Should().Be("Test Vendor");
        vm.ConnectionSecurityText.Should().Be("WPA2-Personal");
        vm.HiddenSsidText.Should().Be("No");
        vm.Status.Should().Contain("Connection refreshed");
    }

    [Fact]
    public async Task DetectIspCommand_IsEnabledOnlyForConnectedSnapshot()
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);

        vm.DetectIspCommand.CanExecute(null).Should().BeFalse();

        scanner.CurrentConnection = MakeConnectedConnection();
        await ((AsyncRelayCommand)vm.RefreshConnectionCommand).ExecuteAsync(null);

        vm.DetectIspCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void LiveSignalSample_UpdatesDisplayStateAndStats()
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        InvokeSignalSample(vm, -62);
        InvokeSignalSample(vm, -58);

        vm.SignalSamples.Should().HaveCount(2);
        vm.HasLiveSignalSamples.Should().BeTrue();
        vm.LiveSignalValueText.Should().Be("-58 dBm");
        vm.SignalSampleCountText.Should().Be("2/60 samples");
        vm.RssiStats.Should().Contain("min -62");
        vm.RssiStats.Should().Contain("avg -60");
        vm.RssiStats.Should().Contain("max -58");
    }

    [Fact]
    public void LiveSpeedSample_SeparatesPhyRateFromRealThroughput()
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        InvokeSpeedSample(vm, rxRate: 866, txRate: 433, rxThroughput: 1.5, txThroughput: 0.5);

        vm.SpeedSamples.Should().HaveCount(1);
        vm.HasLiveSpeedSamples.Should().BeTrue();
        vm.LiveLinkSpeedValueText.Should().Be("866 / 433 Mbps");
        vm.LinkSpeedRxText.Should().Be("866 Mbps");
        vm.LinkSpeedTxText.Should().Be("433 Mbps");
        vm.LinkSpeedStateText.Should().Be("Not connected");
        vm.LinkSpeedStateColor.Should().Be("#94A3B8");
        vm.LinkSpeedDeltaText.Should().Be("Connect to Wi-Fi to read negotiated PHY rate.");
        vm.LiveThroughputValueText.Should().Be("Rx 1.5 Mbps / Tx 500 Kbps");
        vm.ReceiveThroughputNowText.Should().Be("1.5 Mbps");
        vm.TransferThroughputNowText.Should().Be("500 Kbps");
        vm.ReceiveThroughputStateText.Should().Be("Active traffic");
        vm.TransferThroughputStateText.Should().Be("Light traffic");
        vm.SpeedSampleCountText.Should().Be("1/60 samples");
        vm.ThroughputStats.Should().Contain("Peak Rx 1.5 Mbps / Tx 500 Kbps");
    }

    [Fact]
    public async Task LinkSpeedDisplay_UsesConnectedPhyRateAndDelta()
    {
        var scanner = new FakeWifiScanner
        {
            CurrentConnection = MakeConnectedConnection()
        };
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);
        await ((AsyncRelayCommand)vm.RefreshConnectionCommand).ExecuteAsync(null);

        vm.LinkSpeedRxText.Should().Be("866 Mbps");
        vm.LinkSpeedTxText.Should().Be("866 Mbps");
        vm.LinkSpeedStateText.Should().Be("PHY rate available");
        vm.LinkSpeedStateColor.Should().Be("#10B981");
        vm.LinkSpeedDeltaText.Should().Be("Rx and Tx PHY rates are balanced.");

        InvokeSpeedSample(vm, rxRate: 866, txRate: 433, rxThroughput: 1.2, txThroughput: 0.1);

        vm.LinkSpeedRxText.Should().Be("866 Mbps");
        vm.LinkSpeedTxText.Should().Be("433 Mbps");
        vm.LinkSpeedDeltaText.Should().Be("Rx PHY rate is 433 Mbps higher.");
    }

    [Fact]
    public void ThroughputDisplay_ComputesDirectionStatsAndTrafficState()
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        InvokeSpeedSample(vm, rxRate: 866, txRate: 866, rxThroughput: 1.5, txThroughput: 0.5);

        vm.ReceiveThroughputStatsText.Should().Contain("Now 1.5 Mbps");
        vm.ReceiveThroughputStatsText.Should().Contain("Peak 1.5 Mbps");
        vm.TransferThroughputStatsText.Should().Contain("Now 500 Kbps");
        vm.ReceiveThroughputStateColor.Should().Be("#10B981");
        vm.TransferThroughputStateColor.Should().Be("#3B82F6");

        InvokeSpeedSample(vm, rxRate: 866, txRate: 866, rxThroughput: 0, txThroughput: 25);

        vm.ReceiveThroughputNowText.Should().Be("0 Kbps");
        vm.ReceiveThroughputStatsText.Should().Contain("Avg 750 Kbps");
        vm.ReceiveThroughputStateText.Should().Be("Idle traffic");
        vm.ReceiveThroughputStateColor.Should().Be("#64748B");
        vm.TransferThroughputNowText.Should().Be("25 Mbps");
        vm.TransferThroughputStatsText.Should().Contain("Avg 13 Mbps");
        vm.TransferThroughputStatsText.Should().Contain("Peak 25 Mbps");
        vm.TransferThroughputStateText.Should().Be("Heavy traffic");
        vm.TransferThroughputStateColor.Should().Be("#F59E0B");
    }

    [Fact]
    public async Task SignalAlertThreshold_ClampsAndReportsMargin()
    {
        var scanner = new FakeWifiScanner
        {
            CurrentConnection = MakeConnectedConnection()
        };
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);
        await ((AsyncRelayCommand)vm.RefreshConnectionCommand).ExecuteAsync(null);

        vm.SignalAlertThresholdDbm = -120;
        vm.SignalAlertThresholdDbm.Should().Be(-100);
        vm.SignalMarginText.Should().Contain("above alert threshold");

        vm.SignalAlertThresholdDbm = -10;
        vm.SignalAlertThresholdDbm.Should().Be(-40);
        vm.SignalMarginText.Should().Contain("below alert threshold");
        vm.SignalAlertStatusText.Should().Contain("sound off");
    }

    [Fact]
    public async Task WeakSignal_ShowsBannerButSoundIsOptIn()
    {
        var scanner = new FakeWifiScanner
        {
            CurrentConnection = MakeConnectedConnection()
        };
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);
        await ((AsyncRelayCommand)vm.RefreshConnectionCommand).ExecuteAsync(null);

        vm.SignalAlertSoundEnabled.Should().BeFalse();
        vm.SignalAlertThresholdDbm = -75;
        InvokeSignalSample(vm, -82);

        vm.IsSignalAlerting.Should().BeTrue();
        vm.SignalAlertText.Should().Contain("Signal -82 dBm");
        vm.SignalAlertText.Should().Contain("below your -75 dBm alert");
        vm.SignalMarginText.Should().Be("7 dB below alert threshold");
        vm.SignalAlertStatusText.Should().Contain("sound off");
    }

    [Fact]
    public async Task SignalAlert_ClearsOnlyAfterHysteresisRecovery()
    {
        var scanner = new FakeWifiScanner
        {
            CurrentConnection = MakeConnectedConnection()
        };
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out _);

        await PrimeAdapterAsync(vm);
        await ((AsyncRelayCommand)vm.RefreshConnectionCommand).ExecuteAsync(null);

        vm.SignalAlertThresholdDbm = -75;
        InvokeSignalSample(vm, -82);
        vm.IsSignalAlerting.Should().BeTrue();

        InvokeSignalSample(vm, -73);
        vm.IsSignalAlerting.Should().BeTrue();

        InvokeSignalSample(vm, -72);
        vm.IsSignalAlerting.Should().BeFalse();
        vm.SignalMarginText.Should().Be("3 dB above alert threshold");
    }

    [Fact]
    public async Task QueuedAutoRescan_RunsOnlyWhileMonitoring()
    {
        var scanner = new FakeWifiScanner();
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out var sampler);

        await PrimeAdapterAsync(vm);
        vm.AutoRescanEnabled = true;
        await ((AsyncRelayCommand)vm.StartMonitoringCommand).ExecuteAsync(null);

        scanner.ForceScanRequests.Should().Be(1);

        InvokeQueuedAutoRescan(vm);
        await WaitUntilAsync(() => scanner.ForceScanRequests == 2);

        vm.StopMonitoringCommand.Execute(null);

        vm.IsMonitoring.Should().BeFalse();
        sampler.IsRunning.Should().BeFalse();

        InvokeQueuedAutoRescan(vm);
        await Task.Delay(75);

        scanner.ForceScanRequests.Should().Be(2);
    }

    [Fact]
    public async Task Deactivate_CancelsInFlightInitialScan()
    {
        var scanner = new FakeWifiScanner { HoldForceScanUntilCanceled = true };
        using var temp = new TempDirectory();
        using var history = new WifiHistoryStore(temp.DirectoryPath);
        using var vm = CreateViewModel(scanner, history, out var sampler);

        await PrimeAdapterAsync(vm);
        var startTask = ((AsyncRelayCommand)vm.StartMonitoringCommand).ExecuteAsync(null);

        await scanner.WaitForForceScanAsync();
        vm.Deactivate();
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));

        scanner.ForceScanCancellationObserved.Should().BeTrue();
        vm.IsMonitoring.Should().BeFalse();
        sampler.IsRunning.Should().BeFalse();
        vm.IsScanning.Should().BeFalse();
    }

    private static WifiAnalyzerViewModel CreateViewModel(
        FakeWifiScanner scanner,
        WifiHistoryStore history,
        out WifiSampler sampler)
    {
        var logger = new NullLogger();
        var powerShell = new PowerShellRunner(logger);
        sampler = new WifiSampler(scanner, interval: TimeSpan.FromMinutes(10));

        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            return new WifiAnalyzerViewModel(
                scanner,
                sampler,
                new WifiCapabilityProbe(powerShell, scanner),
                new WifiPublicIpProbe(new HttpClient(new OfflineHttpMessageHandler())),
                new WifiLanScanner(powerShell),
                new WifiRouterClientCollector(new SnmpClientService()),
                new WifiDeviceFriendlyNameStore(new AppStorageService(Path.Combine(history.HistoryDirectory, "FriendlyNames"))),
                new WifiDeviceTypeOverrideStore(new AppStorageService(Path.Combine(history.HistoryDirectory, "DeviceTypes"))),
                new SnmpCredentialStore(new SecureCredentialService(
                    new AppStorageService(Path.Combine(history.HistoryDirectory, "SnmpCredentials")))),
                new WifiAnalyzerEngine(),
                history,
                logger);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static async Task PrimeAdapterAsync(WifiAnalyzerViewModel vm)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await vm.ActivateAsync(cancellation.Token);
    }

    private static void InvokeQueuedAutoRescan(WifiAnalyzerViewModel vm)
    {
        var method = typeof(WifiAnalyzerViewModel).GetMethod(
            "QueueAutoScanFromTimer",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.Invoke(vm, null);
    }

    private static void InvokeSignalSample(WifiAnalyzerViewModel vm, int rssiDbm)
    {
        var method = typeof(WifiAnalyzerViewModel).GetMethod(
            "OnSignalSampled",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.Invoke(vm, [null, new WifiSignalSample(
            DateTimeOffset.Now,
            rssiDbm,
            WifiFrequencyHelper.ClassifySignal(rssiDbm))]);
    }

    private static void InvokeSpeedSample(
        WifiAnalyzerViewModel vm,
        double rxRate,
        double txRate,
        double rxThroughput,
        double txThroughput)
    {
        var method = typeof(WifiAnalyzerViewModel).GetMethod(
            "OnSpeedSampled",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.Invoke(vm, [null, new WifiSpeedSample(
            DateTimeOffset.Now,
            rxRate,
            txRate,
            rxThroughput,
            txThroughput)]);
    }

    private static SnmpSessionOptions InvokeBuildAutoDetectRouterSnmpOptions(WifiAnalyzerViewModel vm)
    {
        var method = typeof(WifiAnalyzerViewModel).GetMethod(
            "BuildAutoDetectRouterSnmpOptions",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        return (SnmpSessionOptions)method!.Invoke(vm, null)!;
    }

    private static void InvokeRebuildDeviceInventory(WifiAnalyzerViewModel vm, bool recordPresence)
    {
        var method = typeof(WifiAnalyzerViewModel).GetMethod(
            "RebuildDeviceInventory",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.Invoke(vm, [recordPresence]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, timeout.Token);
        }
    }

    private static WifiConnectionDetails MakeConnectedConnection() => new(
        IsConnected: true,
        Ssid: "Office",
        IsHiddenSsid: false,
        Bssid: "AA:BB:CC:00:00:01",
        Vendor: "Test Vendor",
        Channel: 6,
        CenterFrequencyMhz: 2437,
        ChannelWidthMhz: 20,
        Band: WifiBand.TwoPointFourGhz,
        PhyType: Dot11PhyType.He,
        RssiDbm: -58,
        SignalLevel: WifiFrequencyHelper.ClassifySignal(-58),
        SignalQualityPercent: 82,
        RxRateMbps: 866,
        TxRateMbps: 866,
        Security: WifiSecurityProfile.From(Dot11AuthAlgorithm.RsnaPsk, Dot11CipherAlgorithm.Ccmp, true),
        ProfileName: "Office",
        InterfaceState: WlanInterfaceState.Connected,
        Ipv4Address: "192.168.1.25",
        Ipv4SubnetMask: "255.255.255.0",
        Ipv4Gateway: "192.168.1.1",
        DnsServers: ["192.168.1.1"],
        AdapterMacAddress: "AA-BB-CC-00-00-01",
        AdapterDescription: "Test Wi-Fi",
        PublicIpAddress: null,
        IspName: null,
        IspLocation: null,
        CapturedAt: DateTimeOffset.Now);

    private static WifiAccessPoint MakeVisibleAccessPoint(
        int channel = 6,
        int rssi = -58,
        string bssid = "AA:BB:CC:00:00:01",
        bool isCurrent = false,
        WifiSecurityProfile? security = null) =>
        new(
            Ssid: "Office",
            IsHidden: false,
            Bssid: bssid,
            Vendor: "Test Vendor",
            Channel: channel,
            CenterFrequencyMhz: channel == 11 ? 2462 : 2437,
            ChannelWidthMhz: 20,
            Band: WifiBand.TwoPointFourGhz,
            PhyType: Dot11PhyType.He,
            RssiDbm: rssi,
            SignalLevel: WifiFrequencyHelper.ClassifySignal(rssi),
            LinkQualityPercent: 82,
            Security: security ?? WifiSecurityProfile.From(Dot11AuthAlgorithm.RsnaPsk, Dot11CipherAlgorithm.Ccmp, true),
            IsCurrentConnection: isCurrent,
            FirstSeenAt: DateTimeOffset.Now,
            LastSeenAt: DateTimeOffset.Now);

    private sealed class FakeWifiScanner : IWifiScanner
    {
        private readonly Guid _adapterId = Guid.NewGuid();
        private readonly TaskCompletionSource _forceScanStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _forceScanRequests;

        public bool IsAvailable => true;
        public bool HoldForceScanUntilCanceled { get; init; }
        public bool ForceScanCancellationObserved { get; private set; }
        public int ForceScanRequests => Volatile.Read(ref _forceScanRequests);
        public WifiConnectionDetails? CurrentConnection { get; set; }
        public IReadOnlyList<WifiAccessPoint>? VisibleAccessPoints { get; set; }
        public List<string> ForgottenProfiles { get; } = [];
        public bool ForgetSavedProfileResult { get; set; } = true;

        public Guid? GetPrimaryAdapterId() => _adapterId;

        public WifiConnectionDetails? GetCurrentConnection(Guid adapterId) =>
            CurrentConnection ?? WifiConnectionDetails.Disconnected(WlanInterfaceState.Disconnected);

        public async Task<bool> ForceScanAsync(Guid adapterId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _forceScanRequests);
            _forceScanStarted.TrySetResult();

            if (HoldForceScanUntilCanceled)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    ForceScanCancellationObserved = true;
                    throw;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        public IReadOnlyList<WifiAccessPoint> GetVisibleAccessPoints(Guid adapterId) =>
            VisibleAccessPoints ?? [MakeVisibleAccessPoint()];

        public IReadOnlyList<string> SavedProfileNames { get; set; } = ["Office"];

        public IReadOnlyList<string> GetSavedProfiles(Guid adapterId) => SavedProfileNames;

        public bool ForgetSavedProfile(Guid adapterId, string profileName)
        {
            ForgottenProfiles.Add(profileName);
            if (!ForgetSavedProfileResult)
            {
                return false;
            }

            SavedProfileNames = SavedProfileNames
                .Where(name => !string.Equals(name, profileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return true;
        }

        public int? GetCurrentRssiDbm(Guid adapterId) => null;

        public Task WaitForForceScanAsync() => _forceScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        public void Dispose() { }
    }

    private sealed class OfflineHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class NullLogger : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "ISG-Desk-WifiAnalyzerViewModelTests-" + Guid.NewGuid().ToString("N"));
        }

        public string DirectoryPath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for locked temp files.
            }
        }
    }
}
