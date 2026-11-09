using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiUsageDock.Core.Models;
using AiUsageDock.Core.Scheduling;

namespace AiUsageDock.App;

public partial class FlyoutWindow : Window
{
    private static readonly SolidColorBrush Good = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly SolidColorBrush Bad = new(Color.FromRgb(0xF8, 0x51, 0x49));
    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly SolidColorBrush Track = new(Color.FromArgb(0x33, 0x8B, 0x94, 0x9E));

    private readonly UsageStore _store;
    private readonly Func<Task> _onRefresh;
    private readonly Action _onOpenSettings;
    private readonly Action<string> _onTogglePin;
    private readonly Func<IReadOnlyList<string>> _getPinned;
    private readonly Func<IReadOnlyList<BalanceHistoryPoint>> _getHistory;
    private DateTimeOffset _shownAtUtc;
    private bool _closing;

    public FlyoutWindow(
        UsageStore store,
        Func<Task> onRefresh,
        Action onOpenSettings,
        Action<string> onTogglePin,
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

        Loaded += (_, _) =>
        {
            _shownAtUtc = DateTimeOffset.UtcNow;
            Activate();
        };

        Deactivated += (_, _) =>
        {
            if ((DateTimeOffset.UtcNow - _shownAtUtc).TotalMilliseconds > 200)
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
        DeepSeekBadgeText.Text = HealthText(deepSeekHealth);
        DeepSeekBadgeText.Foreground = HealthBrush(deepSeekHealth);

        if (deepSeek is not null && deepSeek.Balances.Count > 0)
        {
            var primary = deepSeek.Primary;
            DeepSeekBalanceText.Text = primary.FormatTotal();
            DeepSeekDetailText.Text = $"충전 {primary.Symbol}{primary.ToppedUp:F2} · 보너스 {primary.Symbol}{primary.Granted:F2} · {primary.Currency}";
            DeepSeekSpendText.Text = deepSeek.TodaySpendEstimate is { } spend
                ? $"오늘 사용 ≈ {primary.Symbol}{spend:F2}"
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
        AgStatusText.Text = HealthText(agHealth);

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
                ModelsPanel.Children.Add(BuildGroupRow(group, pinned.Contains(group.Family)));
            }
        }

        var parts = new List<string>();
        if (deepSeek is not null)
        {
            parts.Add($"DeepSeek {deepSeek.FetchedAt.ToLocalTime():HH:mm}");
        }

        if (antigravity is not null)
        {
            parts.Add($"Antigravity {antigravity.FetchedAt.ToLocalTime():HH:mm}");
        }

        SummaryText.Text = parts.Count == 0 ? "수집된 데이터가 없습니다." : string.Join(" · ", parts);
        FooterText.Text = "Esc 닫기 · 행 클릭: 핀 고정";

        if (antigravity is not null)
        {
            SummaryText.Text = $"모델 {antigravity.Models.Count}개 · 쿼터 풀 {antigravity.PoolCount}개 · 행 클릭 시 작업표시줄 고정";
            FooterText.Text = $"마지막 갱신 {antigravity.FetchedAt.ToLocalTime():HH:mm:ss} · 자동 30초/5분 · Esc 닫기";
        }
    }

    private FrameworkElement BuildGroupRow(ModelQuotaGroup group, bool isPinned)
    {
        var hasValue = group.RemainingFraction.HasValue;
        var percent = hasValue ? group.RemainingFraction!.Value * 100.0 : 0.0;
        var accent = !hasValue ? Muted : percent > 40 ? Good : percent > 15 ? Warn : Bad;

        var row = new Grid
        {
            Height = 26,
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            ToolTip = "클릭하여 핀 고정"
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = (isPinned ? "★ " : string.Empty) + group.DisplayName,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var percentText = new TextBlock
        {
            Text = hasValue ? $"{percent:F0}%" : "-",
            FontSize = 12,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = accent
        };
        var bar = new ProgressBar
        {
            Height = 4,
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(percent, 0, 100),
            Foreground = accent,
            Background = Track,
            VerticalAlignment = VerticalAlignment.Center
        };
        var reset = new TextBlock
        {
            Text = group.Models.Count > 0 ? group.Models[0].FormatResetTime(DateTimeOffset.Now) : "-",
            FontSize = 11,
            Foreground = Muted,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(name, 0);
        Grid.SetColumn(percentText, 1);
        Grid.SetColumn(bar, 2);
        Grid.SetColumn(reset, 3);
        row.Children.Add(name);
        row.Children.Add(percentText);
        row.Children.Add(bar);
        row.Children.Add(reset);

        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _onTogglePin(group.Family);
            RenderSnapshot();
        };

        return row;
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
            SparkLabel.Visibility = Visibility.Collapsed;
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
            SparkLabel.Visibility = Visibility.Collapsed;
            return;
        }

        SparkCanvas.Visibility = Visibility.Visible;
        SparkLabel.Visibility = Visibility.Visible;

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

    private void OnExitClick(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

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
