using System.Windows;
using NetScopeDiagnosticCenter.UI.ViewModels;

namespace NetScopeDiagnosticCenter.UI;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DataContext = new AboutViewModel();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
