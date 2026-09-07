namespace CrossPlatformPatcher.Core.Modules.OpenWakeWord;

/// <summary>
/// Wraps the OpenWakeWord audio event-handler injection as a plugin module.
/// Threads the injected <see cref="PatchModuleContext.OwwSettings"/> through to
/// <see cref="OpenWakeWordCompatibilityPatcher.Patch"/> so the lock duration baked
/// into the emitted IL reflects the configured value (falling back to the legacy
/// 3000 ms default when no settings are injected).
///
/// <see cref="PatchModuleContext.MigrationMode"/> is deliberately NOT consumed here:
/// it governs launcher migration / 64-bit runtime selection, which is unrelated to
/// the OWW lock/ticks constants emitted by the patcher, so threading it through
/// would add a dead parameter.
/// </summary>
public sealed class OpenWakeWordModule : PatchModuleBase
{
    public override string Name => "OpenWakeWordCompatibility";

    // Must run after Speech so speech call paths are stable before injection.
    public override int Order => 30;

    protected override PatchModuleResult ApplyCore(PatchModuleContext context)
    {
        context.Log($"[{Name}] injecting OpenWakeWord audio event handlers.");

        // context.Module is guaranteed non-null here (PatchModuleBase guards it).
        var patches = OpenWakeWordCompatibilityPatcher.Patch(
            context.Module!,
            context.Log,
            context.OwwSettings);

        return new PatchModuleResult
        {
            ModuleName = Name,
            PatchPointsFound = patches,
            PatchPointsApplied = patches,
        };
    }
}
