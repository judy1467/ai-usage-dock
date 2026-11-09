using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AiUsageDock.App.Interop;
using AiUsageDock.App.Shell;
using AiUsageDock.Core.Providers.Antigravity;
using AiUsageDock.Core.Providers.DeepSeek;
using AiUsageDock.Core.Scheduling;
using AiUsageDock.Core.Storage;

namespace AiUsageDock.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly FileSecretStore _secretStore;
    private readonly HistoryStore _historyStore;
    private readonly UsageStore _store = new();
    private readonly DeepSeekAdapter _deepSeek;
    private readonly AntigravityAdapter _antigravity;
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _agTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _dsTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private AppSettings _settings;
    private TrayIconHost? _tray;
    private FlyoutWindow? _flyout;
    private bool _isSettingsOpen;

    public MainWindow()
    {
        InitializeComponent();

        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiUsageDock");

        _settingsStore = new SettingsStore(appDir);
        _secretStore = new FileSecretStore(appDir);
        _historyStore = new HistoryStore(appDir);
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
                // Antigravity's language server uses a self-signed certificate on loopback.
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
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.ApplyDockStyles(hwnd);
        PositionAboveTaskbar();

        _topmostTimer.Tick += (_, _) =>
        {
            if (_isSettingsOpen || _flyout is { IsVisible: true })
            {
                return;
            }

            PositionAboveTaskbar();
            NativeMethods.EnsureTopmostAboveTaskbar(hwnd);
        };
        _topmostTimer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionAboveTaskbar();
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

    private void PositionAboveTaskbar()
    {
        var workArea = SystemParameters.WorkArea;
        var offset = _settings.Dock?.OffsetX ?? 0;

        var targetLeft = Math.Clamp(
            workArea.Right - Width - 16 + offset,
            workArea.Left + 8,
            Math.Max(workArea.Left + 8, workArea.Right - Width - 8));
        var targetTop = Math.Max(workArea.Top + 8, workArea.Bottom - Height - 4);

        // Only move when the target actually changed; moving every tick causes flicker.
        if (Math.Abs(Left - targetLeft) > 0.5)
        {
            Left = targetLeft;
        }

        if (Math.Abs(Top - targetTop) > 0.5)
        {
            Top = targetTop;
        }
    }
}
