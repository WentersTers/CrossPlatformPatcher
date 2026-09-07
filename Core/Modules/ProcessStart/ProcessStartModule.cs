namespace CrossPlatformPatcher.Core.Modules.ProcessStart;

/// <summary>
/// REFACTOR EXAMPLE MODULE — replicate this shape for every new feature.
///
/// Single responsibility: this class only knows how to orchestrate the
/// Process.Start(string) compatibility patch. The actual dnlib work lives in
/// <see cref="ProcessStartCompatibilityPatcher"/>; this module adapts it to the
/// plugin contract and reports a structured result.
/// </summary>
public sealed class ProcessStartModule : PatchModuleBase
{
    public override string Name => "ProcessStartCompatibility";

    // Runs first — other features may rely on a stable Process.Start baseline.
    public override int Order => 10;

    protected override PatchModuleResult ApplyCore(PatchModuleContext context)
    {
        context.Log($"[{Name}] rewriting fragile Process.Start(string) calls.");

        // context.Module is guaranteed non-null here (PatchModuleBase guards it).
        var patches = ProcessStartCompatibilityPatcher.Patch(context.Module!, context.Log);

        return new PatchModuleResult
        {
            ModuleName = Name,
            PatchPointsFound = patches,
            PatchPointsApplied = patches,
        };
    }

    /// <summary>
    /// Pure, unit-testable helper — demonstrates Single Responsibility.
    /// Extracted so the progress line can be tested without running a patch.
    /// Note: this is intentionally distinct from <see cref="PatchModuleBase.Describe"/>,
    /// which reports the module identity rather than a progress summary.
    /// </summary>
    public static string Describe(int patches)
        => patches == 0
            ? "ProcessStartCompatibility: no patch points found."
            : $"ProcessStartCompatibility: applied {patches} patch point(s).";
}
