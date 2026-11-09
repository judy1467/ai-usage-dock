using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AiUsageDock.App.Shell;

/// <summary>
/// Tray icon + context menu. Owns a runtime-drawn icon (no external asset required).
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;

    public event Action? OpenRequested;
    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayIconHost()
    {
        _icon = CreateIcon();

        _menu = new ContextMenuStrip();
        _menu.Items.Add("열기", null, (_, _) => OpenRequested?.Invoke());
        _menu.Items.Add("새로고침", null, (_, _) => RefreshRequested?.Invoke());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("설정...", null, (_, _) => SettingsRequested?.Invoke());
        _menu.Items.Add("종료", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "AI Usage Dock",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenRequested?.Invoke();
            }
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text is limited to 63 characters.
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var path = RoundedRect(new Rectangle(2, 2, 28, 28), 8);
            using var background = new SolidBrush(Color.FromArgb(30, 30, 34));
            g.FillPath(background, path);

            using var border = new Pen(Color.FromArgb(90, 140, 255), 2f);
            g.DrawPath(border, path);

            using var green = new SolidBrush(Color.FromArgb(63, 185, 80));
            using var amber = new SolidBrush(Color.FromArgb(210, 153, 34));
            g.FillRectangle(green, 8, 16, 4, 9);
            g.FillRectangle(green, 14, 11, 4, 14);
            g.FillRectangle(amber, 20, 7, 4, 18);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
