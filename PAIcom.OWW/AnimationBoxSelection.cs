using System;
using System.Reflection;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Animation target selection rules (v0.1.2), pure and unit-tested.
///
/// Failure observed on hardware: command animations rendered into a box
/// that never appeared on screen while the pet box sat hidden — the frames
/// "played blind" and the idle timer later restored the right box. Two
/// defenses: a box whose ancestor chain contains an invisible container
/// can never display, so it is excluded from selection; and the post-state
/// of every SHOW is logged, so the next hardware log proves the effect
/// instead of only the intent.
/// </summary>
public static class AnimationBoxSelection
{
    /// <summary>
    /// True unless a provably invisible ancestor exists. Reflection failure
    /// or missing properties yield true (exclude only on evidence, never on
    /// ignorance).
    /// </summary>
    public static bool AncestorsVisible(object? control)
    {
        if (control == null)
            return false;
        var current = control;
        for (int depth = 0; depth < 32 && current != null; depth++)
        {
            PropertyInfo? parentProperty;
            try
            {
                parentProperty = current.GetType().GetProperty(
                    "Parent", BindingFlags.Instance | BindingFlags.Public);
            }
            catch
            {
                return true;
            }
            if (parentProperty == null)
                return true;
            object? parent;
            try
            {
                parent = parentProperty.GetValue(current);
            }
            catch
            {
                return true;
            }
            if (parent == null)
                return true;
            if (IsVisibleFalse(parent))
                return false;
            current = parent;
        }
        return true;
    }

    private static bool IsVisibleFalse(object target)
    {
        try
        {
            var visibleProperty = target.GetType().GetProperty(
                "Visible", BindingFlags.Instance | BindingFlags.Public);
            if (visibleProperty == null || visibleProperty.PropertyType != typeof(bool))
                return false;
            return !(bool)(visibleProperty.GetValue(target) ?? false);
        }
        catch
        {
            return false;
        }
    }
}
