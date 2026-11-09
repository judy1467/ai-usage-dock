using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using AiUsageDock.App.Interop;
using AiUsageDock.Core.Abstractions;
using AiUsageDock.Core.Storage;

namespace AiUsageDock.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly Func<Task> _onSavedAsync;
    private readonly AppSettings _settings;

    private sealed record MonitorChoice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    public SettingsWindow(SettingsStore settingsStore, ISecretStore secretStore, Func<Task> onSavedAsync)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _onSavedAsync = onSavedAsync ?? throw new ArgumentNullException(nameof(onSavedAsync));

        InitializeComponent();

        _settings = _settingsStore.Load();

        ApiKeyBox.Password = string.Empty;
        UpdateKeyStatus();

        CurrencyCombo.Items.Clear();
        CurrencyCombo.Items.Add("USD");
        CurrencyCombo.Items.Add("CNY");

        var currency = string.IsNullOrWhiteSpace(_settings.Currency) ? "USD" : _settings.Currency;
        if (!CurrencyCombo.Items.Cast<object>().Any(item => string.Equals(item as string, currency, StringComparison.OrdinalIgnoreCase)))
        {
            CurrencyCombo.Items.Add(currency);
        }

        CurrencyCombo.SelectedItem = CurrencyCombo.Items
            .Cast<object>()
            .FirstOrDefault(item => string.Equals(item as string, currency, StringComparison.OrdinalIgnoreCase));

        if (CurrencyCombo.SelectedItem is null)
        {
            CurrencyCombo.SelectedIndex = 0;
        }

        var offset = _settings.Dock?.OffsetX ?? 0;
        OffsetXBox.Text = offset.ToString();

        PopulateMonitors();
        FullscreenCheck.IsChecked = _settings.Dock?.HideOnFullscreen ?? true;
        StartWithWindowsCheck.IsChecked = StartupRegistrar.IsEnabled();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkChrome();

        // The title bar is composed after the first show; re-apply so DWM keeps dark mode.
        Loaded += (_, _) => ApplyDarkChrome();
    }

    private void ApplyDarkChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var dark = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        var round = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    private void PopulateMonitors()    {
        var choices = new List<MonitorChoice> { new("primary", "주 모니터") };
        foreach (var taskbar in TaskbarLocator.GetTaskbars().Where(t => !t.IsPrimary))
        {
            choices.Add(new MonitorChoice(taskbar.MonitorDevice, taskbar.DisplayName));
        }

        MonitorCombo.ItemsSource = choices;

        var current = _settings.Dock?.Monitor ?? "primary";
        var selected = choices.FirstOrDefault(c => string.Equals(c.Value, current, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        MonitorCombo.SelectedItem = selected;
    }

    private void UpdateKeyStatus()
    {
        var existing = _secretStore.GetDeepSeekApiKey();
        KeyStatusText.Text = string.IsNullOrWhiteSpace(existing)
            ? "저장된 키 없음"
            : $"저장됨: {Mask(existing)} · 변경할 때만 입력";
    }

    private static string Mask(string key)
    {
        if (key.Length <= 10)
        {
            return new string('•', 8);
        }

        return $"{key[..6]}…{key[^4..]}";
    }

    private void OnClearKeyClick(object sender, RoutedEventArgs e)
    {
        _secretStore.Clear();
        ApiKeyBox.Password = string.Empty;
        UpdateKeyStatus();
        StatusText.Text = "저장된 키를 삭제했습니다. 새 키를 입력하고 저장하세요.";
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        StatusText.Text = "저장 및 사용량 갱신 중...";

        try
        {
            // Empty means "keep the stored key"; never round-trip the secret through the UI.
            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                _secretStore.SetDeepSeekApiKey(ApiKeyBox.Password.Trim());
            }

            var currency = CurrencyCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(currency))
            {
                currency = string.IsNullOrWhiteSpace(_settings.Currency) ? "USD" : _settings.Currency;
            }

            var offsetX = _settings.Dock?.OffsetX ?? 0;
            if (int.TryParse(OffsetXBox.Text?.Trim(), out var parsed))
            {
                offsetX = parsed;
            }

            var monitor = (MonitorCombo.SelectedItem as MonitorChoice)?.Value ?? "primary";
            var hideOnFullscreen = FullscreenCheck.IsChecked != false;
            var startWithWindows = StartWithWindowsCheck.IsChecked == true;

            StartupRegistrar.SetEnabled(startWithWindows);

            var dock = (_settings.Dock ?? new DockSettings()) with
            {
                OffsetX = offsetX,
                Monitor = monitor,
                HideOnFullscreen = hideOnFullscreen,
            };

            var updated = _settings with
            {
                Currency = currency,
                Dock = dock,
                StartWithWindows = startWithWindows,
            };

            _settingsStore.Save(updated);
            await _onSavedAsync();
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"저장 오류: {ex.Message}";
            SaveButton.IsEnabled = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
