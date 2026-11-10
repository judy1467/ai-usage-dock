using System;
using System.Collections.Generic;
using System.IO;
using AiUsageDock.Core.Models;
using AiUsageDock.Core.Providers.Antigravity;
using AiUsageDock.Core.Storage;
using Xunit;

namespace AiUsageDock.Core.Tests;

public sealed class CacheStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ai-usage-dock-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_WithoutFile_ReturnsEmpty()
    {
        var store = new CacheStore(_dir);

        var cached = store.Load();

        Assert.False(cached.HasAny);
        Assert.Null(cached.DeepSeek);
        Assert.Null(cached.Antigravity);
    }

    [Fact]
    public void SaveAndLoad_DeepSeekRoundTrips()
    {
        var store = new CacheStore(_dir);
        var fetchedAt = new DateTimeOffset(2026, 11, 9, 1, 5, 0, TimeSpan.Zero);
        var snapshot = new DeepSeekSnapshot(
            new[] { new BalanceInfo("USD", 12.34m, 2.34m, 10.00m) },
            IsAvailable: true,
            FetchedAt: fetchedAt);

        store.Save(snapshot, null);
        var loaded = store.Load();

        Assert.NotNull(loaded.DeepSeek);
        Assert.Equal(12.34m, loaded.DeepSeek!.Primary.Total);
        Assert.True(loaded.DeepSeek.IsAvailable);
        Assert.Equal(fetchedAt, loaded.DeepSeek.FetchedAt);
        Assert.Null(loaded.Antigravity);
    }

    [Fact]
    public void SaveAndLoad_AntigravityRecomputesGroups()
    {
        var store = new CacheStore(_dir);
        var reset = new DateTimeOffset(2026, 11, 15, 13, 54, 57, TimeSpan.Zero);
        var reset2 = new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);
        var models = new List<ModelQuota>
        {
            new("gemini-4-argon-high", "gemini-4-argon", "Gemini 4 Argon (High)", 0.87, reset),
            new("gemini-4-argon-low", "gemini-4-argon", "Gemini 4 Argon (Low)", 0.87, reset),
            new("claude-opus-5-5-high", "claude-opus-5-5", "Claude Opus 5.5 (High)", 1.0, reset2),
        };
        var (groups, pools) = QuotaGrouper.GroupModels(models);
        var quotaGroups = new[]
        {
            new QuotaGroupSummary("Gemini Models", new[]
            {
                new QuotaBucket("gemini-weekly", "Weekly Limit Remaining", "weekly", 0.82, reset),
                new QuotaBucket("gemini-5h", "Five Hour Limit Remaining", "5h", 1.0, reset2),
            }),
        };
        var snapshot = new AntigravitySnapshot("Pro", "g1-pro-tier", models, groups, pools, DateTimeOffset.UtcNow, quotaGroups);

        store.Save(null, snapshot);
        var loaded = store.Load();

        Assert.NotNull(loaded.Antigravity);
        var ag = loaded.Antigravity!;
        Assert.Equal("Pro", ag.PlanName);
        Assert.Equal(3, ag.Models.Count);
        Assert.Equal(2, ag.FamilyGroups.Count);
        Assert.Equal(2, ag.PoolCount);
        Assert.Equal(reset, ag.FamilyGroups[0].ResetAt!.Value.ToUniversalTime());
        Assert.NotNull(ag.QuotaGroups);
        Assert.Single(ag.QuotaGroups!);
        Assert.Equal(2, ag.QuotaGroups![0].Buckets.Count);
        Assert.Equal("gemini-weekly", ag.QuotaGroups[0].Buckets[0].BucketId);
        Assert.Equal(0.82, ag.QuotaGroups[0].Buckets[0].RemainingFraction!.Value, 6);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "cache.json"), "{ not json");
        var store = new CacheStore(_dir);

        var cached = store.Load();

        Assert.False(cached.HasAny);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
