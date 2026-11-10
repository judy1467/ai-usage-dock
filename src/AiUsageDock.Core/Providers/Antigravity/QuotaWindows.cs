using System;
using System.Linq;
using AiUsageDock.Core.Models;

namespace AiUsageDock.Core.Providers.Antigravity;

/// <summary>5-hour and weekly quota windows for one model family.</summary>
public sealed record QuotaWindowPair(QuotaBucket? Session, QuotaBucket? Weekly)
{
    public bool HasAny => Session is not null || Weekly is not null;

    /// <summary>The binding window (the smaller of the two) — what actually limits usage right now.</summary>
    public double? Effective =>
        Session?.RemainingFraction is double session && Weekly?.RemainingFraction is double weekly
            ? Math.Min(session, weekly)
            : Weekly?.RemainingFraction ?? Session?.RemainingFraction;
}

/// <summary>
/// Maps a model family to the pooled quota groups returned by RetrieveUserQuotaSummary.
/// </summary>
public static class QuotaWindows
{
    public static QuotaWindowPair Resolve(AntigravitySnapshot? snapshot, string? family)
    {
        var groups = snapshot?.QuotaGroups;
        if (groups is null || groups.Count == 0 || string.IsNullOrWhiteSpace(family))
        {
            return new QuotaWindowPair(null, null);
        }

        var key = family.ToLowerInvariant();

        QuotaGroupSummary? group;
        if (key.Contains("argon", StringComparison.Ordinal))
        {
            group = groups.FirstOrDefault(g => g.Buckets.Any(b => b.BucketId.Contains("argon", StringComparison.OrdinalIgnoreCase)));
        }
        else if (key.StartsWith("gemini", StringComparison.Ordinal))
        {
            group = groups.FirstOrDefault(g => g.Buckets.Any(b =>
                b.BucketId.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) &&
                !b.BucketId.Contains("argon", StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            group = groups.FirstOrDefault(g => g.Buckets.Any(b =>
                b.BucketId.StartsWith("3p-", StringComparison.OrdinalIgnoreCase)));
        }

        if (group is null)
        {
            return new QuotaWindowPair(null, null);
        }

        var session = group.Buckets.FirstOrDefault(b => string.Equals(b.Window, "5h", StringComparison.OrdinalIgnoreCase));
        var weekly = group.Buckets.FirstOrDefault(b => string.Equals(b.Window, "weekly", StringComparison.OrdinalIgnoreCase));
        return new QuotaWindowPair(session, weekly);
    }

    public static string FormatPercent(double? fraction) =>
        fraction is double value ? $"{value * 100:F0}%" : "–";

    /// <summary>Formats "5h / weekly" the way the taskbar tile and flyout rows show it.</summary>
    public static string FormatPair(QuotaWindowPair pair) =>
        $"{FormatPercent(pair.Session?.RemainingFraction)} / {FormatPercent(pair.Weekly?.RemainingFraction)}";
}
