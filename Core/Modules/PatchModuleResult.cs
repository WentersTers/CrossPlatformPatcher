namespace CrossPlatformPatcher.Core.Modules;

/// <summary>Outcome of a single module run.</summary>
public sealed class PatchModuleResult
{
    /// <summary>Name of the module that produced this result.</summary>
    public string ModuleName { get; init; } = string.Empty;

    /// <summary>Patch points the module located.</summary>
    public int PatchPointsFound { get; init; }

    /// <summary>Patch points actually applied.</summary>
    public int PatchPointsApplied { get; init; }

    /// <summary>Non-fatal errors reported by the module (does not abort the run).</summary>
    public List<string> Errors { get; } = [];

    /// <summary>True when the module reported no errors.</summary>
    public bool Success => Errors.Count == 0;
}
