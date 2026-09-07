using CrossPlatformPatcher.Core.Modules;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Verifies the shared scaffolding provided by <see cref="PatchModuleBase"/>:
/// the null-module guard (which must NOT call into <see cref="PatchModuleBase.ApplyCore"/>)
/// and the canonical <see cref="PatchModuleBase.Describe"/> helper.
/// </summary>
public sealed class PatchModuleBaseTests
{
    private sealed class TestModule : PatchModuleBase
    {
        public override string Name => "TestModule";
        public override int Order => 42;

        public bool ApplyCoreCalled { get; private set; }

        protected override PatchModuleResult ApplyCore(PatchModuleContext context)
        {
            ApplyCoreCalled = true;
            return new PatchModuleResult { ModuleName = Name, PatchPointsFound = 3, PatchPointsApplied = 3 };
        }
    }

    [Fact]
    public void Apply_WithNullModule_Returns_Errors_And_DoesNot_Call_ApplyCore()
    {
        var module = new TestModule();

        var result = module.Apply(new PatchModuleContext { Module = null, Log = _ => { } });

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Equal("TestModule", result.ModuleName);
        Assert.False(module.ApplyCoreCalled);
    }

    [Fact]
    public void Describe_Returns_Name_And_Order()
    {
        var module = new TestModule();

        Assert.Equal("TestModule (order=42)", module.Describe());
    }
}
