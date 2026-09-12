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

    [Fact]
    public void FindClosestMatch_Kills_Stopword_Containment_Hijack()
    {
        // the~weather fired on unfiltered input words; the filter removes it.
        var commands = new[]
        {
            "open discord",
            "my discord nitro expired",
            "hows the weather"
        };

        var match = FuzzyMatcher.FindClosestMatch("open the discord", commands, 0.80f);

        Assert.NotNull(match);
        Assert.Equal("open discord", match!.MatchedCommand);
    }

    [Fact]
    public void FindClosestMatch_Restores_Deuniquified_Command_Via_Shared_Keyword()
    {
        // "discord" lives in two commands; the shared stage disambiguates.
        var commands = new[]
        {
            "open discord",
            "my discord nitro expired"
        };

        var match = FuzzyMatcher.FindClosestMatch("show discord", commands, 0.80f);

        Assert.NotNull(match);
        Assert.Equal("open discord", match!.MatchedCommand);
        Assert.True(match.Confidence >= 0.90f, $"Expected confidence >= 0.90, got {match.Confidence:0.000}");
    }

    [Fact]
    public void FindClosestMatch_Rejects_Coincidence_Containment()
    {
        // chat in viarchat (ratio 0.50, infix) is coincidence, not affixation.
        var commands = new[]
        {
            "open the viarchat website",
            "open the steam chat"
        };

        var match = FuzzyMatcher.FindClosestMatch("open the steam chat", commands, 0.80f);

        Assert.NotNull(match);
        Assert.Equal("open the steam chat", match!.MatchedCommand);
    }

    [Fact]
    public void FindClosestMatch_Keeps_Affixed_Compounds()
    {
        // five/fiver (0.80) and pony/ponytail (prefix, 0.50) are affixation.
        var commands = new[]
        {
            "open pony town",
            "open fiver"
        };

        var pony = FuzzyMatcher.FindClosestMatch("open ponytail", commands, 0.80f);
        Assert.NotNull(pony);
        Assert.Equal("open pony town", pony!.MatchedCommand);

        var five = FuzzyMatcher.FindClosestMatch("open five", commands, 0.80f);
        Assert.NotNull(five);
        Assert.Equal("open fiver", five!.MatchedCommand);
    }

    [Fact]
    public void FindClosestMatch_Routes_Media_Letter_Aliases()
    {
        var commands = new[]
        {
            "open hbo",
            "open hulu",
            "open roblox"
        };

        var hbo = FuzzyMatcher.FindClosestMatch("open h b o", commands, 0.80f);
        Assert.NotNull(hbo);
        Assert.Equal("open hbo", hbo!.MatchedCommand);

        var hulu = FuzzyMatcher.FindClosestMatch("open holy", commands, 0.80f);
        Assert.NotNull(hulu);
        Assert.Equal("open hulu", hulu!.MatchedCommand);
    }
}
