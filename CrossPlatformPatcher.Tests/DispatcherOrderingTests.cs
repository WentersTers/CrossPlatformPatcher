using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>Emulation runs last so fast channels claim the command first.</summary>
[Collection(SequentialTestCollection.CollectionName)]
public sealed class DispatcherOrderingTests
{
    [Fact]
    public void Emulation_First_Moves_Last_Others_Keep_Order()
    {
        var ordered = DispatcherOrdering.MoveEmulationLast(new[]
        {
            "speech-emulation", "ui-simulation", "game-reflection", "process-fallback",
        });
        Assert.Equal(new[]
        {
            "ui-simulation", "game-reflection", "process-fallback", "speech-emulation",
        }, ordered);
    }

    [Fact]
    public void Already_Last_Stays_Stable()
    {
        var ordered = DispatcherOrdering.MoveEmulationLast(new[]
        {
            "ui-simulation", "speech-emulation",
        });
        Assert.Equal(new[] { "ui-simulation", "speech-emulation" }, ordered);
    }

    [Fact]
    public void No_Emulation_Unchanged_And_Empty_Safe()
    {
        Assert.Equal(new[] { "a", "b" },
            DispatcherOrdering.MoveEmulationLast(new[] { "a", "b" }));
        Assert.Empty(DispatcherOrdering.MoveEmulationLast(Array.Empty<string>()));
    }

    [Fact]
    public void Emulation_Name_Constant_Matches_Pipeline()
    {
        Assert.Equal("speech-emulation", DispatcherOrdering.EmulationName);
    }
}
