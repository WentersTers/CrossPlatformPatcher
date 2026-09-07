using CrossPlatformPatcher.Core.Modules;
using CrossPlatformPatcher.Core.Modules.ProcessStart;
using CrossPlatformPatcher.Core.Modules.Speech;
using CrossPlatformPatcher.Core.Modules.OpenWakeWord;
using CrossPlatformPatcher.TestModuleTypes;
using Xunit;

namespace CrossPlatformPatcher.Tests;

public sealed class ModuleRegistryTests
{
    private static PatchModuleContext EmptyContext() => new() { Log = _ => { } };

    // ── Discovery (Open/Closed proof) ────────────────────────────────────

    [Fact]
    public void Discovery_Finds_All_Known_Modules()
    {
        var modules = ModuleRegistry.Discover().Select(m => m.Name).ToList();

        Assert.Contains("ProcessStartCompatibility", modules);
        Assert.Contains("SpeechCompatibility", modules);
        Assert.Contains("OpenWakeWordCompatibility", modules);
    }

    [Fact]
    public void Discovery_Orders_Modules_By_Order()
    {
        var modules = ModuleRegistry.Discover().ToList();

        var processStartIndex = modules.FindIndex(m => m is ProcessStartModule);
        var speechIndex = modules.FindIndex(m => m is SpeechModule);
        var owwIndex = modules.FindIndex(m => m is OpenWakeWordModule);

        // Each must be present and ProcessStart must run before Speech before OWW.
        Assert.True(processStartIndex >= 0);
        Assert.True(speechIndex >= 0);
        Assert.True(owwIndex >= 0);
        Assert.True(processStartIndex < speechIndex);
        Assert.True(speechIndex < owwIndex);
    }

    // ── Error isolation ──────────────────────────────────────────────────

    private sealed class CountingModule : IPatchModule
    {
        public string Name => "Counting";
        public int Order => 5;
        public PatchModuleResult Apply(PatchModuleContext context)
            => new() { ModuleName = Name, PatchPointsFound = 2, PatchPointsApplied = 2 };
    }

    private sealed class ThrowingModule : IPatchModule
    {
        public string Name => "Throwing";
        public int Order => 10;
        public PatchModuleResult Apply(PatchModuleContext context)
            => throw new InvalidOperationException("boom");
    }

    private sealed class ReportingErrorModule : IPatchModule
    {
        public string Name => "ReportingError";
        public int Order => 15;
        public PatchModuleResult Apply(PatchModuleContext context)
            => new() { ModuleName = Name, Errors = { "soft error" } };
    }

    // Test-only modules whose parameterless constructor is broken or absent.
    // These live in the test assembly (not the production assembly) so discovery
    // is hermetic and does not depend on the real module types.

    private sealed class BadCtorModule : IPatchModule
    {
        public BadCtorModule() => throw new InvalidOperationException("ctor boom");

        public string Name => "BadCtor";
        public int Order => 40;
        public PatchModuleResult Apply(PatchModuleContext context) => new();
    }

    private sealed class NoDefaultCtorModule : IPatchModule
    {
        public NoDefaultCtorModule(string dependency) { }

        public string Name => "NoDefaultCtor";
        public int Order => 50;
        public PatchModuleResult Apply(PatchModuleContext context) => new();
    }

    [Fact]
    public void RunAll_Continues_After_A_Module_Throws()
    {
        IPatchModule[] modules = [new CountingModule(), new ThrowingModule(), new ReportingErrorModule()];

        var run = ModuleRegistry.RunAll(modules, EmptyContext());

        // The throwing module is isolated; the others still ran.
        Assert.Equal(3, run.ModulesExecuted);
        Assert.Equal(2, run.ModulesFailed);
        Assert.Equal(2, run.Errors.Count);
        Assert.Contains(run.Errors, e => e.Contains("Throwing"));
        Assert.Contains(run.Errors, e => e.Contains("soft error"));

        // The successful module still contributed its patch points.
        Assert.Equal(2, run.TotalPatchPointsFound);
        Assert.Equal(2, run.TotalPatchPointsApplied);
    }

    [Fact]
    public void RunAll_Aggregates_Patch_Points_Across_Modules()
    {
        IPatchModule[] modules = [new CountingModule(), new CountingModule()];

        var run = ModuleRegistry.RunAll(modules, EmptyContext());

        Assert.True(run.Success);
        Assert.Equal(4, run.TotalPatchPointsFound);
        Assert.Equal(4, run.TotalPatchPointsApplied);
        Assert.Equal(0, run.Errors.Count);
    }

    [Fact]
    public void RunAll_Records_Thrown_Exception_Message()
    {
        IPatchModule[] modules = [new ThrowingModule()];

        var run = ModuleRegistry.RunAll(modules, EmptyContext());

        Assert.False(run.Success);
        Assert.Single(run.Errors);
        Assert.Contains("boom", run.Errors[0]);
        Assert.Equal(1, run.ModulesFailed);
    }

    // ── Discovery error isolation (construction) ─────────────────────────

    [Fact]
    public void Discover_DoesNotThrow_When_A_Module_Constructor_Throws()
    {
        var modules = ModuleRegistry.Discover(typeof(BadCtorModule).Assembly, out var failures);

        // Discover() must not abort: the bad constructor is isolated.
        var failure = Assert.Single(failures.Where(f => f.ModuleType == nameof(BadCtorModule)));
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
        Assert.Contains("ctor boom", failure.Message);

        // The broken module is skipped but discovery completed.
        Assert.DoesNotContain(modules, m => m is BadCtorModule);
    }

    [Fact]
    public void Discover_DoesNotThrow_And_Reports_Module_Without_Parameterless_Ctor()
    {
        var logs = new List<string>();
        var originalLog = ModuleRegistry.Log;
        ModuleRegistry.Log = logs.Add;
        try
        {
            ModuleRegistry.Discover(typeof(NoDefaultCtorModule).Assembly, out var failures);

            var failure = Assert.Single(failures.Where(f => f.ModuleType == nameof(NoDefaultCtorModule)));
            Assert.False(string.IsNullOrWhiteSpace(failure.Message));

            // DI hint is surfaced through the registry logger too.
            Assert.Contains(logs, l => l.Contains(nameof(NoDefaultCtorModule)));
        }
        finally
        {
            ModuleRegistry.Log = originalLog;
        }
    }

    [Fact]
    public void Discover_Finds_StandIn_Modules_Without_Failures()
    {
        var modules = ModuleRegistry.Discover(typeof(AlphaModule).Assembly, out var failures);

        Assert.Empty(failures);
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, modules.Select(m => m.Name));
    }

    // ── Example module pure helper (Single Responsibility) ───────────────

    [Fact]
    public void ProcessStartModule_Describe_Is_Deterministic()
    {
        Assert.Equal("ProcessStartCompatibility: no patch points found.", ProcessStartModule.Describe(0));
        Assert.Equal("ProcessStartCompatibility: applied 3 patch point(s).", ProcessStartModule.Describe(3));
    }
}
