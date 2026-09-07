using System.Reflection;
using dnlib.DotNet;

namespace CrossPlatformPatcher.Core.Modules;

/// <summary>
/// Core engine + registrar. Discovers every <see cref="IPatchModule"/> in an
/// assembly by reflection, orders them, and executes them with per-module error
/// isolation.
///
/// Open/Closed: this file never changes when a feature is added or removed.
/// Only the module implementation set changes.
/// </summary>
public static class ModuleRegistry
{
    /// <summary>
    /// A module that could not be constructed, or was skipped, during discovery.
    ///
    /// Mirrors the per-module error isolation that <see cref="RunAll(IEnumerable{IPatchModule}, PatchModuleContext)"/>
    /// already provides at runtime, so a broken module never aborts the whole run.
    /// </summary>
    public readonly record struct DiscoveryFailure(string ModuleType, string Message);

    /// <summary>
    /// Optional sink for non-fatal discovery diagnostics (e.g. constructor-injection
    /// hints and construction failures). Defaults to <c>null</c> (no-op). Wire this
    /// to any logger of the host application; the modules' runtime <see cref="PatchModuleContext.Log"/>
    /// is not available during discovery because no context exists yet.
    /// </summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Dynamically discover all <see cref="IPatchModule"/> implementations.
    /// Modules are instantiated via their parameterless constructor and ordered
    /// by <see cref="IPatchModule.Order"/>.
    ///
    /// Error isolation: a constructor that throws, or a module that lacks a public
    /// parameterless constructor, is recorded as a <see cref="DiscoveryFailure"/> and
    /// skipped — the remaining modules are still discovered. This is the same
    /// isolation model <see cref="RunAll(IEnumerable{IPatchModule}, PatchModuleContext)"/>
    /// applies to the run step.
    /// </summary>
    /// <param name="assembly">Assembly to scan (defaults to the engine's own assembly).</param>
    public static IReadOnlyList<IPatchModule> Discover(Assembly? assembly = null)
        => Discover(assembly, out _);

    /// <summary>
    /// Discover modules and also report any construction/skip diagnostics that
    /// occurred. Keeps the original <see cref="Discover(Assembly?)"/> signature
    /// backwards-compatible while exposing the failure list.
    /// </summary>
    /// <param name="assembly">Assembly to scan (defaults to the engine's own assembly).</param>
    /// <param name="failures">
    /// Non-empty when at least one module could not be constructed or was skipped.
    /// Each entry carries the affected <c>ModuleType</c> and a human-readable message.
    /// </param>
    public static IReadOnlyList<IPatchModule> Discover(Assembly? assembly, out IReadOnlyList<DiscoveryFailure> failures)
    {
        var asm = assembly ?? typeof(ModuleRegistry).Assembly;
        var moduleType = typeof(IPatchModule);

        var discovered = new List<IPatchModule>();
        var failureList = new List<DiscoveryFailure>();
        failures = failureList;

        foreach (var type in asm.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface)
                continue;
            if (!moduleType.IsAssignableFrom(type))
                continue;

            var ctor = type.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length == 0);
            if (ctor is null)
            {
                // Constructor-injection hint for DI users: don't silently skip.
                var message = $"[{type.Name}] {type.FullName} implements IPatchModule but has no public " +
                              "parameterless constructor; skipping. Constructor-injected modules are not " +
                              "yet registered automatically — register them manually.";
                failureList.Add(new DiscoveryFailure(type.Name, message));
                Log?.Invoke(message);
                continue;
            }

            try
            {
                discovered.Add((IPatchModule)Activator.CreateInstance(type)!);
            }
            catch (Exception ex)
            {
                // Same try/catch shape and message style as RunAll's per-module isolation.
                // Activator.CreateInstance wraps the real ctor exception in a
                // TargetInvocationException; unwrap it so the diagnostic names the
                // actual cause instead of a generic "target of an invocation".
                var cause = ex is TargetInvocationException { InnerException: { } tie }
                    ? tie
                    : ex;
                var message = $"[{type.Name}] {cause.GetType().Name}: {cause.Message}";
                failureList.Add(new DiscoveryFailure(type.Name, message));
                Log?.Invoke(message);
            }
        }

        return discovered
            .OrderBy(m => m.Order)
            .ToList();
    }

    /// <summary>
    /// Run every discovered module against <paramref name="context"/>.
    ///
    /// Error isolation: each module runs in its own try/catch. A throwing module
    /// is recorded as a failure and the remaining modules still execute.
    /// </summary>
    public static PatchRunResult RunAll(IEnumerable<IPatchModule> modules, PatchModuleContext context)
    {
        var run = new PatchRunResult();

        foreach (var module in modules)
        {
            run.ModulesExecuted++;
            try
            {
                var result = module.Apply(context);
                run.Modules.Add(result);
                run.TotalPatchPointsFound += result.PatchPointsFound;
                run.TotalPatchPointsApplied += result.PatchPointsApplied;

                if (!result.Success)
                {
                    run.ModulesFailed++;
                    run.Errors.AddRange(result.Errors);
                }
            }
            catch (Exception ex)
            {
                run.ModulesFailed++;
                var message = $"[{module.Name}] {ex.GetType().Name}: {ex.Message}";
                run.Errors.Add(message);
                run.Modules.Add(new PatchModuleResult { ModuleName = module.Name, Errors = { message } });
            }
        }

        return run;
    }

    /// <summary>Discover and run in one call (production entry point).</summary>
    public static PatchRunResult RunAll(PatchModuleContext context)
        => RunAll(Discover(), context);
}
