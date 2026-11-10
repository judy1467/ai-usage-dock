using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiUsageDock.Core.Models;
using AiUsageDock.Core.Providers.Antigravity;

namespace AiUsageDock.Core.Storage;

/// <summary>
/// Last-known-good snapshots, persisted so the dock can render immediately after a restart
/// (DESIGN.md 5.5). No secrets or PII are stored here.
/// </summary>
public sealed class CacheStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public CacheStore(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        _path = Path.Combine(directoryPath, "cache.json");
    }

    public CachedUsage Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return CachedUsage.Empty;
            }

            var json = File.ReadAllText(_path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return CachedUsage.Empty;
            }

            var file = JsonSerializer.Deserialize<CacheFile>(json, Options);
            if (file is null || file.Schema != 1)
            {
                return CachedUsage.Empty;
            }

            return new CachedUsage(ToDeepSeek(file.DeepSeek), ToAntigravity(file.Antigravity));
        }
        catch (Exception)
        {
            // A corrupt cache must never prevent startup.
            return CachedUsage.Empty;
        }
    }

    public void Save(DeepSeekSnapshot? deepSeek, AntigravitySnapshot? antigravity)
    {
        try
        {
            var file = new CacheFile(1, FromDeepSeek(deepSeek), FromAntigravity(antigravity));
            var json = JsonSerializer.Serialize(file, Options);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // Cache writes are best effort.
        }
    }

    private static CachedDeepSeek? FromDeepSeek(DeepSeekSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return new CachedDeepSeek(
            snapshot.FetchedAt,
            snapshot.IsAvailable,
            snapshot.Balances
                .Select(b => new CachedBalance(b.Currency, b.Total, b.Granted, b.ToppedUp))
                .ToList());
    }

    private static CachedAntigravity? FromAntigravity(AntigravitySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return new CachedAntigravity(
            snapshot.FetchedAt,
            snapshot.PlanName,
            snapshot.TierId,
            snapshot.Models
                .Select(m => new CachedModel(m.ModelId, m.Family, m.Label, m.RemainingFraction, m.ResetAt))
                .ToList(),
            snapshot.QuotaGroups?
                .Select(g => new CachedGroup(
                    g.DisplayName,
                    g.Buckets
                        .Select(b => new CachedBucket(b.BucketId, b.DisplayName, b.Window, b.RemainingFraction, b.ResetAt))
                        .ToList()))
                .ToList());
    }

    private static DeepSeekSnapshot? ToDeepSeek(CachedDeepSeek? cached)
    {
        if (cached is null || cached.Balances.Count == 0)
        {
            return null;
        }

        var balances = cached.Balances
            .Select(b => new BalanceInfo(b.Currency, b.Total, b.Granted, b.ToppedUp))
            .ToList();

        return new DeepSeekSnapshot(balances, cached.IsAvailable, cached.FetchedAt);
    }

    private static AntigravitySnapshot? ToAntigravity(CachedAntigravity? cached)
    {
        if (cached is null || cached.Models.Count == 0)
        {
            return null;
        }

        var models = cached.Models
            .Select(m => new ModelQuota(m.ModelId, m.Family, m.Label, m.RemainingFraction, m.ResetAt))
            .ToList();

        var (groups, poolCount) = QuotaGrouper.GroupModels(models);

        IReadOnlyList<QuotaGroupSummary>? quotaGroups = null;
        if (cached.QuotaGroups is { Count: > 0 })
        {
            quotaGroups = cached.QuotaGroups
                .Select(g => new QuotaGroupSummary(
                    g.DisplayName,
                    g.Buckets
                        .Select(b => new QuotaBucket(b.BucketId, b.DisplayName, b.Window, b.RemainingFraction, b.ResetAt))
                        .ToList()))
                .ToList();
        }

        return new AntigravitySnapshot(cached.PlanName, cached.TierId, models, groups, poolCount, cached.FetchedAt, quotaGroups);
    }

    private sealed record CacheFile(int Schema, CachedDeepSeek? DeepSeek, CachedAntigravity? Antigravity);

    private sealed record CachedBalance(string Currency, decimal Total, decimal Granted, decimal ToppedUp);

    private sealed record CachedModel(string ModelId, string Family, string Label, double? RemainingFraction, DateTimeOffset? ResetAt);

    private sealed record CachedBucket(string BucketId, string DisplayName, string Window, double? RemainingFraction, DateTimeOffset? ResetAt);

    private sealed record CachedGroup(string DisplayName, List<CachedBucket> Buckets);

    private sealed record CachedDeepSeek(DateTimeOffset FetchedAt, bool IsAvailable, List<CachedBalance> Balances);

    private sealed record CachedAntigravity(
        DateTimeOffset FetchedAt,
        string PlanName,
        string? TierId,
        List<CachedModel> Models,
        List<CachedGroup>? QuotaGroups);
}

public sealed record CachedUsage(DeepSeekSnapshot? DeepSeek, AntigravitySnapshot? Antigravity)
{
    public static CachedUsage Empty { get; } = new(null, null);

    public bool HasAny => DeepSeek is not null || Antigravity is not null;
}
