using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsageDock.Core.Models;
using AiUsageDock.Core.Providers.Antigravity;
using AiUsageDock.Core.Scheduling;

namespace AiUsageDock.App;

public partial class FlyoutWindow : Window
{
    private static readonly SolidColorBrush Good = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly SolidColorBrush Bad = new(Color.FromRgb(0xF8, 0x51, 0x49));
    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly SolidColorBrush Track = new(Color.FromArgb(0x33, 0x8B, 0x94, 0x9E));

    private const string DefaultHint = "값: 5시간/주간 · 행 클릭: 고정/해제 (최대 3개)";

    private readonly UsageStore _store;
    private readonly Func<Task> _onRefresh;
    private readonly Action _onOpenSettings;
    private readonly Func<string, bool> _onTogglePin;
    private readonly Func<IReadOnlyList<string>> _getPinned;
    private readonly Func<IReadOnlyList<BalanceHistoryPoint>> _getHistory;
    private DateTimeOffset _shownAtUtc;
    private bool _closing;
    private bool _everActivated;
    private DispatcherTimer? _hintTimer;

    public FlyoutWindow(
        UsageStore store,
        Func<Task> onRefresh,
        Action onOpenSettings,
        Func<string, bool> onTogglePin,
        Func<IReadOnlyList<string>> getPinned,
        Func<IReadOnlyList<BalanceHistoryPoint>> getHistory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _onRefresh = onRefresh ?? throw new ArgumentNullException(nameof(onRefresh));
        _onOpenSettings = onOpenSettings ?? throw new ArgumentNullException(nameof(onOpenSettings));
        _onTogglePin = onTogglePin ?? throw new ArgumentNullException(nameof(onTogglePin));
        _getPinned = getPinned ?? throw new ArgumentNullException(nameof(getPinned));
        _getHistory = getHistory ?? throw new ArgumentNullException(nameof(getHistory));

        InitializeComponent();

        // Stamp before Show: activation can fail for a NOACTIVATE owner and fire Deactivated
        // immediately, which previously closed the panel as soon as it opened.
        _shownAtUtc = DateTimeOffset.UtcNow;

        Loaded += (_, _) =>
        {
            Activate();
        };

        Activated += (_, _) => _everActivated = true;

        Deactivated += (_, _) =>
        {
            // Only auto-close once the panel actually had focus. A NOACTIVATE owner can make
            // Activate() fail, and closing on that first Deactivated hid the panel instantly.
            if (_everActivated && (DateTimeOffset.UtcNow - _shownAtUtc).TotalMilliseconds > 250)
            {
                // Closing directly inside WM_ACTIVATE throws (Window.VerifyNotClosing).
                Dispatcher.BeginInvoke(new Action(SafeClose), System.Windows.Threading.DispatcherPriority.Background);
            }
        };

        PreviewKeyDown += OnPreviewKeyDown;

        RenderSnapshot();
    }

    public void RenderSnapshot()
    {
        var deepSeek = _store.LastGoodDeepSeek;
        var deepSeekResult = _store.DeepSeekResult;
        var antigravity = _store.LastGoodAntigravity;
        var antigravityResult = _store.AntigravityResult;

        var deepSeekHealth = deepSeekResult?.Health ?? ProviderHealth.Ok;
        DeepSeekBadgeText.Text = deepSeekHealth == ProviderHealth.Ok ? "사용 가능" : HealthText(deepSeekHealth);
        DeepSeekBadgeText.Foreground = HealthBrush(deepSeekHealth);

        if (deepSeek is not null && deepSeek.Balances.Count > 0)
        {
            var primary = deepSeek.Primary;
            DeepSeekBalanceText.Text = primary.FormatTotal();
            DeepSeekDetailText.Text = $"충전 {primary.Symbol}{primary.ToppedUp:F2} · 보너스 {primary.Symbol}{primary.Granted:F2} · {primary.Currency}";
            DeepSeekSpendText.Text = deepSeek.TodaySpendEstimate is { } spend
                ? $"오늘 사용 ≈ {primary.Symbol}{spend:F2} (추정)"
                : "오늘 사용 -";
        }
        else
        {
            DeepSeekBalanceText.Text = "-";
            DeepSeekDetailText.Text = deepSeekResult?.ErrorDetail ?? "데이터 없음";
            DeepSeekSpendText.Text = "오늘 사용 -";
        }

        var agHealth = antigravityResult?.Health ?? ProviderHealth.Ok;
        PlanBadgeText.Text = antigravity?.PlanName ?? "플랜 -";
        AgStatusDot.Fill = HealthBrush(agHealth);
        AgStatusText.Text = agHealth switch
        {
            ProviderHealth.Ok => "앱 실행 중 · 30초 주기",
            ProviderHealth.Stale => "지연 · 재시도 중",
            ProviderHealth.AppDown => "앱 꺼짐",
            ProviderHealth.AuthFailed => "토큰 오류",
            ProviderHealth.ParseFailed => "업데이트 필요",
            _ => "연결 오류",
        };

        DrawSparkline(_getHistory());

        ModelsPanel.Children.Clear();

        if (antigravity is null || antigravity.FamilyGroups.Count == 0)
        {
            ModelsPanel.Children.Add(new TextBlock
            {
                Text = antigravityResult?.ErrorDetail ?? "모델 정보 없음",
                Foreground = Muted,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 6)
            });
        }
        else
        {
            var pinned = new HashSet<string>(_getPinned() ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var group in antigravity.FamilyGroups)
            {
                ModelsPanel.Children.Add(BuildGroupRow(group, pinned.Contains(group.Family), antigravity));
            }
        }

        var pinnedCount = _getPinned()?.Count ?? 0;
        SummaryText.Text = antigravity is null
            ? "수집된 데이터가 없습니다."
            : $"모델 {antigravity.Models.Count}개 · 쿼터 그룹 {antigravity.PoolCount}개 · 고정 {pinnedCount}/3 · 5시간/주간 리셋";

        FooterText.Text = antigravity is null
            ? "Esc 닫기 · 행 클릭: 핀 고정"
            : $"마지막 갱신 {antigravity.FetchedAt.ToLocalTime():HH:mm:ss} · 자동 30초/5분 · v1.0";
    }

    private FrameworkElement BuildGroupRow(ModelQuotaGroup group, bool isPinned, AntigravitySnapshot snapshot)
    {
        var windows = QuotaWindows.Resolve(snapshot, group.Family);
        var sessionFraction = windows.Session?.RemainingFraction;
        var weeklyFraction = windows.Weekly?.RemainingFraction;
        var effective = windows.Effective ?? group.RemainingFraction;
        var percent = effective is double value ? value * 100.0 : 0.0;
        var hasValue = effective.HasValue;
        var accent = !hasValue ? Muted : percent > 40 ? Good : percent > 15 ? Warn : Bad;

        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(-4, 0, -4, 4),
            Cursor = Cursors.Hand,
            ToolTip = isPinned ? "클릭하여 작업표시줄에서 해제" : "클릭하여 작업표시줄에 고정",
        };

        var panel = new StackPanel();
        row.Child = panel;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = group.DisplayName,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Border? pinChip = null;
        if (isPinned)
        {
            pinChip = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x2A, 0x3D)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x4A, 0x6B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 0, 5, 1),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "고정",
                    FontSize = 9.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF)),
                },
            };
        }

        var percentText = new TextBlock
        {
            Text = windows.HasAny
                ? QuotaWindows.FormatPair(windows)
                : hasValue ? $"{percent:F0}% / –" : "-",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "5시간/주간 남은 비율",
        };

        Grid.SetColumn(name, 0);
        Grid.SetColumn(percentText, 2);
        header.Children.Add(name);
        header.Children.Add(percentText);
        if (pinChip is not null)
        {
            Grid.SetColumn(pinChip, 1);
            header.Children.Add(pinChip);
        }

        var bar = new ProgressBar
        {
            Height = 3.5,
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(percent, 0, 100),
            Foreground = accent,
            Background = Track,
            Margin = new Thickness(0, 3, 0, 0),
        };

        var now = DateTimeOffset.Now;
        var resetParts = new List<string>();
        if (windows.Session is { } session)
        {
            resetParts.Add($"5h {session.FormatResetTime(now)}");
        }

        if (windows.Weekly is { } weekly)
        {
            resetParts.Add($"주간 {weekly.FormatResetTime(now)}");
        }

        if (resetParts.Count == 0 && group.Models.Count > 0)
        {
            resetParts.Add(group.Models[0].FormatResetTime(now));
        }

        var reset = new TextBlock
        {
            Text = resetParts.Count > 0 ? string.Join(" · ", resetParts) : "-",
            FontSize = 10.5,
            Foreground = Muted,
            Margin = new Thickness(0, 2, 0, 0),
        };

        panel.Children.Add(header);
        panel.Children.Add(bar);
        panel.Children.Add(reset);

        var hover = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x29));
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var applied = _onTogglePin(group.Family);
            if (!applied)
            {
                ShowHint("독에는 최대 3개까지 고정할 수 있습니다. 하나를 해제한 뒤 다시 시도하세요.");
            }

            RenderSnapshot();
        };

        return row;
    }

    private void ShowHint(string message)
    {
        HintText.Text = message;
        HintText.Foreground = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));

        _hintTimer?.Stop();
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer?.Stop();
            HintText.Text = DefaultHint;
            HintText.Foreground = Muted;
        };
        _hintTimer.Start();
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = MoreButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void SafeClose()
    {
        if (_closing || !IsVisible)
        {
            return;
        }

        try
        {
            _closing = true;
            Close();
        }
        catch (InvalidOperationException)
        {
            // The window was already closing (activation race).
        }
        finally
        {
            _closing = false;
        }
    }

    private void DrawSparkline(IReadOnlyList<BalanceHistoryPoint>? history)
    {
        SparkCanvas.Children.Clear();

        if (history is null || history.Count(p => p is not null) < 2)
        {
            SparkCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        var points = history
            .Where(p => p is not null)
            .OrderBy(p => p.Timestamp)
            .TakeLast(80)
            .ToList();

        if (points.Count < 2)
        {
            SparkCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        SparkCanvas.Visibility = Visibility.Visible;

        var min = points.Min(p => p.Total);
        var max = points.Max(p => p.Total);
        var range = max - min;
        var width = SparkCanvas.Width > 0 ? SparkCanvas.Width : 148;
        var height = SparkCanvas.Height > 0 ? SparkCanvas.Height : 50;
        const double pad = 3;

        var mapped = new List<Point>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var x = pad + (width - 2 * pad) * i / (points.Count - 1);
            var normalized = range <= 0 ? 0.5 : (double)((points[i].Total - min) / range);
            var y = pad + (height - 2 * pad) * (1 - normalized);
            mapped.Add(new Point(x, y));
        }

        var area = new System.Windows.Shapes.Polygon
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x6E, 0x8C, 0xFF)),
        };
        foreach (var point in mapped)
        {
            area.Points.Add(point);
        }

        area.Points.Add(new Point(width - pad, height - pad));
        area.Points.Add(new Point(pad, height - pad));

        var line = new System.Windows.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x8A, 0xA5, 0xFF)),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
        };
        foreach (var point in mapped)
        {
            line.Points.Add(point);
        }

        var last = new System.Windows.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = new SolidColorBrush(Color.FromRgb(0xB0, 0xC4, 0xFF)),
        };
        Canvas.SetLeft(last, mapped[^1].X - 2);
        Canvas.SetTop(last, mapped[^1].Y - 2);

        SparkCanvas.Children.Add(area);
        SparkCanvas.Children.Add(line);
        SparkCanvas.Children.Add(last);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        SafeClose();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _onRefresh();
        }
        catch
        {
            // Health is surfaced through the store; ignore transient refresh failures here.
        }
        finally
        {
            RenderSnapshot();
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _onOpenSettings();

    private void OnExitClick(object sender, RoutedEventArgs e) => App.RequestExit();

    private static string HealthText(ProviderHealth health) => health switch
    {
        ProviderHealth.Ok => "정상",
        ProviderHealth.Stale => "지연",
        ProviderHealth.AppDown => "앱 중단",
        _ => "오류"
    };

    private static SolidColorBrush HealthBrush(ProviderHealth health) => health switch
    {
        ProviderHealth.Ok => Good,
        ProviderHealth.Stale => Warn,
        _ => Bad
    };
}

