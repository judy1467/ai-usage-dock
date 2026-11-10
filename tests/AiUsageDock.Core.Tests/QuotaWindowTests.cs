using System;
using AiUsageDock.Core.Models;
using AiUsageDock.Core.Providers.Antigravity;
using Xunit;

namespace AiUsageDock.Core.Tests;

public class QuotaWindowTests
{
    private const string SummaryJson = """
    {"response":{"groups":[
      {"displayName":"Gemini Models","buckets":[
         {"bucketId":"gemini-weekly","displayName":"Weekly Limit Remaining","window":"weekly","remainingFraction":0.8161635,"resetTime":"2026-11-15T13:55:18Z"},
         {"bucketId":"gemini-5h","displayName":"Five Hour Limit Remaining","window":"5h","remainingFraction":1,"resetTime":"2026-11-10T16:23:25Z"}]},
      {"displayName":"Claude and GPT models","buckets":[
         {"bucketId":"3p-weekly","displayName":"Weekly Limit Remaining","window":"weekly","remainingFraction":0.4947604,"resetTime":"2026-11-16T13:09:18Z"},
         {"bucketId":"3p-5h","displayName":"Five Hour Limit Remaining","window":"5h","remainingFraction":1,"resetTime":"2026-11-10T16:23:25Z"}]},
      {"displayName":"Gemini 4 Argon","buckets":[
         {"bucketId":"gemini-argon-wtu-weekly","displayName":"Gemini 4 Argon Weekly Limit","window":"weekly","remainingFraction":0.0027796,"resetTime":"2026-11-15T13:54:57Z"}]}
    ]}}
    """;

    [Fact]
    public void ParseQuotaSummary_ReadsGroupsAndBuckets()
    {
        var groups = AntigravityAdapter.ParseQuotaSummary(SummaryJson);

        Assert.Equal(3, groups.Count);
        Assert.Equal(2, groups[0].Buckets.Count);
        Assert.Equal("gemini-weekly", groups[0].Buckets[0].BucketId);
        Assert.Equal("weekly", groups[0].Buckets[0].Window);
        Assert.Equal(0.8161635, groups[0].Buckets[0].RemainingFraction!.Value, 6);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 15, 13, 55, 18, TimeSpan.Zero),
            groups[0].Buckets[0].ResetAt!.Value.ToUniversalTime());
    }

    [Fact]
    public void ParseQuotaSummary_Malformed_ReturnsEmpty()
    {
        Assert.Empty(AntigravityAdapter.ParseQuotaSummary("{ not json"));
        Assert.Empty(AntigravityAdapter.ParseQuotaSummary("{}"));
    }

    [Fact]
    public void Resolve_MapsFamiliesToPools()
    {
        var snapshot = new AntigravitySnapshot(
            "Pro", null, Array.Empty<ModelQuota>(), Array.Empty<ModelQuotaGroup>(), 0,
            DateTimeOffset.UtcNow, AntigravityAdapter.ParseQuotaSummary(SummaryJson));

        var flash = QuotaWindows.Resolve(snapshot, "gemini-3.8-flash");
        Assert.Equal(1.0, flash.Session!.RemainingFraction!.Value, 6);
        Assert.Equal(0.8161635, flash.Weekly!.RemainingFraction!.Value, 6);
        Assert.Equal("100% / 82%", QuotaWindows.FormatPair(flash));
        Assert.Equal(0.8161635, flash.Effective!.Value, 6);

        var claude = QuotaWindows.Resolve(snapshot, "claude-opus-5-5");
        Assert.Equal(0.4947604, claude.Weekly!.RemainingFraction!.Value, 6);
        Assert.Equal("100% / 49%", QuotaWindows.FormatPair(claude));

        var argon = QuotaWindows.Resolve(snapshot, "gemini-4-argon");
        Assert.Null(argon.Session);
        Assert.Equal(0.0027796, argon.Weekly!.RemainingFraction!.Value, 6);
        Assert.Equal("– / 0%", QuotaWindows.FormatPair(argon));
        Assert.Equal(0.0027796, argon.Effective!.Value, 6);
    }

    [Fact]
    public void Resolve_WithoutSummary_ReturnsEmptyPair()
    {
        var snapshot = new AntigravitySnapshot(
            "Pro", null, Array.Empty<ModelQuota>(), Array.Empty<ModelQuotaGroup>(), 0, DateTimeOffset.UtcNow);

        var pair = QuotaWindows.Resolve(snapshot, "gemini-4-argon");

        Assert.False(pair.HasAny);
        Assert.Null(pair.Effective);
        Assert.Equal("– / –", QuotaWindows.FormatPair(pair));
    }
}

