using System;
using AiUsageDock.Core.Abstractions;
using AiUsageDock.Core.Models;

namespace AiUsageDock.Core.Scheduling;

/// <summary>
/// Holds the latest provider results while preserving the last successful snapshots.
/// DESIGN.md 7.4: values never disappear just because a fetch fails.
/// </summary>
public sealed class UsageStore
{
    private readonly object _gate = new();

    public ProviderResult? DeepSeekResult { get; private set; }
    public DeepSeekSnapshot? LastGoodDeepSeek { get; private set; }
    public ProviderResult? AntigravityResult { get; private set; }
    public AntigravitySnapshot? LastGoodAntigravity { get; private set; }

    public event Action? Changed;

    public void Apply(string providerId, ProviderResult result)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        var updated = false;

        lock (_gate)
        {
            if (IsDeepSeek(providerId))
            {
                DeepSeekResult = result;

                var deepSeekSnapshot = result.Snapshot?.DeepSeek;
                if (deepSeekSnapshot is not null)
                {
                    LastGoodDeepSeek = deepSeekSnapshot;
                }

                updated = true;
            }
            else if (IsAntigravity(providerId))
            {
                AntigravityResult = result;

                var antigravitySnapshot = result.Snapshot?.Antigravity;
                if (antigravitySnapshot is not null)
                {
                    LastGoodAntigravity = antigravitySnapshot;
                }

                updated = true;
            }
        }

        if (updated)
        {
            Changed?.Invoke();
        }
    }

    public bool IsDeepSeekStale(DateTimeOffset now, TimeSpan interval)
    {
        lock (_gate)
        {
            return IsStale(LastGoodDeepSeek?.FetchedAt, now, interval);
        }
    }

    public bool IsAntigravityStale(DateTimeOffset now, TimeSpan interval)
    {
        lock (_gate)
        {
            return IsStale(LastGoodAntigravity?.FetchedAt, now, interval);
        }
    }

    public ProviderHealth GetOverallHealth(
        DateTimeOffset now,
        TimeSpan deepSeekInterval,
        TimeSpan antigravityInterval)
    {
        lock (_gate)
        {
            var worst = ProviderHealth.Ok;
            var hasActiveProvider = false;

            if (DeepSeekResult is not null || LastGoodDeepSeek is not null)
            {
                hasActiveProvider = true;

                var deepSeekHealth = GetProviderHealth(
                    DeepSeekResult,
                    IsStale(LastGoodDeepSeek?.FetchedAt, now, deepSeekInterval));

                worst = Worse(worst, deepSeekHealth);
            }

            if (AntigravityResult is not null || LastGoodAntigravity is not null)
            {
                hasActiveProvider = true;

                var antigravityHealth = GetProviderHealth(
                    AntigravityResult,
                    IsStale(LastGoodAntigravity?.FetchedAt, now, antigravityInterval));

                worst = Worse(worst, antigravityHealth);
            }

            return hasActiveProvider ? worst : ProviderHealth.Ok;
        }
    }

    private static ProviderHealth GetProviderHealth(ProviderResult? result, bool isStale)
    {
        var health = result?.Health ?? ProviderHealth.Ok;
        return isStale ? Worse(health, ProviderHealth.Stale) : health;
    }

    private static bool IsStale(DateTimeOffset? fetchedAt, DateTimeOffset now, TimeSpan interval)
    {
        if (fetchedAt is null || interval <= TimeSpan.Zero)
        {
            return false;
        }

        return now - fetchedAt.Value > interval * 2;
    }

    private static ProviderHealth Worse(ProviderHealth left, ProviderHealth right)
    {
        return Severity(right) > Severity(left) ? right : left;
    }

    private static int Severity(ProviderHealth health)
    {
        return health switch
        {
            ProviderHealth.AuthFailed => 4,
            ProviderHealth.ParseFailed => 4,
            ProviderHealth.NetworkFailed => 4,
            ProviderHealth.AppDown => 3,
            ProviderHealth.Stale => 2,
            _ => 0
        };
    }

    private static bool IsDeepSeek(string providerId)
    {
        return string.Equals(providerId, "deepseek", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAntigravity(string providerId)
    {
        return string.Equals(providerId, "antigravity", StringComparison.OrdinalIgnoreCase);
    }
}

public static class BackoffPolicy
{
    public static TimeSpan ComputeDelay(
        int consecutiveFailures,
        TimeSpan normalInterval,
        TimeSpan baseBackoff,
        TimeSpan maxBackoff)
    {
        if (consecutiveFailures <= 0)
        {
            return normalInterval;
        }

        if (baseBackoff <= TimeSpan.Zero)
        {
            return normalInterval;
        }

        if (maxBackoff < TimeSpan.Zero)
        {
            maxBackoff = TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, consecutiveFailures - 1);
        var ticks = baseBackoff.Ticks * multiplier;

        if (double.IsNaN(ticks) || double.IsInfinity(ticks) || ticks >= maxBackoff.Ticks)
        {
            return maxBackoff;
        }

        var delay = TimeSpan.FromTicks((long)ticks);
        return delay > maxBackoff ? maxBackoff : delay;
    }
}
