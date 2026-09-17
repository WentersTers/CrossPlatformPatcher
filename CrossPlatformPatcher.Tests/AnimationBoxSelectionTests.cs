using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Ancestor-visibility rule: a box inside an invisible container can never
/// display, so selection must exclude it. Stubs stand in for WinForms
/// controls (reflection only needs the property shapes), keeping this
/// cross-platform.
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public sealed class AnimationBoxSelectionTests
{
    private sealed class StubBox
    {
        public bool Visible { get; set; } = true;
        public object? Parent { get; set; }
    }

    private sealed class NoParentBox
    {
        public bool Visible { get; set; } = true;
    }

    [Fact]
    public void Visible_Chain_Passes()
    {
        var root = new StubBox { Visible = true, Parent = null };
        var mid = new StubBox { Visible = true, Parent = root };
        var leaf = new StubBox { Visible = false, Parent = mid };
        Assert.True(AnimationBoxSelection.AncestorsVisible(leaf));
    }

    [Fact]
    public void Hidden_Parent_Fails()
    {
        var root = new StubBox { Visible = false, Parent = null };
        var leaf = new StubBox { Visible = true, Parent = root };
        Assert.False(AnimationBoxSelection.AncestorsVisible(leaf));
    }

    [Fact]
    public void Hidden_Grandparent_Fails()
    {
        var root = new StubBox { Visible = false, Parent = null };
        var mid = new StubBox { Visible = true, Parent = root };
        var leaf = new StubBox { Visible = true, Parent = mid };
        Assert.False(AnimationBoxSelection.AncestorsVisible(leaf));
    }

    [Fact]
    public void Missing_Parent_Property_Passes()
    {
        Assert.True(AnimationBoxSelection.AncestorsVisible(new NoParentBox()));
    }

    [Fact]
    public void Null_Control_Fails()
    {
        Assert.False(AnimationBoxSelection.AncestorsVisible(null));
    }
}
