using System;
using AiUsageDock.Core.Layout;
using Xunit;

namespace AiUsageDock.Core.Tests;

public class DockLayoutTests
{
    // 3840x2160 @150%, taskbar 72px tall at the bottom, tray reserve 110 DIP.
    private static DockLayoutRequest BottomRequest(int taskbarHeight, bool autoHide = false, double scale = 1.5, int offsetPx = 0)
        => new(
            Taskbar: new DockRect(0, 2160 - taskbarHeight, 3840, taskbarHeight),
            Edge: DockEdge.Bottom,
            AutoHide: autoHide,
            Monitor: new DockRect(0, 0, 3840, 2160),
            DpiScale: scale,
            WidthDip: 428,
            DesiredHeightDip: 46,
            MinOnTaskbarHeightDip: 30,
            OffsetPx: offsetPx,
            TrayReserveDip: 110,
            GapDip: 4);

    [Fact]
    public void Bottom_TallTaskbar_ParksInsideTaskbar()
    {
        var result = DockLayout.Compute(BottomRequest(taskbarHeight: 72));

        Assert.True(result.OnTaskbar);
        Assert.Equal(60, result.Rect.Height);          // 72 - 2*6
        Assert.Equal(40.0, result.HeightDip, 3);       // 60 / 1.5
        Assert.Equal(2160 - 72 + 6, result.Rect.Top);  // 6px inside the top edge
        Assert.Equal(428 * 1.5, result.Rect.Width, 0);
        Assert.Equal(3840 - 165 - 642, result.Rect.Left); // tray reserve 110 DIP * 1.5 = 165
    }

    [Fact]
    public void Bottom_AutoHiddenTaskbar_SitsAboveTheRevealStrip()
    {
        var result = DockLayout.Compute(BottomRequest(taskbarHeight: 2, autoHide: true));

        Assert.False(result.OnTaskbar);
        Assert.Equal(46 * 1.5, result.Rect.Height, 0);
        Assert.Equal(2160 - 2 - 69 - 6, result.Rect.Top);
    }

    [Fact]
    public void Bottom_AutoHideButRevealed_ParksInsideTaskbar()
    {
        var result = DockLayout.Compute(BottomRequest(taskbarHeight: 72, autoHide: true));

        Assert.True(result.OnTaskbar);
        Assert.Equal(2160 - 72 + 6, result.Rect.Top);
    }

    [Fact]
    public void Bottom_NormalOffset_ShiftsLeft()
    {
        var normal = DockLayout.Compute(BottomRequest(taskbarHeight: 72, offsetPx: 0));
        var shifted = DockLayout.Compute(BottomRequest(taskbarHeight: 72, offsetPx: -200));

        Assert.Equal(normal.Rect.Left - 200, shifted.Rect.Left);
    }

    [Fact]
    public void Bottom_LargePositiveOffset_IsClampedToMonitor()
    {
        var result = DockLayout.Compute(BottomRequest(taskbarHeight: 72, offsetPx: 100_000));

        Assert.InRange(result.Rect.Left, 0, 3840 - result.Rect.Width);
        Assert.Equal(3840 - result.Rect.Width - 6, result.Rect.Left);
    }
    [Fact]
    public void TopEdge_TaskbarAtTop_ParksBelowIt()
    {
        var request = new DockLayoutRequest(
            Taskbar: new DockRect(0, 0, 3840, 72),
            Edge: DockEdge.Top,
            AutoHide: false,
            Monitor: new DockRect(0, 0, 3840, 2160),
            DpiScale: 1.5,
            WidthDip: 428,
            DesiredHeightDip: 46,
            MinOnTaskbarHeightDip: 30,
            OffsetPx: 0,
            TrayReserveDip: 110,
            GapDip: 4);

        var result = DockLayout.Compute(request);

        Assert.True(result.OnTaskbar);
        Assert.Equal(6, result.Rect.Top);
    }

    [Fact]
    public void LeftEdge_ParksBesideVerticalTaskbar()
    {
        var request = new DockLayoutRequest(
            Taskbar: new DockRect(0, 0, 72, 2160),
            Edge: DockEdge.Left,
            AutoHide: false,
            Monitor: new DockRect(0, 0, 3840, 2160),
            DpiScale: 1.5,
            WidthDip: 428,
            DesiredHeightDip: 46,
            MinOnTaskbarHeightDip: 30,
            OffsetPx: 0,
            TrayReserveDip: 110,
            GapDip: 4);

        var result = DockLayout.Compute(request);

        Assert.False(result.OnTaskbar);
        Assert.Equal(72 + 6, result.Rect.Left);
        Assert.Equal(2160 - 69 - 6, result.Rect.Top);
    }
}
