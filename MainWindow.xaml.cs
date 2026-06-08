using System.Windows;
using NetScopeDiagnosticCenter.UI;

namespace NetScopeDiagnosticCenter;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>
    /// Resolved by the DI container. The previous constructor that wired up every
    /// service by hand has been replaced by service registrations in App.xaml.cs.
    /// </summary>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void OpenReportClick(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenPath(_viewModel.LastReportPath);
    }

    private void OpenWlanReportClick(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenPath(_viewModel.LastWlanReportPath);
    }

    private void OpenTextSummaryClick(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenPath(_viewModel.LastTextSummaryPath);
    }

    private void OpenRawJsonClick(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenPath(_viewModel.LastRawJsonPath);
    }

    private void SnmpAuthPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.SnmpV3AuthPassword = passwordBox.Password;
        }
    }

    private void SnmpPrivacyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.SnmpV3PrivacyPassword = passwordBox.Password;
        }
    }

    private void NetworkDeviceSnmpAuthPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.NetworkDeviceSnmpV3AuthPassword = passwordBox.Password;
        }
    }

    private void NetworkDeviceSnmpPrivacyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.NetworkDeviceSnmpV3PrivacyPassword = passwordBox.Password;
        }
    }

    private void WifiRouterSnmpAuthPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.WifiAnalyzer.RouterSnmpV3AuthPassword = passwordBox.Password;
        }
    }

    private void WifiRouterSnmpPrivacyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.WifiAnalyzer.RouterSnmpV3PrivacyPassword = passwordBox.Password;
        }
    }

    private void ClearNetworkDeviceSnmpPasswordBoxesClick(object sender, RoutedEventArgs e)
    {
        NetworkDeviceSnmpAuthPasswordBox.Password = string.Empty;
        NetworkDeviceSnmpPrivacyPasswordBox.Password = string.Empty;
    }

    private void ClearWifiRouterSnmpPasswordBoxesClick(object sender, RoutedEventArgs e)
    {
        WifiRouterSnmpAuthPasswordBox.Password = string.Empty;
        WifiRouterSnmpPrivacyPasswordBox.Password = string.Empty;
    }

    private void OnLogoClick(object sender, RoutedEventArgs e)
    {
        ShowInfoWindow();
    }

    private void OnInfoClick(object sender, RoutedEventArgs e)
    {
        ShowInfoWindow();
    }

    private void ShowInfoWindow()
    {
        new UI.AboutWindow { Owner = this }.ShowDialog();
    }

    // Custom-chrome window-control handlers.

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
