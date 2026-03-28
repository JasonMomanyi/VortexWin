using System.Windows;
using Wpf.Ui.Controls;
using Wpf.Ui.Appearance;
using VortexWin.Core.Ipc;

namespace VortexWin.Settings;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        
        SystemThemeWatcher.Watch(this);

        RootNavigation.SelectionChanged += RootNavigation_SelectionChanged;

        Loaded += async (s, e) => await RefreshStatusAsync();
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is NavigationViewItem navItem)
        {
            string tag = navItem.Tag?.ToString() ?? "";
            
            if (tag == "dashboard")
            {
                DashboardPanel.Visibility = Visibility.Visible;
                PlaceholderPanel.Visibility = Visibility.Collapsed;
                _ = RefreshStatusAsync();
            }
            else
            {
                DashboardPanel.Visibility = Visibility.Collapsed;
                PlaceholderPanel.Visibility = Visibility.Visible;
                txtPlaceholder.Text = navItem.Content?.ToString() ?? "Placeholder";
            }
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
    }

    private void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show("Test logic will be implemented here. Sandboxed dry-run of Challenge.", "Test Challenge", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            using var client = new IpcClient();
            var statusDto = await client.GetStatusAsync();

            if (statusDto != null)
            {
                txtStatus.Text = $"State: {statusDto.State}";
                txtStatus.Foreground = statusDto.State == ServiceState.Secured ? 
                    System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Goldenrod;

                txtCountdown.Text = statusDto.RemainingSeconds > 0 
                    ? $"{statusDto.RemainingSeconds} / {statusDto.TotalSeconds} seconds remaining" 
                    : "Not running";
            }
            else
            {
                txtStatus.Text = "Status: Service Disconnected / Error";
                txtStatus.Foreground = System.Windows.Media.Brushes.Salmon;
            }
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error: {ex.Message}";
        }
    }
}