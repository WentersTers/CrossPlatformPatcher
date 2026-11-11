using System;
using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Regression cases taken from the recorded voice corpus (Fedora 43, L03,
/// L04, L08, L12, Steam Proton). Each transcript is the ASR output from a
/// real run; the assertions pin the routes that the coverage/near-word gates
/// fixed without losing the correct ones.
/// </summary>
public class FuzzyMatcherCorpusTests
{
    private static readonly string[] OpenCommands =
    {
        "open disney plus",
        "open hbo",
        "open hulu",
        "open spotify",
        "open the browser",
        "open the trading app",
        "open twitch",
        "open twiter",
        "open youtube",
        "open redit",
    };

    private static readonly string[] ShowCommands =
    {
        "show hiking places near me",
        "show pizza places near me",
        "show camping areas near me",
    };

    private const float Threshold = 0.85f;

    [Fact]
    public void PointShop_DoesNotHijackTradingApp()
    {
        var match = FuzzyMatcher.FindClosestMatch("open the point shop", OpenCommands, Threshold);
        Assert.Null(match);
    }

    [Fact]
    public void HighPlaces_RoutesToHiking_NotPizza()
    {
        var match = FuzzyMatcher.FindClosestMatch("go high places near me", ShowCommands, Threshold);
        Assert.NotNull(match);
        Assert.Equal("show hiking places near me", match!.MatchedCommand);
    }

    [Fact]
    public void FlipACoin_DoesNotRouteToShowOrHide()
    {
        var match = FuzzyMatcher.FindClosestMatch(
            "please flip a coin",
            new[] { "please show up", "please hide" },
            Threshold);
        Assert.Null(match);
    }

    [Fact]
    public void OpenHello_RoutesToHulu()
    {
        var match = FuzzyMatcher.FindClosestMatch("open hello", OpenCommands, Threshold);
        Assert.NotNull(match);
        Assert.Equal("open hulu", match!.MatchedCommand);
    }

    [Fact]
    public void NoisyOpenHello_StillRoutesToHulu()
    {
        // L12 transcript with wake debris.
        var match = FuzzyMatcher.FindClosestMatch("a i m open hello", OpenCommands, Threshold);
        Assert.NotNull(match);
        Assert.Equal("open hulu", match!.MatchedCommand);
    }

    [Fact]
    public void NoisyOpenSpotify_StillRoutesToSpotify()
    {
        // L12 transcript with wake debris.
        var match = FuzzyMatcher.FindClosestMatch("a pie come open spot a fight", OpenCommands, Threshold);
        Assert.NotNull(match);
        Assert.Equal("open spotify", match!.MatchedCommand);
    }

    [Fact]
    public void PowCamp_DoesNotHijackReddit()
    {
        var match = FuzzyMatcher.FindClosestMatch("hey pow camp open the point", OpenCommands, Threshold);
        Assert.Null(match);
    }

    [Fact]
    public void CleanCommand_StillMatchesExactly()
    {
        var match = FuzzyMatcher.FindClosestMatch("open disney plus", OpenCommands, Threshold);
        Assert.NotNull(match);
        Assert.Equal("open disney plus", match!.MatchedCommand);
    }

    [Fact]
    public void MumbledHiking_StillRoutesToHiking()
    {
        var match = FuzzyMatcher.FindClosestMatch("joe hiding places near me", ShowCommands, Threshold);
        Assert.NotNull(match);
        Assert.Equal("show hiking places near me", match!.MatchedCommand);
    }
}
