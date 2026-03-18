using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Top-level orchestrator.  Loads the target module, runs each patch step,
/// and (optionally) writes the modified PE to disk.
/// </summary>
public class AssemblyPatcher
{
    private readonly bool _verbose;

    public AssemblyPatcher(bool verbose = false) => _verbose = verbose;

    /// <summary>Patch the assembly at <paramref name="inputPath"/>.</summary>
    public PatchResult Patch(string inputPath, string outputPath, bool dryRun = false)
    {
        var result = new PatchResult();

        // ── 1. Load module ────────────────────────────────────────────────
        Log("Loading module …");
        var ctx  = ModuleDef.CreateModuleContext();
        // Read into memory first so we don't hold an exclusive OS file lock
        byte[] peBytes;
        using (var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            peBytes = new byte[fs.Length];
            fs.ReadExactly(peBytes);
        }
        var module = ModuleDefMD.Load(peBytes, ctx);
        module.Context = ctx;

        // ── 2. Scan all methods BEFORE injecting new types ────────────────
        //   (prevents our own injected code from polluting the search pool)
        var allMethods = module.GetTypes()
                               .SelectMany(t => t.Methods)
                               .Where(m => m.HasBody)
                               .ToList();

        result.MethodsScanned = allMethods.Count;
        Log($"Scanned {allMethods.Count} methods.");

        // ── Compat mode patch: neutralize fragile Process.Start(string) ───
        Log("Compat build: rewriting fragile Process.Start(string) calls.");
        var rewrites = ProcessStartCompatibilityPatcher.Patch(module, Log);
        result.PatchPointsFound = rewrites;
        result.PatchPointsApplied = rewrites;

        Log("Compat build: wrapping System.Speech call paths.");
        var speechWraps = SpeechCompatibilityPatcher.Patch(module, Log);
        result.PatchPointsFound += speechWraps;
        result.PatchPointsApplied += speechWraps;

        // ── 4. Apply patches ──────────────────────────────────────────────
        if (!dryRun && result.Errors.Count == 0)
        {
            Log("Compat build: IL compatibility patch complete.");
        }
        else if (dryRun)
        {
            Log("(dry-run) Skipping write step.");
        }

        // ── 5. Write output ────────────────────────────────────────────────
        if (!dryRun && result.Errors.Count == 0)
        {
            Log($"Writing patched assembly to {outputPath} …");

            // If no IL patching happened, preserve the original bytes exactly.
            // This avoids resource mapping regressions in heavily obfuscated builds.
            if (result.PatchPointsApplied == 0)
            {
                File.WriteAllBytes(outputPath, peBytes);
                Log("Compat build: wrote byte-for-byte copy (no IL/resource rewrite).");
            }
            else
            {
                // Use the managed writer - NativeModuleWriter requires all existing
                // RIDs to be preserved which conflicts with newly added types.
                var writerOptions = new ModuleWriterOptions(module)
                {
                    WritePdb = false,
                };

                module.Write(outputPath, writerOptions);
            }

            // Generate OS launcher scripts alongside the patched exe
            var outputDir = Path.GetDirectoryName(outputPath) ?? "";
            if (string.IsNullOrEmpty(outputDir))
                outputDir = ".";
            LauncherGenerator.WriteAll(outputDir, Path.GetFileName(outputPath));
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void Log(string msg)
    {
        if (_verbose)
            Console.WriteLine($"  [V] {msg}");
        else
            Console.WriteLine($"  {msg}");
    }
}

/// <summary>Results returned after a patch run.</summary>
public class PatchResult
{
    public int MethodsScanned      { get; set; }
    public int PatchPointsFound    { get; set; }
    public int PatchPointsApplied  { get; set; }
    public List<string> Errors     { get; } = [];
}

