namespace CrossPlatformPatcher.Core.Modules;

/// <summary>
/// Shared scaffolding for every patch module.
///
/// Previously each module duplicated the same boilerplate: a null-module guard,
/// a progress log line, and a <c>Describe</c> helper. This base class centralises
/// that scaffolding (Section 2.2 of the modularisation review):
///   • enforces <see cref="Name"/> / <see cref="Order"/> for discovery/ordering,
///   • guards against a null <see cref="PatchModuleContext.Module"/> without throwing,
///   • logs a brief entry/exit line around <see cref="ApplyCore"/>,
///   • provides a canonical <see cref="Describe"/> implementation.
/// Subclasses only implement the actual patch work in <see cref="ApplyCore"/> and
/// keep constructing their own structured <see cref="PatchModuleResult"/>.
/// </summary>
public abstract class PatchModuleBase : IPatchModule
{
    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract int Order { get; }

    /// <summary>
    /// Module-specific patch logic. Invoked only when
    /// <see cref="PatchModuleContext.Module"/> is non-null. Subclasses must return
    /// a structured <see cref="PatchModuleResult"/> describing what they did.
    /// </summary>
    protected abstract PatchModuleResult ApplyCore(PatchModuleContext context);

    /// <summary>
    /// Sealed entry point (non-virtual — subclasses cannot override it). Guards
    /// against a null target module (reporting an error instead of throwing), then
    /// delegates to <see cref="ApplyCore"/> inside a brief entry/exit log framing.
    /// </summary>
    public PatchModuleResult Apply(PatchModuleContext context)
    {
        context.Log($"[{Name}] begin.");

        if (context.Module is null)
        {
            context.Log($"[{Name}] no module loaded.");
            return new PatchModuleResult
            {
                ModuleName = Name,
                Errors = { "No target module was provided." },
            };
        }

        var result = ApplyCore(context);
        context.Log($"[{Name}] complete.");
        return result;
    }

    /// <summary>Canonical single-line description for a module.</summary>
    public virtual string Describe() => $"{Name} (order={Order})";
}
