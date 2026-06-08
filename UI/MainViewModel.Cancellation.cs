using System.Windows.Input;

namespace NetScopeDiagnosticCenter.UI;

public sealed partial class MainViewModel
{
    private void CancelCurrentOperation()
    {
        if (!IsBusy)
        {
            return;
        }

        if (IsDiagnosisRunning)
        {
            ExecuteIfAvailable(CancelDiagnoseCommand);
            return;
        }

        if (IsPortTestRunning)
        {
            ExecuteIfAvailable(CancelPortTestCommand);
            return;
        }

        if (IsLinkQualityRunning)
        {
            ExecuteIfAvailable(CancelLinkQualityCommand);
            return;
        }

        if (IsTestRunning)
        {
            ExecuteIfAvailable(CancelTestCommand);
            return;
        }

        if (IsShareDiscoveryRunning)
        {
            ExecuteIfAvailable(CancelShareDiscoveryCommand);
            return;
        }

        if (IsDomainDiscoveryRunning)
        {
            ExecuteIfAvailable(CancelDomainDiscoveryCommand);
            return;
        }

        if (IsServiceDiscoveryRunning)
        {
            ExecuteIfAvailable(CancelServiceDiscoveryCommand);
            return;
        }

        if (IsPrinterScanRunning)
        {
            ExecuteIfAvailable(CancelPrinterScanCommand);
            return;
        }

        if (IsPrinterSnmpIdentifyRunning)
        {
            ExecuteIfAvailable(CancelPrinterSnmpIdentifyCommand);
            return;
        }

        if (IsNetworkDeviceScanRunning)
        {
            ExecuteIfAvailable(CancelNetworkDeviceScanCommand);
            return;
        }

        if (Reports?.IsWlanReportGenerating == true)
        {
            Reports.CancelActiveReportOperation();
            return;
        }

        StatusMessage = "The current operation does not expose cancellation.";
    }

    private static void ExecuteIfAvailable(ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
