using System.ComponentModel;
using System.Windows;
using LarsCloud.ViewModels;

namespace LarsCloud;

public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void MinimizeWindow_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindow_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeWindowButton is null) return;
        MaximizeWindowButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        MaximizeWindowButton.ToolTip = WindowState == WindowState.Maximized ? "Відновити" : "Розгорнути";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void AllowClose() => _allowClose = true;
}
