using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace LarsCloud.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pauseItem;

    public TrayService(Action showWindow, Action syncNow, Action openLocal, Action openDrive,
        Action togglePause, Action exit, bool isPaused)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        _icon = new Forms.NotifyIcon
        {
            Text = "Lar's Cloud",
            Icon = File.Exists(iconPath) ? new DrawingIcon(iconPath) : System.Drawing.SystemIcons.Application,
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
    }
}
