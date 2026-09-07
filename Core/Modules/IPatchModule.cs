namespace CrossPlatformPatcher.Core.Modules;

/// <summary>
/// Contract every patch feature must implement.
///
/// Open/Closed: the core engine only knows about <see cref="IPatchModule"/>.
/// Adding a new feature = adding a new class that implements this interface.
/// The engine never needs to be edited to support a new feature.
/// </summary>
public interface IPatchModule
{
    /// <summary>Human-readable module name, used in logs and result reporting.</summary>
    string Name { get; }

    /// <summary>
    /// Execution order (ascending). Lower values run first.
    /// Keeps deterministic ordering even when modules are discovered dynamically.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Apply this patch feature to the target module.
    /// Implementations MUST NOT catch broadly and swallow errors — the registry
    /// isolates exceptions per-module so one failure cannot bring down the run.
    /// </summary>
    PatchModuleResult Apply(PatchModuleContext context);
}
