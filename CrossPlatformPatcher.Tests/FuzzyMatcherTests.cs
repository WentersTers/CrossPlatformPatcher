using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

[Collection(SequentialTestCollection.CollectionName)]
public sealed class FuzzyMatcherTests
{
    [Fact]
    public void NormalizePhoneticAliases_HandlesPluralizationVariants()
    {
        var normalized = FuzzyMatcher.NormalizePhoneticAliases("open road block");

        Assert.Equal("open roblox", normalized);
    }

    [Fact]
    public void FindClosestMatch_Matches_WhenOnlyPluralizationDiffers()
    {
        var commands = new[]
        {
            "launch photo filters",
            "open spotify",
            "open settings"
        };

        var match = FuzzyMatcher.FindClosestMatch("launch photo filter", commands, 0.80f);

        Assert.NotNull(match);
        Assert.Equal("launch photo filters", match!.MatchedCommand);
        Assert.True(match.Confidence >= 0.90f, $"Expected confidence >= 0.90, got {match.Confidence:0.000}");
    }

    [Fact]
    public void FindClosestMatch_UsesWordOverlap_ForPluralizationAcrossPhrase()
    {
        var commands = new[]
        {
            "enable debug logs",
            "enable stealth mode"
        };

        var match = FuzzyMatcher.FindClosestMatch("enable debug log", commands, 0.80f);

        Assert.NotNull(match);
        Assert.Equal("enable debug logs", match!.MatchedCommand);
        Assert.True(match.Confidence >= 0.85f, $"Expected confidence >= 0.85, got {match.Confidence:0.000}");
    }

    [Fact]
    public void FindClosestMatch_Does_Not_Match_Stopword_Fragment_To_Keyword()
    {
        var commands = new[]
        {
            "open the browser",
            "hows the weather"
        };

        var match = FuzzyMatcher.FindClosestMatch("the browse", commands, 0.80f);

        Assert.NotNull(match);
        Assert.Equal("open the browser", match!.MatchedCommand);
    }
}
