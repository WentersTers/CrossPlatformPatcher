using Microsoft.CodeAnalysis;
using System.Reflection;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Resolves .NET Framework 4.8 reference assemblies needed to Roslyn-compile
/// HotSwapTemplate, working on any OS.
///
/// Strategy (in priority order):
///   1. Extract the dlls that were embedded into this patcher binary from the
///      Microsoft.NETFramework.ReferenceAssemblies.net48 NuGet package.
///   2. Walk the well-known Windows installation paths (so developers on Windows
///      with .NET Framework installed never need the embedded copies).
///   3. Last-ditch: steal mscorlib from wherever the .NET runtime loaded it.
/// </summary>
public static class ReferenceAssemblyResolver
{
    // Logical resource names embedded in the patcher exe (see .csproj EmbeddedResource items)
    private static readonly (string logical, string filename)[] EmbeddedRefs =
    [
        ("netfx48.mscorlib.dll",             "mscorlib.dll"),
        ("netfx48.System.dll",               "System.dll"),
        ("netfx48.System.Core.dll",          "System.Core.dll"),
        ("netfx48.System.Windows.Forms.dll", "System.Windows.Forms.dll"),
        ("netfx48.System.Drawing.dll",       "System.Drawing.dll"),
        ("netfx48.System.Net.Http.dll",      "System.Net.Http.dll"),
    ];

    private static string? _resolvedDir;
    private static readonly object _lock = new();

    /// <summary>
    /// Returns an array of <see cref="MetadataReference"/> objects for all
    /// required .NET Framework 4.8 reference assemblies.
    /// Throws <see cref="InvalidOperationException"/> only if no source can
    /// produce at least mscorlib.
    /// </summary>
    public static MetadataReference[] GetReferences()
    {
        var dir = GetOrExtractDir();
        var refs = new List<MetadataReference>();

        foreach (var (_, filename) in EmbeddedRefs)
        {
            var path = Path.Combine(dir, filename);
            if (File.Exists(path))
                refs.Add(MetadataReference.CreateFromFile(path));
        }

        if (refs.Count == 0)
            throw new InvalidOperationException(
                "Could not locate any .NET Framework 4.8 reference assemblies. " +
                "The patcher may have been built without the embedded refs.");

        Console.WriteLine($"    [refs] Using .NET FW 4.8 references from: {dir}  ({refs.Count} dlls)");
        return [.. refs];
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private static string GetOrExtractDir()
    {
        lock (_lock)
        {
            if (_resolvedDir is not null)
                return _resolvedDir;

            // 1. Try well-known on-disk Windows paths first (no extraction needed)
            foreach (var candidate in WindowsCandidatePaths())
            {
                if (HasAllRefs(candidate))
                {
                    _resolvedDir = candidate;
                    return _resolvedDir;
                }
            }

            // 2. Extract embedded refs to a temp directory
            var extractDir = ExtractEmbeddedRefs();
            if (extractDir is not null && HasAllRefs(extractDir))
            {
                _resolvedDir = extractDir;
                return _resolvedDir;
            }

            // 3. Last-ditch: try the partial set that may exist in extractDir
            if (extractDir is not null)
            {
                _resolvedDir = extractDir;
                return _resolvedDir;
            }

            // 4. Absolute fallback: the running .NET runtime directory
            //    (only reliable on Windows with .NET Framework installed)
#pragma warning disable IL3000
            var fallback = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "";
#pragma warning restore IL3000
            _resolvedDir = fallback;
            return _resolvedDir;
        }
    }

    private static IEnumerable<string> WindowsCandidatePaths()
    {
        // Standard Windows paths for .NET Framework reference assemblies
        yield return @"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8";
        yield return @"C:\Program Files\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8";
        yield return @"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2";
        // Wine maps the Windows system root; on Linux/Mac this will just not exist
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8");
    }

    private static bool HasAllRefs(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        // Require at minimum mscorlib to be present
        return File.Exists(Path.Combine(dir, "mscorlib.dll"));
    }

    /// <summary>
    /// Extracts all embedded .NET FW 4.8 ref dlls to a stable temp folder
    /// (%LOCALAPPDATA%/PAIcom/netfx48 on Windows, ~/.paicom/netfx48 elsewhere).
    /// Returns the folder path, or null if none of the embedded resources exist
    /// (patcher was built without them).
    /// </summary>
    private static string? ExtractEmbeddedRefs()
    {
        var extractDir = Path.Combine(GetPaicomDataDir(), "netfx48");
        Directory.CreateDirectory(extractDir);

        var exe = Assembly.GetExecutingAssembly();
        bool anyFound = false;

        foreach (var (logical, filename) in EmbeddedRefs)
        {
            var dest = Path.Combine(extractDir, filename);

            // Skip if already extracted (idempotent)
            if (File.Exists(dest)) { anyFound = true; continue; }

            using var stream = exe.GetManifestResourceStream(logical);
            if (stream is null) continue;

            anyFound = true;
            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write,
                                          FileShare.None, 65536, false);
            stream.CopyTo(fs);
        }

        return anyFound ? extractDir : null;
    }

    /// <summary>
    /// Returns the PAIcom data directory, respecting OS conventions.
    /// Windows/Wine : %LOCALAPPDATA%\PAIcom
    /// Linux/Mac    : ~/.paicom
    /// </summary>
    public static string GetPaicomDataDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // On Linux/Mac SpecialFolder.LocalApplicationData resolves to ~/.local/share
        // which is fine; we just use a sub-folder named PAIcom.
        if (string.IsNullOrEmpty(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        return Path.Combine(local, "PAIcom");
    }
}

