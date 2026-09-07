using CrossPlatformPatcher.Core.Modules;

namespace CrossPlatformPatcher.TestModuleTypes;

/// <summary>
/// Hermetic stand-in IPatchModule implementations used by ModuleRegistryTests.
/// They live in their own assembly so discovery tests can scan an assembly that
/// contains ONLY healthy modules (no construction failures) without depending on
/// the real production modules (ProcessStartModule / SpeechModule / OpenWakeWordModule).
/// </summary>
public sealed class AlphaModule : IPatchModule
{
    public string Name => "Alpha";
    public int Order => 1;

    public PatchModuleResult Apply(PatchModuleContext context)
        => new() { ModuleName = Name, PatchPointsFound = 1, PatchPointsApplied = 1 };
}

public sealed class BetaModule : IPatchModule
{
    public string Name => "Beta";
    public int Order => 2;

    public PatchModuleResult Apply(PatchModuleContext context)
        => new() { ModuleName = Name, PatchPointsFound = 2, PatchPointsApplied = 2 };
}

public sealed class GammaModule : IPatchModule
{
    public string Name => "Gamma";
    public int Order => 3;

    public PatchModuleResult Apply(PatchModuleContext context)
        => new() { ModuleName = Name, PatchPointsFound = 3, PatchPointsApplied = 3 };
}
