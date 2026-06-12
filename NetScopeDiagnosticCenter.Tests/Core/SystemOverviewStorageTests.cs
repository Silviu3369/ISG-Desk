using NetScopeDiagnosticCenter.Core.Models;

namespace NetScopeDiagnosticCenter.Tests.Core;

public class SystemOverviewStorageTests
{
    private const long Gib = 1024L * 1024 * 1024;

    // ---------------------------------------------------------------- disks

    [Theory]
    [InlineData("Healthy", "OK")]
    [InlineData("Warning", "Warning")]
    [InlineData("Unhealthy", "Critical")]
    [InlineData("Unknown", "Unknown")]
    [InlineData("", "Unknown")]
    public void DiskSeverity_MapsHealthStatus(string health, string expected)
    {
        new SystemOverviewDisk { HealthStatus = health }.Severity.Should().Be(expected);
    }

    [Fact]
    public void DiskSeverity_FailurePrediction_OverridesHealthyStatus()
    {
        var disk = new SystemOverviewDisk { HealthStatus = "Healthy", FailurePredicted = true };

        disk.Severity.Should().Be("Critical");
        disk.HealthDisplay.Should().Be("Failure predicted");
    }

    [Theory]
    [InlineData(4, "OK")]
    [InlineData(79, "OK")]
    [InlineData(80, "Warning")]
    [InlineData(94, "Warning")]
    [InlineData(95, "Critical")]
    [InlineData(100, "Critical")]
    public void DiskSeverity_SsdWearBands_EscalateEvenWhenHealthy(int wear, string expected)
    {
        var disk = new SystemOverviewDisk
        {
            HealthStatus = "Healthy",
            SmartAvailable = true,
            WearPercent = wear,
        };

        disk.Severity.Should().Be(expected);
    }

    [Fact]
    public void DiskTypeLine_JoinsKnownParts_AndSkipsUnknown()
    {
        var ssd = new SystemOverviewDisk
        {
            MediaType = "SSD",
            BusType = "NVMe",
            Size = "477 GB",
            SerialNumber = "S123",
        };
        ssd.TypeLine.Should().Be("SSD · NVMe · 477 GB · SN S123");

        var hdd = new SystemOverviewDisk
        {
            MediaType = "HDD",
            BusType = "SATA",
            Size = "1 TB",
            SerialNumber = "Unknown",
            SpindleRpm = 7200,
        };
        hdd.TypeLine.Should().Be("HDD · SATA · 1 TB · 7200 RPM");

        new SystemOverviewDisk().TypeLine.Should().Be("Unknown");
    }

    [Fact]
    public void DiskSmartDetail_WithoutCounters_PointsAtAdminRights()
    {
        var disk = new SystemOverviewDisk { SmartAvailable = false };

        disk.SmartDetail.Should().Contain("administrator");
    }

    [Fact]
    public void DiskSmartDetail_WithCounters_ShowsTemperatureWearAndHours()
    {
        var disk = new SystemOverviewDisk
        {
            SmartAvailable = true,
            TemperatureC = 34,
            WearPercent = 4,
            PowerOnHours = 1234,
        };

        disk.SmartDetail.Should().Contain("34 °C");
        disk.SmartDetail.Should().Contain("wear 4% used");
        disk.SmartDetail.Should().Contain("h powered on");
    }

    [Fact]
    public void DiskSmartDetail_CountersReadableButEmpty_SaysNoData()
    {
        new SystemOverviewDisk { SmartAvailable = true }.SmartDetail.Should().Contain("no data");
    }

    // ---------------------------------------------------------------- volumes

    [Fact]
    public void VolumeUsedPercent_ComputesFromBytes()
    {
        var volume = new SystemOverviewVolume { TotalBytes = 500 * Gib, FreeBytes = 250 * Gib };

        volume.UsedPercent.Should().BeApproximately(50.0, 0.01);
        volume.FreePercent.Should().BeApproximately(50.0, 0.01);
    }

    [Fact]
    public void VolumeSeverity_HealthyFreeSpace_IsOk()
    {
        new SystemOverviewVolume { TotalBytes = 500 * Gib, FreeBytes = 250 * Gib }
            .Severity.Should().Be("OK");
    }

    [Fact]
    public void VolumeSeverity_LowFreePercent_IsWarning()
    {
        // 8 % free on a 500 GiB volume (40 GiB) — above the GiB floors, below 12 %.
        new SystemOverviewVolume { TotalBytes = 500 * Gib, FreeBytes = 40 * Gib }
            .Severity.Should().Be("Warning");
    }

    [Fact]
    public void VolumeSeverity_VeryLowFreePercent_IsCritical()
    {
        // 3 % free on a 500 GiB volume — 15 GiB left, Windows updates start failing.
        new SystemOverviewVolume { TotalBytes = 500 * Gib, FreeBytes = 15 * Gib }
            .Severity.Should().Be("Critical");
    }

    [Fact]
    public void VolumeSeverity_AbsoluteFloors_TriggerEvenWhenPercentLooksFine()
    {
        // 14 GiB free of 100 GiB is 14 % — percent-healthy, but below the 15 GiB floor.
        new SystemOverviewVolume { TotalBytes = 100 * Gib, FreeBytes = 14 * Gib }
            .Severity.Should().Be("Warning");
        // 4 GiB free of 50 GiB is 8 % — Warning by percent, but the 5 GiB floor makes it Critical.
        new SystemOverviewVolume { TotalBytes = 50 * Gib, FreeBytes = 4 * Gib }
            .Severity.Should().Be("Critical");
    }

    [Fact]
    public void VolumeSeverity_NoSize_IsUnknown()
    {
        new SystemOverviewVolume().Severity.Should().Be("Unknown");
    }

    [Fact]
    public void VolumeUsageText_FormatsGigabytesAndTerabytes()
    {
        var gb = new SystemOverviewVolume { TotalBytes = 500 * Gib, FreeBytes = 250 * Gib };
        gb.UsageText.Should().Contain("GB free of");
        gb.UsageText.Should().Contain("(50% free)");

        var tb = new SystemOverviewVolume { TotalBytes = 2048 * Gib, FreeBytes = 1024 * Gib };
        tb.UsageText.Should().Contain("TB");
    }
}
