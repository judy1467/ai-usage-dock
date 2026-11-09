using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using AiUsageDock.Core.Layout;

namespace AiUsageDock.App.Interop;

internal sealed record TaskbarInfo(
    IntPtr Handle,
    DockRect Rect,
    DockEdge Edge,
    bool AutoHide,
    DockRect Monitor,
    string MonitorDevice,
    bool IsPrimary)
{
    public string DisplayName => IsPrimary
        ? $"주 모니터 ({MonitorDevice})"
        : $"보조 모니터 ({MonitorDevice})";
}

/// <summary>
/// Enumerates the primary (Shell_TrayWnd) and secondary (Shell_SecondaryTrayWnd) taskbars.
/// </summary>
internal static class TaskbarLocator
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

    public static IReadOnlyList<TaskbarInfo> GetTaskbars()
    {
        var result = new List<TaskbarInfo>();
        var autoHide = IsAutoHide();

        AddTaskbar(NativeMethods.FindWindow(PrimaryTaskbarClass, null), autoHide, isPrimary: true, result);

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var className = new StringBuilder(64);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            if (string.Equals(className.ToString(), SecondaryTaskbarClass, StringComparison.Ordinal))
            {
                AddTaskbar(hwnd, autoHide, isPrimary: false, result);
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    public static TaskbarInfo? Select(IReadOnlyList<TaskbarInfo> taskbars, string? monitor)
    {
        if (taskbars.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(monitor) &&
            !string.Equals(monitor, "primary", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var taskbar in taskbars)
            {
                if (string.Equals(taskbar.MonitorDevice, monitor, StringComparison.OrdinalIgnoreCase) ||
                    taskbar.MonitorDevice.EndsWith(monitor, StringComparison.OrdinalIgnoreCase))
                {
                    return taskbar;
                }
            }
        }

        foreach (var taskbar in taskbars)
        {
            if (taskbar.IsPrimary)
            {
                return taskbar;
            }
        }

        return taskbars[0];
    }

    private static void AddTaskbar(IntPtr hwnd, bool autoHide, bool isPrimary, List<TaskbarInfo> target)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var windowRect))
        {
            return;
        }

        var monitorHandle = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
            szDevice = string.Empty,
        };

        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return;
        }

        var taskbarRect = new DockRect(
            windowRect.Left,
            windowRect.Top,
            windowRect.Right - windowRect.Left,
            windowRect.Bottom - windowRect.Top);

        var monitorRect = new DockRect(
            monitorInfo.rcMonitor.Left,
            monitorInfo.rcMonitor.Top,
            monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left,
            monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top);

        target.Add(new TaskbarInfo(
            hwnd,
            taskbarRect,
            InferEdge(taskbarRect, monitorRect),
            autoHide,
            monitorRect,
            string.IsNullOrWhiteSpace(monitorInfo.szDevice) ? "unknown" : monitorInfo.szDevice,
            isPrimary));
    }

    private static DockEdge InferEdge(DockRect taskbar, DockRect monitor)
    {
        var top = Math.Abs(taskbar.Top - monitor.Top);
        var bottom = Math.Abs(monitor.Bottom - taskbar.Bottom);
        var left = Math.Abs(taskbar.Left - monitor.Left);
        var right = Math.Abs(monitor.Right - taskbar.Right);

        // A bottom taskbar is the common case; tie-break toward bottom.
        if (bottom <= top && bottom <= left && bottom <= right)
        {
            return DockEdge.Bottom;
        }

        if (top <= left && top <= right)
        {
            return DockEdge.Top;
        }

        return left <= right ? DockEdge.Left : DockEdge.Right;
    }

    private static bool IsAutoHide()
    {
        var data = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
        };

        return NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref data) != UIntPtr.Zero
               && (data.lParam.ToInt64() & NativeMethods.ABS_AUTOHIDE) != 0;
    }
}
