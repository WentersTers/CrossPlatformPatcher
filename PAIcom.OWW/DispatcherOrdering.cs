using System;
using System.Collections.Generic;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Dispatcher execution order for first-success-wins (v0.1.2).
///
/// The speech-emulation channel resolves instantly (it only queues into the
/// host speech engine) but its effects land seconds later on a cold engine —
/// after faster channels already acted. Racing it fairly is impossible in a
/// sequential pipeline, so it runs last: fast, synchronous channels claim
/// the command first, and emulation only fires when nothing else could.
/// Same doctrine as the input chain (SAPI recognition is the last resort):
/// SAPI is last resort for execution too.
/// </summary>
public static class DispatcherOrdering
{
    public const string EmulationName = "speech-emulation";

    /// <summary>Stable partition: everything keeps order, emulation goes last.</summary>
    public static string[] MoveEmulationLast(IReadOnlyList<string> names)
    {
        if (names == null || names.Count == 0)
            return Array.Empty<string>();
        var head = new List<string>(names.Count);
        var tail = new List<string>();
        foreach (var name in names)
        {
            if (string.Equals(name, EmulationName, StringComparison.Ordinal))
                tail.Add(name);
            else
                head.Add(name);
        }
        head.AddRange(tail);
        return head.ToArray();
    }
}
