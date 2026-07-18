using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace LarsCloud.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly DrawingIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _pauseItem;

    public TrayService(Action showWindow, Action syncNow, Action openLocal, Action openDrive,
        Action togglePause, Action exit, bool isPaused)
    {
        _trayIcon = LoadApplicationIcon();
        _icon = new Forms.NotifyIcon
        {
            Text = "Lar's Cloud",
            Icon = _trayIcon,
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Відкрити Lar’s Cloud", null, (_, _) => showWindow());
        menu.Items.Add("Синхронізувати зараз", null, (_, _) => syncNow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Відкрити локальну папку", null, (_, _) => openLocal());
        menu.Items.Add("Відкрити Google Drive", null, (_, _) => openDrive());
        _pauseItem = new Forms.ToolStripMenuItem(isPaused ? "Відновити синхронізацію" : "Призупинити синхронізацію", null,
            (_, _) => togglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Вийти з програми", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => showWindow();
    }

    public void SetPaused(bool paused) => _pauseItem.Text = paused ? "Відновити синхронізацію" : "Призупинити синхронізацію";

    public void Notify(string title, string message, bool isError = false)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = isError ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _trayIcon.Dispose();
    }

    private static DrawingIcon LoadApplicationIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
            if (resource?.Stream is not null)
            {
                using var embedded = new DrawingIcon(resource.Stream);
                return (DrawingIcon)embedded.Clone();
            }
        }
        catch (Exception) { }

        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                var executableIcon = DrawingIcon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (executableIcon is not null) return executableIcon;
            }
        }
        catch (Exception) { }

        return (DrawingIcon)System.Drawing.SystemIcons.Application.Clone();
    }
}
