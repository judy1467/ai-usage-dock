using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsageDock.App.Interop;
using AiUsageDock.App.Shell;
using AiUsageDock.Core.Layout;
using AiUsageDock.Core.Models;
using AiUsageDock.Core.Providers.Antigravity;
using AiUsageDock.Core.Providers.DeepSeek;
using AiUsageDock.Core.Scheduling;
using AiUsageDock.Core.Storage;

namespace AiUsageDock.App;

public partial class MainWindow : Window
{
    private const int DockHeightDip = 44;
    private const int TrayReserveDip = 110;
    private const int MinOnTaskbarHeightDip = 30;

    private readonly SettingsStore _settingsStore;
    private readonly FileSecretStore _secretStore;
    private readonly HistoryStore _historyStore;
    private readonly CacheStore _cacheStore;
    private readonly UsageStore _store = new();
    private readonly DeepSeekAdapter _deepSeek;
    private readonly AntigravityAdapter _antigravity;
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _agTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _dsTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private AppSettings _settings;
    private TrayIconHost? _tray;
    private FlyoutWindow? _flyout;
    private IntPtr _hwnd;
    private DockRect _lastDockRect;
    private bool _hiddenForFullscreen;
    private bool _isSettingsOpen;
    private bool _balanceAlertArmed = true;
    private readonly System.Collections.Generic.HashSet<string> _alertedFamilies = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();

        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiUsageDock");

        _settingsStore = new SettingsStore(appDir);
        _secretStore = new FileSecretStore(appDir);
        _historyStore = new HistoryStore(appDir);
        _cacheStore = new CacheStore(appDir);
        _settings = _settingsStore.Load();

        var dsHttp = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10)
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        var agHttp = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                // Antigravity's language server uses a self-signed certificate on loopback only.
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        _deepSeek = new DeepSeekAdapter(dsHttp, _secretStore);
        _antigravity = new AntigravityAdapter(agHttp, new AntigravityProcessDiscovery());

        _store.Changed += () => Dispatcher.InvokeAsync(UpdateDockUi);
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += (_, _) => _tray?.Dispose();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.ApplyDockStyles(_hwnd);
        UpdateDockGeometry();

        _topmostTimer.Tick += (_, _) => OnDockTick();
        _topmostTimer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreCachedSnapshots();
        UpdateDockGeometry();
        CreateTrayIcon();
        UpdateDockUi();

        _agTimer.Tick += (_, _) => _ = RefreshAntigravityAsync();
        _dsTimer.Tick += (_, _) => _ = RefreshDeepSeekAsync();
        _agTimer.Start();
        _dsTimer.Start();
        _ = RefreshAllAsync();
    }

    private void CreateTrayIcon()
    {
        _tray = new TrayIconHost();
        _tray.OpenRequested += () => Dispatcher.Invoke(ShowFlyout);
        _tray.RefreshRequested += () => Dispatcher.Invoke(() => _ = RefreshAllAsync());
        _tray.SettingsRequested += () => Dispatcher.Invoke(OpenSettingsWindow);
        _tray.ExitRequested += () => Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    private void RestoreCachedSnapshots()
    {
        var cached = _cacheStore.Load();
        if (cached.DeepSeek is { } deepSeek)
        {
            _store.Apply(
                DeepSeekAdapter.ProviderId,
                new ProviderResult(
                    new ProviderSnapshot(DeepSeekAdapter.ProviderId, deepSeek, null, deepSeek.FetchedAt),
                    ProviderHealth.Stale));
        }

        if (cached.Antigravity is { } antigravity)
        {
            _store.Apply(
                AntigravityAdapter.ProviderId,
                new ProviderResult(
                    new ProviderSnapshot(AntigravityAdapter.ProviderId, null, antigravity, antigravity.FetchedAt),
                    ProviderHealth.Stale));
        }
    }

    private void OnDockTick()
    {
        if (_isSettingsOpen || _flyout is { IsVisible: true })
        {
            return;
        }

        var hideOnFullscreen = _settings.Dock?.HideOnFullscreen ?? true;
        var fullscreen = hideOnFullscreen && IsFullscreenForeground();

        if (fullscreen != _hiddenForFullscreen)
        {
            _hiddenForFullscreen = fullscreen;
            Visibility = fullscreen ? Visibility.Hidden : Visibility.Visible;
        }

        if (fullscreen)
        {
            return;
        }

        UpdateDockGeometry();

        // Re-assert topmost only when the taskbar is actually covering the dock. Doing it on
        // every tick made the strip flicker while the user clicked the taskbar.
        if (IsCoveredByTaskbar(_lastDockRect))
        {
            NativeMethods.EnsureTopmostAboveTaskbar(_hwnd);
        }
    }

    private bool IsCoveredByTaskbar(DockRect rect)
    {
        if (_hwnd == IntPtr.Zero || rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        var point = new NativeMethods.POINT
        {
            X = rect.Left + rect.Width / 2,
            Y = rect.Top + rect.Height / 2,
        };

        var top = NativeMethods.WindowFromPoint(point);
        if (top == IntPtr.Zero || top == _hwnd)
        {
            return false;
        }

        var root = NativeMethods.GetAncestor(top, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero)
        {
            root = top;
        }

        var className = new System.Text.StringBuilder(64);
        NativeMethods.GetClassName(root, className, className.Capacity);
        var cls = className.ToString();
        return cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private void UpdateDockGeometry()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var taskbar = TaskbarLocator.Select(TaskbarLocator.GetTaskbars(), _settings.Dock?.Monitor);
        if (taskbar is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;

        var layout = DockLayout.Compute(new DockLayoutRequest(
            taskbar.Rect,
            taskbar.Edge,
            taskbar.AutoHide,
            taskbar.Monitor,
            scale,
            WidthDip: (int)Math.Round(Width),
            DesiredHeightDip: DockHeightDip,
            MinOnTaskbarHeightDip: MinOnTaskbarHeightDip,
            OffsetPx: _settings.Dock?.OffsetX ?? 0,
            TrayReserveDip: TrayReserveDip,
            GapDip: 4,
            PreferOnTaskbar: false));

        if (Math.Abs(Height - layout.HeightDip) > 0.35)
        {
            Height = layout.HeightDip;
        }

        if (layout.Rect == _lastDockRect)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            layout.Rect.Left,
            layout.Rect.Top,
            layout.Rect.Width,
            layout.Rect.Height,
            NativeMethods.SWP_NOACTIVATE);

        _lastDockRect = layout.Rect;
    }

    private bool IsFullscreenForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hwnd)
        {
            return false;
        }

        if (NativeMethods.SHQueryUserNotificationState(out var state) == 0 &&
            (state == NativeMethods.QUNS_BUSY || state == NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN))
        {
            return true;
        }

        var className = new System.Text.StringBuilder(64);
        NativeMethods.GetClassName(foreground, className, className.Capacity);
        var cls = className.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow")
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(foreground, out var windowRect))
        {
            return false;
        }

        var monitorHandle = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFOEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
            szDevice = string.Empty,
        };

        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return false;
        }

        var coversWidth = windowRect.Right - windowRect.Left >= monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left - 2;
        var coversHeight = windowRect.Bottom - windowRect.Top >= monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top - 2;
        return coversWidth && coversHeight;
    }

    private void EvaluateAlerts()
    {
        var alerts = _settings.Alerts ?? new AlertSettings();
        if (!alerts.Enabled)
        {
            return;
        }

        if (_store.LastGoodDeepSeek is { } deepSeek)
        {
            var total = deepSeek.Primary.Total;
            if (total < alerts.LowBalance && _balanceAlertArmed)
            {
                _balanceAlertArmed = false;
                _tray?.ShowBalloon("DeepSeek 잔액 부족", $"현재 잔액 {deepSeek.Primary.FormatTotal()}");
            }
            else if (total > alerts.LowBalance * 1.2m)
            {
                _balanceAlertArmed = true;
            }
        }

        var groups = _store.LastGoodAntigravity?.FamilyGroups;
        if (groups is null)
        {
            return;
        }

        foreach (var group in groups)
        {
            if (group.RemainingFraction is not double fraction)
            {
                continue;
            }

            if (fraction < alerts.LowQuota && _alertedFamilies.Add(group.Family))
            {
                _tray?.ShowBalloon("안티그래비티 쿼터 부족", $"{group.DisplayName} {fraction * 100:F0}% 남음");
            }
            else if (fraction > alerts.LowQuota * 1.2)
            {
                _alertedFamilies.Remove(group.Family);
            }
        }
    }
}
