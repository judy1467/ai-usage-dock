using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AiUsageDock.Core.Models;

namespace AiUsageDock.Core.Providers.Antigravity
{
    public static class QuotaGrouper
    {
        private static readonly Regex TrailingTierIdRegex =
            new Regex("-(low|medium|high)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingTierLabelRegex =
            new Regex(@"\s*\((Low|Medium|High)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TierSuffixRegex =
            new Regex(@"(?:-(low|medium|high)|\((low|medium|high)\))\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex WhitespaceRegex =
            new Regex(@"\s+", RegexOptions.Compiled);

        public static string ExtractFamily(string? modelId, string? label = null)
        {
            var trimmedId = modelId?.Trim();

            if (!string.IsNullOrWhiteSpace(trimmedId) &&
                !trimmedId.StartsWith("MODEL_", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = trimmedId.ToLowerInvariant();
                return TrailingTierIdRegex.Replace(trimmed, string.Empty);
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                return string.Empty;
            }

            var slug = TrailingTierLabelRegex.Replace(label, string.Empty);
            slug = TrailingTierIdRegex.Replace(slug, string.Empty);
            slug = slug.Trim().ToLowerInvariant();
            slug = WhitespaceRegex.Replace(slug, "-");

            if (slug.StartsWith("claude-", StringComparison.Ordinal))
            {
                slug = slug.Replace(".", "-");
            }

            return slug;
        }

        public static string CleanFamilyDisplayName(string label, string fallbackFamily)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return fallbackFamily;
            }

            var cleaned = TrailingTierLabelRegex.Replace(label, string.Empty).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? fallbackFamily : cleaned;
        }

        public static (IReadOnlyList<ModelQuotaGroup> FamilyGroups, int PoolCount) GroupModels(
            IReadOnlyList<ModelQuota> models)
        {
            if (models == null || models.Count == 0)
            {
                return (Array.Empty<ModelQuotaGroup>(), 0);
            }

            var poolKeys = new HashSet<(double, DateTimeOffset?)>();
            foreach (var model in models)
            {
                if (model?.RemainingFraction is double remaining)
                {
                    var resetAt = model.ResetAt?.ToUniversalTime();
                    poolKeys.Add((Math.Round(remaining, 6), resetAt));
                }
            }

            var groups = new List<ModelQuotaGroup>();

            foreach (var familyGroup in models
                         .Where(m => m != null)
                         .GroupBy(m => m.Family ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                var familyKey = familyGroup.Key;
                var familyModels = familyGroup.ToList();

                var representative = familyModels.FirstOrDefault(m => m.RemainingFraction.HasValue)
                                     ?? familyModels[0];

                var displayName = CleanFamilyDisplayName(representative.Label, familyKey);

                var ordered = familyModels
                    .OrderBy(m => TierOrder(m))
                    .ToList();

                groups.Add(new ModelQuotaGroup(
                    familyKey,
                    displayName,
                    representative.RemainingFraction,
                    representative.ResetAt,
                    ordered));
            }

            return (groups, poolKeys.Count);
        }

        private static int TierOrder(ModelQuota model)
        {
            var tier = ExtractTier(model?.ModelId) ?? ExtractTier(model?.Label);
            return tier switch
            {
                "high" => 0,
                "medium" => 1,
                "low" => 2,
                _ => 3
            };
        }

        private static string? ExtractTier(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var match = TierSuffixRegex.Match(value.Trim());
            if (!match.Success)
            {
                return null;
            }

            var tier = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return tier.ToLowerInvariant();
        }
    }
}
