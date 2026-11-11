using dnlib.DotNet;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Post-patch checks for a registry entry: the output must carry the resources
/// the strategy promised, the resource pairing must be intact, and - when the
/// entry pins one - the output hash must match.
/// </summary>
public static class PatchVerifier
{
    /// <summary>
    /// Verify an already-loaded output module. Returns human-readable failures;
    /// empty means the output is valid for this entry.
    /// </summary>
    public static IReadOnlyList<string> Verify(ModuleDef output, VersionEntry entry, string? outputSha256 = null)
    {
        var failures = new List<string>();

        // CLI header flag 0x1 = IL-only managed assembly.
        if (((uint)output.Cor20HeaderFlags & 1u) == 0)
            failures.Add("output is not an IL-only managed assembly");

        foreach (var required in entry.RequiredOutputResources)
        {
            if (!output.Resources.Any(r => r.Name == required))
                failures.Add($"missing required resource: {required}");
        }

        // Pairing rule: the materialization fix adds exactly one entry per
        // name. Duplicates mean a double-patch or a drifted input.
        foreach (var required in entry.RequiredOutputResources)
        {
            var count = output.Resources.Count(r => r.Name == required);
            if (count > 1)
                failures.Add($"resource '{required}' appears {count} times (pairing drift)");
        }

        if (entry.ExpectedOutputSha256 is { Length: > 0 } expected &&
            outputSha256 is { Length: > 0 } actual &&
            !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"output hash {Short(actual)} does not match certified hash {Short(expected)}");
        }

        return failures;
    }

    private static string Short(string hash) => hash.Length >= 12 ? hash[..12] : hash;
}
