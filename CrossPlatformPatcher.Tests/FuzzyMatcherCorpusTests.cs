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

    // L01 (Ubuntu 24.04 / Wine 11), 2026-11-10. The ASR heard the "flip a
    // coin" take as "if i call please flip a coin"; the fuzzy keyword stage
    // scored 'call' ~ 'calling' at 90 % and accepted the match on exactly 50 %
    // command coverage, dispatching "why are you calling me a researcher".
    private static readonly string[] ResearcherCorpus =
    {
        "why are you calling me a researcher",
        "open twitch",
        "open spotify",
        "open hulu",
        "open the browser",
        "please hide",
        "stop listening",
        "show hiking places near me",
        "play some music",
        "show me a meme",
    };

    [Fact]
    public void GarbledFlipACoin_DoesNotRouteToResearcher()
    {
        var match = FuzzyMatcher.FindClosestMatch("if i call please flip a coin", ResearcherCorpus, Threshold);
        Assert.Null(match);
    }

    [Fact]
    public void BareCall_DoesNotRouteToResearcher()
    {
        Assert.Null(FuzzyMatcher.FindClosestMatch("call", ResearcherCorpus, Threshold));
        Assert.Null(FuzzyMatcher.FindClosestMatch("i call", ResearcherCorpus, Threshold));
    }

    [Fact]
    public void ResearcherCommand_StillMatches()
    {
        var match = FuzzyMatcher.FindClosestMatch(
            "hey paicom why are you calling me a researcher", ResearcherCorpus, Threshold);
        Assert.NotNull(match);
        Assert.Equal("why are you calling me a researcher", match!.MatchedCommand);
    }

    // L01/L09/L14 batteries (2026-11-11/12): the ASR drops "open" before
    // "browser" and splits "youtube" into two words on every stack. Both
    // must still dispatch.
    private static readonly string[] BrowserCorpus =
    {
        "open the browser",
        "show browser history",
        "close browser tabs",
        "open discord",
        "open twitch",
    };

    [Fact]
    public void DroppedOpen_Routes_Browser_Via_Head_Noun()
    {
        // "browser" appears in three commands, so the shared stage is the
        // route and its distinctive-word gate is what used to refuse it.
        var match = FuzzyMatcher.FindClosestMatch("the browser", BrowserCorpus, Threshold);
        Assert.NotNull(match);
        Assert.Equal("open the browser", match!.MatchedCommand);
    }

    [Fact]
    public void SplitYoutube_Routes_To_Youtube()
    {
        string[] commands = { "open youtube", "open twitch", "open spotify" };
        var match = FuzzyMatcher.FindClosestMatch(
            "a bike i'm hoping you tube", commands, Threshold);
        Assert.NotNull(match);
        Assert.Equal("open youtube", match!.MatchedCommand);
    }

    [Fact]
    public void Head_Noun_Rule_Does_Not_Revive_The_Researcher_Route()
    {
        // The head of "why are you calling me a researcher" is "researcher",
        // which the garbled coin take does not contain; the head-noun rule
        // must not route it.
        Assert.Null(FuzzyMatcher.FindClosestMatch("if i call please flip a coin", ResearcherCorpus, Threshold));
    }
}
