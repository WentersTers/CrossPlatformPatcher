namespace CrossPlatformPatcher.Core.Modules;

/// <summary>Aggregated outcome across every discovered module.</summary>
public sealed class PatchRunResult
{
    /// <summary>Per-module results, in execution order.</summary>
    public List<PatchModuleResult> Modules { get; } = [];

    /// <summary>Aggregated errors (fatal module crashes + module-reported errors).</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Number of modules attempted.</summary>
    public int ModulesExecuted { get; set; }

    /// <summary>Number of modules that failed (threw or reported errors).</summary>
    public int ModulesFailed { get; set; }

    /// <summary>Sum of all modules' patch points found.</summary>
    public int TotalPatchPointsFound { get; set; }

    /// <summary>Sum of all modules' patch points applied.</summary>
    public int TotalPatchPointsApplied { get; set; }

    /// <summary>True when no module reported or threw an error.</summary>
    public bool Success => Errors.Count == 0;
}
