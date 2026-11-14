using System;

namespace AiUsageDock.Core.Layout;

public enum DockEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

public readonly record struct DockRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

public sealed record DockLayoutRequest(
    DockRect Taskbar,
    DockEdge Edge,
    bool AutoHide,
    DockRect Monitor,
    double DpiScale,
    int WidthDip,
    int DesiredHeightDip,
    int MinOnTaskbarHeightDip,
    int OffsetPx,
    int TrayReserveDip,
    int GapDip,
    bool PreferOnTaskbar = true);

public sealed record DockLayoutResult(DockRect Rect, double HeightDip, bool OnTaskbar);

/// <summary>
/// Pure geometry for parking the dock against a taskbar. Kept in Core so it can be unit tested
/// without a window or a real taskbar.
/// </summary>
public static class DockLayout
{
    /// <summary>Taskbars reported shorter than this (physical px) are treated as auto-hidden.</summary>
    public const int HiddenTaskbarThresholdPx = 12;

    public static DockLayoutResult Compute(DockLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scale = request.DpiScale <= 0 ? 1.0 : request.DpiScale;
        var gap = (int)Math.Round(request.GapDip * scale);
        var width = (int)Math.Round(request.WidthDip * scale);
        var desiredHeight = (int)Math.Round(request.DesiredHeightDip * scale);
        var minOnTaskbar = (int)Math.Round(request.MinOnTaskbarHeightDip * scale);
        var tray = (int)Math.Round(request.TrayReserveDip * scale);

        var taskbar = request.Taskbar;
        var monitor = request.Monitor;

        var autoHidden = request.Edge is DockEdge.Bottom or DockEdge.Top
                         && request.AutoHide
                         && taskbar.Height <= HiddenTaskbarThresholdPx;

        var innerHeight = taskbar.Height - 2 * gap;
        var onTaskbar = request.PreferOnTaskbar
                        && !autoHidden
                        && innerHeight >= minOnTaskbar
                        && innerHeight > 0;

        int height;
        int top;

        switch (request.Edge)
        {
            case DockEdge.Top:
                height = onTaskbar ? innerHeight : desiredHeight;
                top = onTaskbar ? taskbar.Top + gap : taskbar.Bottom + gap;
                break;

            case DockEdge.Left:
            case DockEdge.Right:
                // A horizontal strip does not fit inside a vertical taskbar; park it at the
                // bottom of the monitor, beside the bar.
                onTaskbar = false;
                height = desiredHeight;
                top = monitor.Bottom - height - gap;
                break;

            default: // Bottom
                height = onTaskbar ? innerHeight : desiredHeight;
                top = onTaskbar ? taskbar.Top + gap : taskbar.Top - height - gap;
                break;
        }

        var left = request.Edge switch
        {
            DockEdge.Left => taskbar.Right + gap,
            DockEdge.Right => taskbar.Left - width - gap,
            _ => taskbar.Right - tray - width + request.OffsetPx,
        };

        var minLeft = monitor.Left + gap;
        var maxLeft = Math.Max(minLeft, monitor.Right - width - gap);
        left = Math.Clamp(left, minLeft, maxLeft);

        var minTop = monitor.Top + gap;
        var maxTop = Math.Max(minTop, monitor.Bottom - height - gap);
        top = Math.Clamp(top, minTop, maxTop);

        return new DockLayoutResult(new DockRect(left, top, width, height), height / scale, onTaskbar);
    }
}
