using System.Security.Cryptography;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Patch-time half of the tripwire (<see cref="TripwireCheck"/> is the
/// injected runtime half). Writes <c>&lt;output&gt;.tripwire-baseline</c>
/// holding the SHA-256 of the freshly patched bytes:
/// line 1 <c>sha256:&lt;HEX&gt;</c>, line 2 <c>utc:&lt;ISO&gt;</c>.
/// </summary>
public static class TripwireBaseline
{
    public static string BaselinePathFor(string outputPath) => outputPath + TripwireFileSuffix;

    /// <summary>Keep in sync with <c>TripwireCheck.BaselineSuffix</c> (the
    /// injected assembly cannot reference this project).</summary>
    public const string TripwireFileSuffix = ".tripwire-baseline";

    /// <summary>Hash <paramref name="outputPath"/> and persist the baseline
    /// file beside it. Returns the hex hash. Throws on IO failure.</summary>
    public static string WriteBaseline(string outputPath, Action<string>? log = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputPath)));
        File.WriteAllText(BaselinePathFor(outputPath),
            $"sha256:{hash}\nutc:{DateTime.UtcNow:O}\n");
        log?.Invoke($"[INFO] Tripwire baseline: {hash} ({BaselinePathFor(outputPath)})");
        return hash;
    }
}
