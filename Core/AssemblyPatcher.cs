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
    private readonly OpenWakeWordSettings _owwSettings;

    public AssemblyPatcher(bool verbose = false, OpenWakeWordSettings? owwSettings = null)
    {
        _verbose = verbose;
        _owwSettings = owwSettings ?? OpenWakeWordSettings.CreateDefault();
    }

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

        // ── Embed resources ────────────────────────────────────────────────
        Log("Embedding Vosk, ONNX, and OpenWakeWord resources …");
        VoskResourceEmbedder.EmbedIntoModule(module, _verbose);

        // ── Compat mode patch: neutralize fragile Process.Start(string) ───
        Log("Compat build: rewriting fragile Process.Start(string) calls.");
        var rewrites = ProcessStartCompatibilityPatcher.Patch(module, Log);
        result.PatchPointsFound = rewrites;
        result.PatchPointsApplied = rewrites;

        Log("Compat build: wrapping System.Speech call paths.");
        var speechWraps = SpeechCompatibilityPatcher.Patch(module, Log);
        result.PatchPointsFound += speechWraps;
        result.PatchPointsApplied += speechWraps;

        Log("Compat build: injecting OpenWakeWord audio event handlers.");
        var owwWraps = OpenWakeWordCompatibilityPatcher.Patch(module, Log);
        result.PatchPointsFound += owwWraps;
        result.PatchPointsApplied += owwWraps;

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

            var outputDir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputDir)) outputDir = ".";

            Log("Extracting PAIcom.OWW.dll and dependencies …");
            string[] embeddedDlls = {
                "PAIcom.OWW.dll",
                "System.Memory.dll",
                "System.Buffers.dll",
                "System.Numerics.Vectors.dll",
                "System.Runtime.CompilerServices.Unsafe.dll",
                "onnxruntime.managed.dll"
            };

            foreach (var dll in embeddedDlls)
            {
                using var s = typeof(AssemblyPatcher).Assembly.GetManifestResourceStream(dll);
                if (s != null)
                {
                    var outName = dll == "onnxruntime.managed.dll" ? "Microsoft.ML.OnnxRuntime.dll" : dll;
                    var destPath = Path.Combine(outputDir, outName);
                    using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, false);
                    s.CopyTo(fs);
                }
            }

            // If no IL patching happened, preserve the original bytes exactly.
            // This avoids resource mapping regressions in heavily obfuscated builds.
            if (result.PatchPointsApplied == 0)
            {
                File.WriteAllBytes(outputPath, peBytes);
                Log("Compat build: wrote byte-for-byte copy (no IL/resource rewrite).");
            }
            else
            {
                // Inject the OpenWakeWord helper assembly reference
                var owwRef = new AssemblyRefUser("PAIcom.OWW", new Version(1, 0, 0, 0));

                // Need to wire the actual call inside OnAudioChunkAvailable
                var helperType = module.Find("CrossPlatformPatcherOWW", isReflectionName: false) as TypeDef;
                var onAudioMethod = helperType?.FindMethod("OnAudioChunkAvailable");
                if (onAudioMethod != null)
                {
                    var owwHelperTypeRef = new TypeRefUser(module, "CrossPlatformPatcher.Core", "OpenWakeWordHelper", owwRef);
                    var enqueueMethodRef = new MemberRefUser(module, "EnqueueAudio", 
                        MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Object), 
                        owwHelperTypeRef);

                    // Clear heuristic trigger bodies to just call the real DLL wrapper
                    onAudioMethod.Body.Instructions.Clear();
                    onAudioMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                    onAudioMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Call, enqueueMethodRef));
                    onAudioMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    onAudioMethod.Body.OptimizeBranches();
                    onAudioMethod.Body.OptimizeMacros();
                }

                // Use the managed writer - NativeModuleWriter requires all existing
                // RIDs to be preserved which conflicts with newly added types.
                var writerOptions = new ModuleWriterOptions(module)
                {
                    WritePdb = false,
                };

                module.Write(outputPath, writerOptions);
            }

            Log($"Compat build: Wrote PAIcom.OWW.dll and dependencies to output directory.");

            // Generate OS launcher scripts alongside the patched exe
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

