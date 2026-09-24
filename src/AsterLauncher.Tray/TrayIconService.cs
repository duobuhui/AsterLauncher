using System.Drawing;
using System.Windows.Forms;

namespace AsterLauncher.Tray;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;

    public TrayIconService()
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("打开 AsterLauncher", null, (_, _) => RestoreRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon = new NotifyIcon
        {
            Text = "AsterLauncher",
            Icon = SystemIcons.Application,
            ContextMenuStrip = _menu
        };
        _icon.DoubleClick += (_, _) => RestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? RestoreRequested;
    public event EventHandler? ExitRequested;

    public void Show() => _icon.Visible = true;

    public void Hide() => _icon.Visible = false;

    public void Dispose()
    {
        Hide();
        _icon.Dispose();
        _menu.Dispose();
    }
}
