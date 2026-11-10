using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AiUsageDock.Core.Models;

public enum ProviderHealth
{
    Ok,
    Stale,
    AppDown,
    AuthFailed,
    NetworkFailed,
    ParseFailed
}

public sealed record BalanceInfo(string Currency, decimal Total, decimal Granted, decimal ToppedUp)
{
    public string Symbol => Currency.ToUpperInvariant() switch
    {
        "USD" => "$",
        "CNY" or "RMB" => "¥",
        "EUR" => "€",
        _ => Currency + " "
    };

    public string FormatTotal() =>
        string.Create(CultureInfo.InvariantCulture, $"{Symbol}{Total:F2}");
}

public sealed record BalanceHistoryPoint(DateTimeOffset Timestamp, string Currency, decimal Total);

public sealed record DeepSeekSnapshot(
    IReadOnlyList<BalanceInfo> Balances,
    bool IsAvailable,
    DateTimeOffset FetchedAt,
    decimal? TodaySpendEstimate = null,
    IReadOnlyList<BalanceHistoryPoint>? RecentHistory = null)
{
    public BalanceInfo Primary =>
        Balances.FirstOrDefault(b => string.Equals(b.Currency, "USD", StringComparison.OrdinalIgnoreCase))
        ?? Balances[0];

    public BalanceInfo GetByCurrency(string preferredCurrency) =>
        Balances.FirstOrDefault(b => string.Equals(b.Currency, preferredCurrency, StringComparison.OrdinalIgnoreCase))
        ?? Primary;
}

public sealed record ModelQuota(
    string ModelId,
    string Family,
    string Label,
    double? RemainingFraction,
    DateTimeOffset? ResetAt)
{
    public double? RemainingPercent => RemainingFraction.HasValue ? RemainingFraction.Value * 100.0 : null;

    public string FormatResetTime(DateTimeOffset now) => ResetFormatting.Format(ResetAt, now);
}

public static class ResetFormatting
{
    public static string Format(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is null)
        {
            return "-";
        }

        var resetLocal = resetAt.Value.ToLocalTime();
        var nowLocal = now.ToLocalTime();
        var diff = resetLocal - nowLocal;

        if (diff <= TimeSpan.Zero)
        {
            return "곧 리셋";
        }

        if (diff < TimeSpan.FromHours(24))
        {
            return diff < TimeSpan.FromHours(1)
                ? $"{diff.Minutes}분 후"
                : $"{(int)diff.TotalHours}시간 {diff.Minutes}분 후";
        }

        return resetLocal.ToString("MM/dd HH:mm 리셋", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// One pooled quota window from RetrieveUserQuotaSummary (e.g. gemini-weekly, 3p-5h).
/// </summary>
public sealed record QuotaBucket(
    string BucketId,
    string DisplayName,
    string Window,
    double? RemainingFraction,
    DateTimeOffset? ResetAt)
{
    public double? RemainingPercent => RemainingFraction * 100.0;

    public string FormatResetTime(DateTimeOffset now) => ResetFormatting.Format(ResetAt, now);
}

public sealed record QuotaGroupSummary(string DisplayName, IReadOnlyList<QuotaBucket> Buckets);

public sealed record ModelQuotaGroup(
    string Family,
    string DisplayName,
    double? RemainingFraction,
    DateTimeOffset? ResetAt,
    IReadOnlyList<ModelQuota> Models);

public sealed record AntigravitySnapshot(
    string PlanName,
    string? TierId,
    IReadOnlyList<ModelQuota> Models,
    IReadOnlyList<ModelQuotaGroup> FamilyGroups,
    int PoolCount,
    DateTimeOffset FetchedAt,
    IReadOnlyList<QuotaGroupSummary>? QuotaGroups = null);

public sealed record ProviderSnapshot(
    string ProviderId,
    DeepSeekSnapshot? DeepSeek,
    AntigravitySnapshot? Antigravity,
    DateTimeOffset FetchedAt);

public sealed record ProviderResult(
    ProviderSnapshot? Snapshot,
    ProviderHealth Health,
    string? ErrorCode = null,
    string? ErrorDetail = null)
{
    public static ProviderResult Ok(ProviderSnapshot snapshot) =>
        new(snapshot, ProviderHealth.Ok);

    public static ProviderResult Fail(ProviderHealth health, string errorCode, string? detail = null) =>
        new(null, health, errorCode, detail);
}
