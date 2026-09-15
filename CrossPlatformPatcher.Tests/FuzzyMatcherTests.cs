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
    public void FindClosestMatch_Rejects_Suffix_Coincidence_At_Floor()
    {
        // chat IS a suffix of viarchat at exactly 0.50: the affix floor is
        // prefix-only, so no partial route may fire here.
        var commands = new[]
        {
            "open the viarchat website",
            "open torch"
        };

        var match = FuzzyMatcher.FindClosestMatch("chat", commands, 0.80f);

        Assert.True(
            match == null || match.MatchedCommand != "open the viarchat website",
            "suffix coincidence must not route to viarchat, got: " +
            (match == null ? "null" : match.MatchedCommand));
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

    [Fact]
    public void FindClosestMatch_Shared_Beats_Partial_On_Wake_Debris()
    {
        // Order-discriminating vector (Windows census pie-preemption):
        // partial-first routed "pie" ~ "piece" to furniture; twin order
        // (shared first) resolves play+some+music. Live-observed input.
        var commands = new[]
        {
            "play some music",
            "if you would be a piece of furniture what would you be"
        };

        var match = FuzzyMatcher.FindClosestMatch("the pie comb play some music music down", commands, 0.80f);

        Assert.NotNull(match);
        Assert.Equal("play some music", match!.MatchedCommand);
    }

    [Fact]
    public void FindClosestMatch_Wake_Debris_Does_Not_Fire_Partial()
    {
        // "pie"/"comb"/"calm" are stopwords (manifest-gated, never command
        // vocabulary): debris-only transcripts must safe-reject, never route.
        // Pre-fix this partial-fired pie~piece to furniture.
        var commands = new[]
        {
            "open youtube",
            "if you would be a piece of furniture what would you be"
        };

        var match = FuzzyMatcher.FindClosestMatch("hey pie comb", commands, 0.80f);

        Assert.True(
            match == null || match.MatchedCommand != "if you would be a piece of furniture what would you be",
            "debris must not route to furniture, got: " +
            (match == null ? "null" : match.MatchedCommand));
    }

    [Fact]
    public void FindClosestMatch_Partial_Still_Fires_Without_Shared_Content()
    {
        // The reorder must not kill legitimate partials: with no exact or
        // shared content present, prefix containment within the floor still
        // routes (spotif is contained in spotify at 6/7 = 0.857 >= 0.75).
        var commands = new[]
        {
            "open spotify",
            "if you would be a piece of furniture what would you be"
        };

        var match = FuzzyMatcher.FindClosestMatch("spotif", commands, 0.80f);

        Assert.NotNull(match);
        Assert.Equal("open spotify", match!.MatchedCommand);
    }
}
