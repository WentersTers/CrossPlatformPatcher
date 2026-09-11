namespace CrossPlatformPatcher.Core.Modules.Speech;

/// <summary>
/// Wraps the System.Speech → Vosk compatibility patch as a plugin module.
/// </summary>
public sealed class SpeechModule : PatchModuleBase
{
    public override string Name => "SpeechCompatibility";

    public override int Order => 20;

    protected override PatchModuleResult ApplyCore(PatchModuleContext context)
    {
        context.Log($"[{Name}] wrapping System.Speech call paths.");

        // context.Module is guaranteed non-null here (PatchModuleBase guards it).
        var patches = SpeechCompatibilityPatcher.Patch(context.Module!, context.Log);

        return new PatchModuleResult
        {
            ModuleName = Name,
            PatchPointsFound = patches,
            PatchPointsApplied = patches,
        };
    }
}
