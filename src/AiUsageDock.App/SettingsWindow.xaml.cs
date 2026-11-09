using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AiUsageDock.Core.Abstractions;
using AiUsageDock.Core.Storage;

namespace AiUsageDock.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly Func<Task> _onSavedAsync;
    private readonly AppSettings _settings;

    public SettingsWindow(SettingsStore settingsStore, ISecretStore secretStore, Func<Task> onSavedAsync)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _onSavedAsync = onSavedAsync ?? throw new ArgumentNullException(nameof(onSavedAsync));

        InitializeComponent();

        _settings = _settingsStore.Load();

        ApiKeyBox.Text = _secretStore.GetDeepSeekApiKey() ?? string.Empty;

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

        StartWithWindowsCheck.IsChecked = StartupRegistrar.IsEnabled();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        StatusText.Text = "저장 및 사용량 갱신 중...";

        try
        {
            _secretStore.SetDeepSeekApiKey(ApiKeyBox.Text.Trim());

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

            var dock = (_settings.Dock ?? new DockSettings()) with { OffsetX = offsetX };
            var startWithWindows = StartWithWindowsCheck.IsChecked == true;
            StartupRegistrar.SetEnabled(startWithWindows);

            var updated = _settings with { Currency = currency, Dock = dock, StartWithWindows = startWithWindows };

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
