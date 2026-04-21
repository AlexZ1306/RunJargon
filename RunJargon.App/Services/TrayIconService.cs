using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;

namespace RunJargon.App.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _disposed;

    internal event EventHandler? OpenRequested;
    internal event EventHandler? ExitRequested;

    internal TrayIconService(string iconPath, string toolTipText)
    {
        var openItem = new Forms.ToolStripMenuItem("Открыть");
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new Forms.ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(iconPath),
            Text = NormalizeToolTip(toolTipText),
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void ShowBalloonTip(string title, string text)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var icon = _notifyIcon.Icon;
        var contextMenu = _notifyIcon.ContextMenuStrip;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        icon?.Dispose();
        contextMenu?.Dispose();
    }

    private static Icon LoadIcon(string iconPath)
    {
        if (File.Exists(iconPath))
        {
            return new Icon(iconPath);
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static string NormalizeToolTip(string text)
    {
        var normalized = string.IsNullOrWhiteSpace(text) ? "Run Jargon" : text.Trim();
        return normalized.Length <= 63
            ? normalized
            : normalized[..63];
    }
}
