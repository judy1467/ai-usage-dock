using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AiUsageDock.Core.Abstractions;
using AiUsageDock.Core.Models;

namespace AiUsageDock.Core.Providers.Antigravity;

public sealed class AntigravityAdapter : IUsageProvider
{
    public const string ProviderId = "antigravity";

    private const string ServicePath = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string RequestBody = "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"ideVersion\":\"unknown\",\"locale\":\"en\"}}";

    private readonly HttpClient _httpClient;
    private readonly IAntigravityDiscovery _discovery;
    private readonly TimeProvider _timeProvider;
    private AntigravityEndpoint? _cachedEndpoint;

    public AntigravityAdapter(
        HttpClient httpClient,
        IAntigravityDiscovery discovery,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _discovery = discovery;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Id => ProviderId;

    public TimeSpan DefaultInterval => TimeSpan.FromSeconds(30);

    public async Task<ProviderResult> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            if (_cachedEndpoint is null)
            {
                var discoveryResult = await _discovery.DiscoverAsync(ct).ConfigureAwait(false);
                if (!discoveryResult.IsSuccess)
                {
                    return ProviderResult.Fail(
                        ProviderHealth.AppDown,
                        discoveryResult.ErrorCode ?? "ls_not_found",
                        discoveryResult.ErrorDetail);
                }

                _cachedEndpoint = discoveryResult.Endpoint!;
            }

            var outcome = await SendGetUserStatusAsync(_cachedEndpoint!, ct).ConfigureAwait(false);

            if (outcome.Status == HttpStatusCode.Unauthorized || outcome.Error is not null)
            {
                _cachedEndpoint = null;

                var rediscovery = await _discovery.DiscoverAsync(ct).ConfigureAwait(false);
                if (!rediscovery.IsSuccess)
                {
                    return ProviderResult.Fail(
                        ProviderHealth.AppDown,
                        rediscovery.ErrorCode ?? "ls_not_found",
                        rediscovery.ErrorDetail);
                }

                _cachedEndpoint = rediscovery.Endpoint!;
                outcome = await SendGetUserStatusAsync(_cachedEndpoint!, ct).ConfigureAwait(false);

                if (outcome.Status == HttpStatusCode.Unauthorized)
                {
                    return ProviderResult.Fail(
                        ProviderHealth.AuthFailed,
                        "csrf_invalid",
                        "Antigravity CSRF token rejected.");
                }

                if (outcome.Error is not null)
                {
                    return ProviderResult.Fail(
                        ProviderHealth.AppDown,
                        "port_closed",
                        outcome.Error.Message);
                }
            }

            if (outcome.Status == HttpStatusCode.OK && outcome.Body is not null)
            {
                return ParseResponse(outcome.Body, _timeProvider.GetUtcNow());
            }

            if (outcome.Status == HttpStatusCode.Unauthorized)
            {
                return ProviderResult.Fail(
                    ProviderHealth.AuthFailed,
                    "csrf_invalid",
                    "Antigravity CSRF token rejected.");
            }

            return ProviderResult.Fail(
                ProviderHealth.AppDown,
                "port_closed",
                $"Unexpected Antigravity response status: {(int?)outcome.Status}.");
        }
        catch (OperationCanceledException)
        {
            return ProviderResult.Fail(ProviderHealth.AppDown, "cancelled", "Antigravity request was cancelled.");
        }
        catch (Exception ex)
        {
            return ProviderResult.Fail(ProviderHealth.AppDown, "port_closed", ex.Message);
        }
    }

    public static ProviderResult ParseResponse(string json, DateTimeOffset fetchedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("userStatus", out var userStatus) ||
                userStatus.ValueKind != JsonValueKind.Object)
            {
                return ProviderResult.Fail(
                    ProviderHealth.ParseFailed,
                    "schema_changed",
                    "userStatus missing.");
            }

            var planName = "Unknown";
            if (userStatus.TryGetProperty("planStatus", out var planStatus) &&
                planStatus.ValueKind == JsonValueKind.Object &&
                planStatus.TryGetProperty("planInfo", out var planInfo) &&
                planInfo.ValueKind == JsonValueKind.Object &&
                planInfo.TryGetProperty("planName", out var planNameElement) &&
                planNameElement.ValueKind == JsonValueKind.String)
            {
                var value = planNameElement.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    planName = value!;
                }
            }

            string? tierId = null;
            if (userStatus.TryGetProperty("userTier", out var userTier) &&
                userTier.ValueKind == JsonValueKind.Object &&
                userTier.TryGetProperty("id", out var tierIdElement) &&
                tierIdElement.ValueKind == JsonValueKind.String)
            {
                tierId = tierIdElement.GetString();
            }

            if (!userStatus.TryGetProperty("cascadeModelConfigData", out var cascadeConfig) ||
                cascadeConfig.ValueKind != JsonValueKind.Object ||
                !cascadeConfig.TryGetProperty("clientModelConfigs", out var clientModelConfigs) ||
                clientModelConfigs.ValueKind != JsonValueKind.Array)
            {
                return ProviderResult.Fail(
                    ProviderHealth.ParseFailed,
                    "schema_changed",
                    "clientModelConfigs missing.");
            }

            var models = new List<ModelQuota>();
            foreach (var element in clientModelConfigs.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var modelId = GetString(element, "modelId") ?? string.Empty;
                var label = GetString(element, "label");
                var family = QuotaGrouper.ExtractFamily(modelId, label);

                if (string.IsNullOrWhiteSpace(modelId) && !string.IsNullOrWhiteSpace(label))
                {
                    modelId = family;
                }

                if (string.IsNullOrWhiteSpace(label))
                {
                    label = modelId;
                }

                double? remainingFraction = null;
                DateTimeOffset? resetAt = null;

                if (element.TryGetProperty("quotaInfo", out var quotaInfo) &&
                    quotaInfo.ValueKind == JsonValueKind.Object)
                {
                    if (quotaInfo.TryGetProperty("remainingFraction", out var remainingElement) &&
                        remainingElement.ValueKind == JsonValueKind.Number &&
                        remainingElement.TryGetDouble(out var remainingValue))
                    {
                        remainingFraction = remainingValue;
                    }

                    if (quotaInfo.TryGetProperty("resetTime", out var resetElement) &&
                        resetElement.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(
                            resetElement.GetString(),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var resetValue))
                    {
                        resetAt = resetValue;
                    }
                }

                models.Add(new ModelQuota(modelId, family, label!, remainingFraction, resetAt));
            }

            var (familyGroups, poolCount) = QuotaGrouper.GroupModels(models);

            return ProviderResult.Ok(
                new ProviderSnapshot(
                    ProviderId,
                    null,
                    new AntigravitySnapshot(planName, tierId, models, familyGroups, poolCount, fetchedAt),
                    fetchedAt));
        }
        catch (JsonException)
        {
            return ProviderResult.Fail(
                ProviderHealth.ParseFailed,
                "schema_changed",
                "Invalid Antigravity JSON response.");
        }
    }

    private async Task<SendOutcome> SendGetUserStatusAsync(AntigravityEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            var uri = $"https://127.0.0.1:{endpoint.Port}{ServicePath}";

            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.TryAddWithoutValidation("x-codeium-csrf-token", endpoint.CsrfToken ?? string.Empty);
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Content = new StringContent(RequestBody, Encoding.UTF8, "application/json");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            var body = response.StatusCode == HttpStatusCode.OK
                ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
                : null;

            return new SendOutcome(response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return new SendOutcome(null, null, ex);
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private readonly record struct SendOutcome(
        HttpStatusCode? Status,
        string? Body,
        Exception? Error);
}
