using System.Windows;
using WiiHub.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace WiiHub.App;

/// <summary>
/// The notification-area (tray) icon: shows controller status, lets the user open or exit the app,
/// and shows Windows notifications.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const string AppName = "Wii Guitar Hub";

    private readonly Forms.NotifyIcon _icon;
    private readonly MainViewModel _viewModel;
    private readonly Action _open;
    private readonly Action _exit;
    private readonly Drawing.Icon _appIcon;
    private bool _disposed;

    public TrayIcon(MainViewModel viewModel, Action open, Action exit)
    {
        _viewModel = viewModel;
        _open = open;
        _exit = exit;
        _appIcon = LoadAppIcon();

        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, e) =>
        {
            BuildMenu(menu);
            e.Cancel = false;
        };
        BuildMenu(menu);

        _icon = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _open();
        };
        _icon.BalloonTipClicked += (_, _) => _open();
    }

    /// <summary>Keeps the tooltip up to date (call every second or so).</summary>
    public void Refresh()
    {
        if (_disposed) return;
        string text = $"{AppName} — {_viewModel.TraySummary}";
        _icon.Text = text.Length > 63 ? text[..63] : text; // Windows limit
    }

    /// <summary>
    /// Shows a Windows notification. Windows shows it as a toast and keeps it in the notification
    /// centre; during fullscreen games "Do not disturb" may hold it back until the game ends.
    /// </summary>
    public void ShowNotification(string title, string message, bool warning = false)
    {
        if (_disposed) return;
        _icon.ShowBalloonTip(8000, title, message, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    private void BuildMenu(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var title = new Forms.ToolStripMenuItem(AppName) { Enabled = false };
        title.Font = new Drawing.Font(title.Font, Drawing.FontStyle.Bold);
        menu.Items.Add(title);

        if (_viewModel.Controllers.Count == 0)
        {
            menu.Items.Add(new Forms.ToolStripMenuItem("No controllers paired") { Enabled = false });
        }
        foreach (var c in _viewModel.Controllers)
        {
            string state = c.IsConnected
                ? $"Player {c.PlayerIndex}{(c.HasBattery ? $" · {c.BatteryText}" : "")}"
                : "off";
            menu.Items.Add(new Forms.ToolStripMenuItem($"{(c.IsConnected ? "●" : "○")}  {c.Name} — {state}") { Enabled = false });
        }

        menu.Items.Add(new Forms.ToolStripSeparator());

        var open = new Forms.ToolStripMenuItem("Open Wii Guitar Hub", null, (_, _) => _open());
        open.Font = new Drawing.Font(open.Font, Drawing.FontStyle.Bold);
        menu.Items.Add(open);

        var turnOffAll = new Forms.ToolStripMenuItem("Turn off all Wiimotes", null,
            (_, _) => _viewModel.TurnOffAllCommand.Execute(null))
        {
            Enabled = _viewModel.TurnOffAllCommand.CanExecute(null),
        };
        menu.Items.Add(turnOffAll);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => _exit()));
    }

    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/wii-guitar-hub.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception)
        {
            // Fall back to the generic application icon below.
        }
        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false; // otherwise a "ghost" icon stays until the mouse hovers over it
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
