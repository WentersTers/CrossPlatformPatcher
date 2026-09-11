using dnlib.DotNet;

namespace CrossPlatformPatcher.Core.Modules;

/// <summary>
/// Dependency-injection context handed to every module at execution time.
///
/// Modules must use these injected services instead of touching global state
/// (e.g. <c>Console</c> or process-wide settings) directly. This keeps modules
/// decoupled and unit-testable.
/// </summary>
public sealed class PatchModuleContext
{
    /// <summary>The dnlib module being patched. Nullable so the registry is easy to
    /// test without materialising a real PE; production always supplies a module.</summary>
    public ModuleDefMD? Module { get; init; }

    /// <summary>Injected logger. Prefer this over <c>Console.WriteLine</c>.</summary>
    public Action<string> Log { get; init; } = _ => { };

    /// <summary>Injected OpenWakeWord configuration.</summary>
    public OpenWakeWordSettings? OwwSettings { get; init; }

    /// <summary>Injected launcher migration mode.</summary>
    public MigrationMode MigrationMode { get; init; } = MigrationMode.Stable;
}
