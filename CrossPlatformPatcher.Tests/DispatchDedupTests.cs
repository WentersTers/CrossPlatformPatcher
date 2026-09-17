using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>Dispatch-dedup contract: same token twice inside the window
/// suppresses the echo; anything else flows.</summary>
[Collection(SequentialTestCollection.CollectionName)]
public sealed class DispatchDedupTests
{
    private const string WindowVar = "PAICOM_DISPATCH_DEDUP_MS";

    public DispatchDedupTests()
    {
        DispatchDedup.Reset();
        Environment.SetEnvironmentVariable(WindowVar, null);
    }

    [Fact]
    public void Immediate_Repeat_Suppressed_Different_Token_Flows()
    {
        DispatchDedup.Record("open browser");
        Assert.True(DispatchDedup.IsDuplicate("open browser"));
        Assert.True(DispatchDedup.IsDuplicate("Open Browser"));
        Assert.False(DispatchDedup.IsDuplicate("open twitch"));
        Assert.False(DispatchDedup.IsDuplicate(null));
        Assert.False(DispatchDedup.IsDuplicate(""));
    }

    [Fact]
    public void Suppression_Does_Not_Extend_Suppression()
    {
        // Only Record arms the window; IsDuplicate is read-only, so a
        // deliberate repeat after the window still executes.
        DispatchDedup.Record("volume up");
        Assert.True(DispatchDedup.IsDuplicate("volume up"));
        Assert.True(DispatchDedup.IsDuplicate("volume up"));
    }

    [Fact]
    public void Zero_Window_Disables()
    {
        var prior = Environment.GetEnvironmentVariable(WindowVar);
        try
        {
            Environment.SetEnvironmentVariable(WindowVar, "0");
            DispatchDedup.Record("open browser");
            Assert.False(DispatchDedup.IsDuplicate("open browser"));
            Assert.Equal(0, DispatchDedup.GetWindowMs());
        }
        finally
        {
            Environment.SetEnvironmentVariable(WindowVar, prior);
        }
    }

    [Fact]
    public void Default_Window_Is_Positive()
    {
        var prior = Environment.GetEnvironmentVariable(WindowVar);
        try
        {
            Environment.SetEnvironmentVariable(WindowVar, null);
            Assert.Equal(DispatchDedup.DefaultWindowMs, DispatchDedup.GetWindowMs());
            Assert.True(DispatchDedup.DefaultWindowMs > 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WindowVar, prior);
        }
    }

    [Fact]
    public void Record_Ignores_Blank_Token()
    {
        DispatchDedup.Record(null);
        DispatchDedup.Record("   ");
        Assert.False(DispatchDedup.IsDuplicate("open browser"));
    }
}
