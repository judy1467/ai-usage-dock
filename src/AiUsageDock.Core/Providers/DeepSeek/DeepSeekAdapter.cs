using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AiUsageDock.Core.Abstractions;
using AiUsageDock.Core.Models;

namespace AiUsageDock.Core.Providers.DeepSeek;

public sealed class DeepSeekAdapter : IUsageProvider
{
    public const string ProviderId = "deepseek";

    private const string BalanceEndpoint = "https://api.deepseek.com/user/balance";

    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secretStore;
    private readonly TimeProvider _timeProvider;

    public DeepSeekAdapter(HttpClient httpClient, ISecretStore secretStore, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Id => ProviderId;

    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);

    public async Task<ProviderResult> FetchAsync(CancellationToken ct = default)
    {
        var fetchedAt = _timeProvider.GetUtcNow();

        try
        {
            string? apiKey;
            try
            {
                apiKey = _secretStore.GetDeepSeekApiKey();
            }
            catch (Exception ex)
            {
                return ProviderResult.Fail(ProviderHealth.AuthFailed, "key_unreadable", ex.Message);
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return ProviderResult.Fail(ProviderHealth.AuthFailed, "key_missing", "DeepSeek API key is not configured.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, BalanceEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return ProviderResult.Fail(ProviderHealth.AuthFailed, $"http_{statusCode}", "DeepSeek rejected the API key.");
            }

            if ((int)response.StatusCode == 429)
            {
                return ProviderResult.Fail(ProviderHealth.NetworkFailed, "http_429", "DeepSeek rate limit reached.");
            }

            if (statusCode >= 500)
            {
                return ProviderResult.Fail(ProviderHealth.NetworkFailed, "http_5xx", $"DeepSeek server error ({statusCode}).");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProviderResult.Fail(ProviderHealth.NetworkFailed, $"http_{statusCode}", $"Unexpected DeepSeek response ({statusCode}).");
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseResponse(json, fetchedAt);
        }
        catch (OperationCanceledException)
        {
            return ProviderResult.Fail(ProviderHealth.NetworkFailed, "cancelled", "The DeepSeek request was cancelled.");
        }
        catch (Exception ex)
        {
            return ProviderResult.Fail(ProviderHealth.NetworkFailed, "exception", ex.Message);
        }
    }

    public static ProviderResult ParseResponse(
        string json,
        DateTimeOffset fetchedAt,
        decimal? todaySpend = null,
        IReadOnlyList<BalanceHistoryPoint>? history = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var isAvailable = true;
            if (root.TryGetProperty("is_available", out var availableElement) &&
                (availableElement.ValueKind == JsonValueKind.True || availableElement.ValueKind == JsonValueKind.False))
            {
                isAvailable = availableElement.GetBoolean();
            }

            if (!root.TryGetProperty("balance_infos", out var balanceInfosElement) ||
                balanceInfosElement.ValueKind != JsonValueKind.Array ||
                balanceInfosElement.GetArrayLength() == 0)
            {
                return ProviderResult.Fail(ProviderHealth.ParseFailed, "schema_changed", "balance_infos is missing or empty.");
            }

            var infos = new List<BalanceInfo>();
            foreach (var item in balanceInfosElement.EnumerateArray())
            {
                var currency = GetString(item, "currency") ?? string.Empty;
                var total = GetDecimal(item, "total_balance");
                var granted = GetDecimal(item, "granted_balance");
                var toppedUp = GetDecimal(item, "topped_up_balance");

                infos.Add(new BalanceInfo(currency, total, granted, toppedUp));
            }

            var snapshot = new DeepSeekSnapshot(infos, isAvailable, fetchedAt, todaySpend, history);
            return ProviderResult.Ok(new ProviderSnapshot(ProviderId, snapshot, null, fetchedAt));
        }
        catch (JsonException ex)
        {
            return ProviderResult.Fail(ProviderHealth.ParseFailed, "parse_error", ex.Message);
        }
        catch (Exception ex)
        {
            return ProviderResult.Fail(ProviderHealth.ParseFailed, "parse_error", ex.Message);
        }
    }

    public static decimal CalculateTodaySpend(
        IReadOnlyList<BalanceHistoryPoint> history,
        DateTimeOffset nowLocal,
        string currency = "USD")
    {
        if (history is null || history.Count < 2)
        {
            return 0m;
        }

        var points = history
            .Where(p => p is not null)
            .Where(p => string.Equals(p.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Timestamp.ToOffset(nowLocal.Offset).Date == nowLocal.Date)
            .OrderBy(p => p.Timestamp)
            .ToList();

        if (points.Count < 2)
        {
            return 0m;
        }

        var spend = 0m;
        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1].Total;
            var current = points[i].Total;
            spend += Math.Max(0m, previous - current);
        }

        return spend;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static decimal GetDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0m;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (decimal.TryParse(
                    text,
                    NumberStyles.Number | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }
        }

        return 0m;
    }
}
