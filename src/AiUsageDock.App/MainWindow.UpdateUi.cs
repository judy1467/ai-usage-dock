using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiUsageDock.Core.Models;
using AiUsageDock.Core.Providers.Antigravity;
using AiUsageDock.Core.Providers.DeepSeek;

namespace AiUsageDock.App
{
    public partial class MainWindow
    {
        private static readonly Brush LowBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
        private static readonly Brush MidBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
        private static readonly Brush HighBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
        private DateTimeOffset _flyoutClosedUtc;

        public Task RefreshAllAsync() => Task.WhenAll(RefreshDeepSeekAsync(), RefreshAntigravityAsync());

        private async Task RefreshDeepSeekAsync()
        {
            var result = await _deepSeek.FetchAsync();
            if (result.Snapshot?.DeepSeek is { } ds)
            {
                _historyStore.Append(new BalanceHistoryPoint(ds.FetchedAt, ds.Primary.Currency, ds.Primary.Total));
                var spend = DeepSeekAdapter.CalculateTodaySpend(_historyStore.LoadRecent(DateTimeOffset.UtcNow.AddDays(-7)), DateTimeOffset.Now, ds.Primary.Currency);
                result = ProviderResult.Ok(result.Snapshot with { DeepSeek = ds with { TodaySpendEstimate = spend } });
            }
            _store.Apply(DeepSeekAdapter.ProviderId, result);
            _cacheStore.Save(_store.LastGoodDeepSeek, _store.LastGoodAntigravity);
            EvaluateAlerts();
        }

        private async Task RefreshAntigravityAsync()
        {
            var res = await _antigravity.FetchAsync();
            _store.Apply(AntigravityAdapter.ProviderId, res);
            _cacheStore.Save(_store.LastGoodDeepSeek, _store.LastGoodAntigravity);
            EvaluateAlerts();
        }

        private void UpdateDockUi()
        {
            DeepSeekValueText.Text = _store.LastGoodDeepSeek?.Primary.FormatTotal()
                ?? (_store.DeepSeekResult?.ErrorCode switch
                {
                    "key_missing" => "키 설정",
                    "http_401" => "키 오류",
                    _ => "$--.--"
                });

            var pinned = GetPinned();
            var groups = _store.LastGoodAntigravity?.FamilyGroups;

            const int SlotCount = 3;
            var matched = new ModelQuotaGroup?[SlotCount];
            var used = new HashSet<ModelQuotaGroup>();

            for (var i = 0; i < SlotCount; i++)
            {
                var family = pinned.ElementAtOrDefault(i);
                ModelQuotaGroup? found = null;
                if (groups is not null && family is not null)
                {
                    found = groups.FirstOrDefault(g =>
                        !used.Contains(g) &&
                        string.Equals(g.Family, family, StringComparison.OrdinalIgnoreCase));
                }

                if (found is null && groups is not null)
                {
                    found = groups.FirstOrDefault(g => !used.Contains(g));
                }

                if (found is not null)
                {
                    used.Add(found);
                }

                matched[i] = found;
            }

            BindTile(Tile1Label, Tile1Value, Tile1Bar, matched[0], pinned.ElementAtOrDefault(0), _store.LastGoodAntigravity);
            BindTile(Tile2Label, Tile2Value, Tile2Bar, matched[1], pinned.ElementAtOrDefault(1), _store.LastGoodAntigravity);
            BindTile(Tile3Label, Tile3Value, Tile3Bar, matched[2], pinned.ElementAtOrDefault(2), _store.LastGoodAntigravity);

            var antigravityWorking = _store.LastGoodAntigravity is not null;
            var deepSeekKeyMissing = _store.DeepSeekResult?.ErrorCode == "key_missing";
            var health = (deepSeekKeyMissing && antigravityWorking)
                ? (_store.AntigravityResult?.Health ?? ProviderHealth.Ok)
                : _store.GetOverallHealth(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));

            var color = health switch
            {
                ProviderHealth.Ok => Brushes.LimeGreen,
                ProviderHealth.Stale => Brushes.Goldenrod,
                ProviderHealth.AppDown => Brushes.OrangeRed,
                _ => Brushes.Red
            };
            StatusDot.Fill = color;

            var isError = health is ProviderHealth.AuthFailed or ProviderHealth.ParseFailed or ProviderHealth.NetworkFailed;
            DockBorder.BorderBrush = new SolidColorBrush(isError ? Color.FromRgb(0xF8, 0x51, 0x49) : Color.FromRgb(0x38, 0x38, 0x3F));
            ErrorBangText.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
            _tray?.SetTooltip($"AI Usage Dock · {DeepSeekValueText.Text} · {Tile1Value.Text}/{Tile2Value.Text}/{Tile3Value.Text}");
            _flyout?.RenderSnapshot();
        }

        private static string ShortenDisplayName(string name) => name switch
        {
            "Gemini 4 Argon" or "gemini-4-argon" => "Gemini 4",
            "Gemini 3.8 Flash" or "gemini-3.8-flash" => "Flash 3.8",
            "Claude Opus 5.5" or "claude-opus-5-5" => "Claude Opus",
            "Claude Sonnet 5.5" or "claude-sonnet-5-5" => "Claude Sonnet",
            "GPT-OSS 120B" or "gpt-oss-120b" => "GPT-OSS",
            _ => name
        };

        private static Brush BrushForPercent(double pct) =>
            pct < 15 ? LowBrush : pct < 40 ? MidBrush : HighBrush;

        private static void BindTile(
            TextBlock label,
            TextBlock value,
            ProgressBar bar,
            ModelQuotaGroup? group,
            string? fallbackFamily,
            AntigravitySnapshot? snapshot)
        {
            var displayName = group?.DisplayName ?? fallbackFamily ?? "—";
            label.Text = ShortenDisplayName(displayName);

            // Taskbar shows "5-hour / weekly" — the second number is the weekly limit.
            var pair = QuotaWindows.Resolve(snapshot, group?.Family ?? fallbackFamily);

            double? effective;
            if (pair.HasAny)
            {
                value.Text = QuotaWindows.FormatPair(pair);
                effective = pair.Effective;
            }
            else if (group?.RemainingFraction is double legacy)
            {
                // Quota summary unavailable (older app build): fall back to the per-model number.
                value.Text = $"{legacy * 100:F0}% / –";
                effective = legacy;
            }
            else
            {
                value.Text = "—";
                effective = null;
            }

            if (effective is double fraction)
            {
                var pct = fraction * 100.0;
                bar.Value = Math.Clamp(pct, 0, 100);
                bar.Foreground = BrushForPercent(pct);
            }
            else
            {
                bar.Value = 0;
                bar.Foreground = LowBrush;
            }
        }

        private void OnDockLeftClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowFlyout();
        }

        private void ShowFlyout()
        {
            if (_flyout is { IsVisible: true })
            {
                _flyout.Close();
                _flyout = null;
                return;
            }

            if ((DateTimeOffset.UtcNow - _flyoutClosedUtc).TotalMilliseconds < 250)
            {
                return;
            }

            _flyout = new FlyoutWindow(
                _store,
                RefreshAllAsync,
                OpenSettingsWindow,
                TogglePin,
                GetPinned,
                () => _historyStore.LoadRecent(DateTimeOffset.UtcNow.AddDays(-7)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            _flyout.Closed += (_, _) => { _flyoutClosedUtc = DateTimeOffset.UtcNow; _flyout = null; };

            var workArea = SystemParameters.WorkArea;
            _flyout.Left = Math.Clamp(Left + Width - _flyout.Width, workArea.Left + 8, Math.Max(workArea.Left + 8, workArea.Right - _flyout.Width - 8));
            _flyout.Top = Math.Max(workArea.Top + 8, Top - _flyout.Height - 8);

            _flyout.Show();
            _flyout.Activate();
        }

        private void OnDockRightClick(object sender, MouseButtonEventArgs e)
        {
            if (ContextMenu is { } menu) { menu.PlacementTarget = this; menu.IsOpen = true; }
            e.Handled = true;
        }

        private async void OnMenuRefreshClick(object sender, RoutedEventArgs e) => await RefreshAllAsync();
        private void OnMenuSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsWindow();
        private void OnMenuExitClick(object sender, RoutedEventArgs e) => App.RequestExit();

        private void OpenSettingsWindow()
        {
            if (_isSettingsOpen) return;

            if (_flyout is not null) { _flyout.Close(); _flyout = null; }

            _isSettingsOpen = true;
            try
            {
                var win = new SettingsWindow(_settingsStore, _secretStore, async () => { UpdateDockGeometry(); await RefreshAllAsync(); UpdateDockUi(); });
                win.ShowDialog();
                _settings = _settingsStore.Load();
                UpdateDockGeometry();
                UpdateDockUi();
            }
            finally
            {
                _isSettingsOpen = false;
            }
        }

        private static readonly string[] DefaultPinned = { "gemini-4-argon", "gemini-3.8-flash", "claude-opus-5-5" };

        private bool TogglePin(string family)
        {
            var list = (_settings.Pinned ?? Array.Empty<string>()).ToList();

            if (list.Remove(family))
            {
                _settings = _settings with { Pinned = list };
                _settingsStore.Save(_settings);
                UpdateDockUi();
                return true;
            }

            // The dock has exactly three slots; refuse a fourth pin instead of silently hiding it.
            if (list.Count >= 3)
            {
                return false;
            }

            list.Add(family);
            _settings = _settings with { Pinned = list };
            _settingsStore.Save(_settings);
            UpdateDockUi();
            return true;
        }

        private IReadOnlyList<string> GetPinned() => _settings.Pinned ?? DefaultPinned;
    }
}

