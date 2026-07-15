using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LarsCloud.ViewModels;

namespace LarsCloud;

public partial class MainWindow : Window
{
    private const int WindowNonClientHitTestMessage = 0x0084;
    private const int HitTestClient = 1;
    private const int HitTestLeft = 10;
    private const int HitTestRight = 11;
    private const int HitTestTop = 12;
    private const int HitTestTopLeft = 13;
    private const int HitTestTopRight = 14;
    private const int HitTestBottom = 15;
    private const int HitTestBottomLeft = 16;
    private const int HitTestBottomRight = 17;
    private const double ResizeBorderSize = 7;

    private bool _allowClose;
    private HwndSource? _windowSource;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private void MinimizeWindow_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindow_OnClick(object sender, RoutedEventArgs e) => ToggleMaximizeState();

    private void CloseWindow_OnClick(object sender, RoutedEventArgs e) => Close();

    private void TitleBarDragArea_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeState();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed && WindowState == WindowState.Normal)
            DragMove();
    }

    private void ToggleMaximizeState() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_OnStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeWindowButton is null) return;
        if (WindowState == WindowState.Maximized)
        {
            MaxWidth = SystemParameters.WorkArea.Width;
            MaxHeight = SystemParameters.WorkArea.Height;
        }
        else
        {
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
        }
        MaximizeWindowButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        MaximizeWindowButton.ToolTip = WindowState == WindowState.Maximized ? "Відновити" : "Розгорнути";
    }

    private IntPtr WindowMessageHook(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WindowNonClientHitTestMessage || WindowState != WindowState.Normal)
            return IntPtr.Zero;

        var value = lParam.ToInt64();
        var screenPoint = new Point(unchecked((short)(value & 0xFFFF)), unchecked((short)((value >> 16) & 0xFFFF)));
        var hitTest = GetResizeHitTest(PointFromScreen(screenPoint));
        if (hitTest == HitTestClient) return IntPtr.Zero;

        handled = true;
        return new IntPtr(hitTest);
    }

    private int GetResizeHitTest(Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight)
            return HitTestClient;

        var left = point.X <= ResizeBorderSize;
        var right = point.X >= ActualWidth - ResizeBorderSize;
        var top = point.Y <= ResizeBorderSize;
        var bottom = point.Y >= ActualHeight - ResizeBorderSize;

        if (top && left) return HitTestTopLeft;
        if (top && right) return HitTestTopRight;
        if (bottom && left) return HitTestBottomLeft;
        if (bottom && right) return HitTestBottomRight;
        if (left) return HitTestLeft;
        if (right) return HitTestRight;
        if (top) return HitTestTop;
        if (bottom) return HitTestBottom;
        return HitTestClient;
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

    protected override void OnClosed(EventArgs e)
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }
        base.OnClosed(e);
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
