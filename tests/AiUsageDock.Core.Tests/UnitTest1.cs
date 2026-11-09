using System;
using System.Linq;
using AiUsageDock.Core.Models;
using AiUsageDock.Core.Providers.Antigravity;
using AiUsageDock.Core.Providers.DeepSeek;
using AiUsageDock.Core.Scheduling;
using Xunit;

namespace AiUsageDock.Core.Tests;

public class UnitTest1
{
    [Fact]
    public void DeepSeekParseResponse_ParsesStringDecimals_AndFormatsUsd()
    {
        var fetchedAt = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        const string json = """
        {
          "is_available": true,
          "balance_infos": [
            {
              "currency": "USD",
              "total_balance": "12.34",
              "granted_balance": "2.34",
              "topped_up_balance": "10.00"
            }
          ]
        }
        """;

        var result = DeepSeekAdapter.ParseResponse(json, fetchedAt);

        Assert.Equal(ProviderHealth.Ok, result.Health);
        var primary = result.Snapshot!.DeepSeek!.Primary;
        Assert.Equal(12.34m, primary.Total);
        Assert.Equal("$12.34", primary.FormatTotal());
    }

    [Fact]
    public void DeepSeekParseResponse_EmptyBalanceInfos_ReturnsSchemaChanged()
    {
        var result = DeepSeekAdapter.ParseResponse(
            """{ "is_available": true, "balance_infos": [] }""",
            DateTimeOffset.UtcNow);

        Assert.Equal(ProviderHealth.ParseFailed, result.Health);
        Assert.Equal("schema_changed", result.ErrorCode);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void DeepSeekCalculateTodaySpend_SumsDrops_AndIgnoresTopUps()
    {
        var nowLocal = DateTime.Today.AddHours(23).AddMinutes(59);
        var history = new[]
        {
            new BalanceHistoryPoint(new DateTimeOffset(DateTime.Today.AddHours(9)), "USD", 20.00m),
            new BalanceHistoryPoint(new DateTimeOffset(DateTime.Today.AddHours(18)), "USD", 17.66m),
            new BalanceHistoryPoint(new DateTimeOffset(DateTime.Today.AddHours(20)), "USD", 19.00m),
            new BalanceHistoryPoint(new DateTimeOffset(DateTime.Today.AddHours(19)), "CNY", 10.00m),
            new BalanceHistoryPoint(new DateTimeOffset(DateTime.Today.AddHours(21)), "CNY", 12.00m),
            new BalanceHistoryPoint(new DateTimeOffset(DateTime.Today.AddDays(1).AddMinutes(1)), "USD", 1.00m)
        };

        var spend = DeepSeekAdapter.CalculateTodaySpend(history, nowLocal, "USD");

        Assert.Equal(2.34m, spend);
    }

    [Fact]
    public void AntigravityParseResponse_GroupsModels_AndKeepsUtcResetTimes()
    {
        var fetchedAt = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        const string json = """
        {
          "userStatus": {
            "planStatus": {
              "planInfo": { "planName": "Pro" }
            },
            "userTier": { "id": "g1-pro-tier" },
            "cascadeModelConfigData": {
              "clientModelConfigs": [
                {
                  "label": "Gemini 4 Argon (High)",
                  "modelId": "gemini-4-argon-high",
                  "quotaInfo": { "remainingFraction": 0.8723, "resetTime": "2026-11-15T13:54:57Z" }
                },
                {
                  "label": "Gemini 3.8 Flash (High)",
                  "modelId": "gemini-3.8-flash-high",
                  "quotaInfo": { "remainingFraction": 0.9954, "resetTime": "2026-11-09T03:55:00Z" }
                }
              ]
            }
          }
        }
        """;

        var result = AntigravityAdapter.ParseResponse(json, fetchedAt);

        Assert.Equal(ProviderHealth.Ok, result.Health);
        var antigravity = result.Snapshot!.Antigravity!;
        Assert.Equal("Pro", antigravity.PlanName);
        Assert.Equal(2, antigravity.PoolCount);
        Assert.Equal(2, antigravity.FamilyGroups.Count);
        Assert.All(antigravity.Models, model => Assert.Equal(TimeSpan.Zero, model.ResetAt!.Value.Offset));
    }

    [Fact]
    public void UsageStore_PreservesLastGoodDeepSeek_AfterNetworkFailure()
    {
        var fetchedAt = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        var ok = DeepSeekAdapter.ParseResponse(
            """{ "is_available": true, "balance_infos": [{ "currency": "USD", "total_balance": "12.34" }] }""",
            fetchedAt);

        var store = new UsageStore();
        store.Apply("deepseek", ok);

        Assert.NotNull(store.LastGoodDeepSeek);
        Assert.Equal(12.34m, store.LastGoodDeepSeek!.Primary.Total);

        store.Apply("deepseek", ProviderResult.Fail(ProviderHealth.NetworkFailed, "http_5xx"));

        Assert.NotNull(store.LastGoodDeepSeek);
        Assert.Equal(12.34m, store.LastGoodDeepSeek!.Primary.Total);
    }

    [Fact]
    public void AntigravityParseResponse_WithoutModelId_DerivesFamiliesFromLabels()
    {
        var fetchedAt = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        const string json = """
        {
          "userStatus": {
            "planStatus": {
              "planInfo": { "planName": "Pro" }
            },
            "cascadeModelConfigData": {
              "clientModelConfigs": [
                {
                  "label": "Gemini 4 Argon (High)",
                  "modelOrAlias": "MODEL_1",
                  "quotaInfo": { "remainingFraction": 0.8723, "resetTime": "2026-11-15T13:54:57Z" }
                },
                {
                  "label": "Gemini 3.8 Flash (High)",
                  "modelOrAlias": "MODEL_2",
                  "quotaInfo": { "remainingFraction": 0.9954, "resetTime": "2026-11-09T03:55:00Z" }
                },
                {
                  "label": "Claude Opus 5.5 (Low)",
                  "modelOrAlias": "MODEL_3",
                  "quotaInfo": { "remainingFraction": 0.2500, "resetTime": "2026-11-10T08:00:00Z" }
                }
              ]
            }
          }
        }
        """;

        var result = AntigravityAdapter.ParseResponse(json, fetchedAt);

        Assert.Equal(ProviderHealth.Ok, result.Health);
        var antigravity = result.Snapshot!.Antigravity!;
        Assert.Equal(3, antigravity.FamilyGroups.Count);
        var families = antigravity.FamilyGroups.Select(group => group.Family).ToList();
        Assert.Contains("gemini-4-argon", families);
        Assert.Contains("gemini-3.8-flash", families);
        Assert.Contains("claude-opus-5-5", families);
    }
}
