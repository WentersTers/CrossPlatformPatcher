using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using CrossPlatformPatcher.Core.Modules;
using CrossPlatformPatcher.PeImage;
using CrossPlatformPatcher.Resources;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Top-level orchestrator.  Loads the target module, runs each patch step,
/// and (optionally) writes the modified PE to disk.
/// </summary>
public class AssemblyPatcher
{
    private readonly bool _verbose;
    private readonly OpenWakeWordSettings _owwSettings;
    private readonly MigrationMode _migrationMode;

    public AssemblyPatcher(bool verbose = false, OpenWakeWordSettings? owwSettings = null, MigrationMode migrationMode = MigrationMode.Stable)
    {
        _verbose = verbose;
        _owwSettings = owwSettings ?? OpenWakeWordSettings.CreateDefault();
        _migrationMode = migrationMode;
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
        var peMachine = GetMachineValue(peBytes);
        var peMachineLabel = FormatMachine(peMachine);
        Log($"Architecture diagnostics: target PE machine={peMachineLabel} (0x{peMachine:X4})");
        Log($"Architecture diagnostics: launcher migration mode={MigrationModeParser.ToCliString(_migrationMode)}");
        result.TargetMachine = $"0x{peMachine:X4}";
        result.TargetArchitecture = peMachineLabel;

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

        // ── Run patch modules ──────────────────────────────────────────────
        // Modules are discovered dynamically, so adding a new feature never
        // requires editing this engine. Failures are isolated per-module.
        var moduleRun = ModuleRegistry.RunAll(
            new PatchModuleContext
            {
                Module = module,
                Log = Log,
                OwwSettings = _owwSettings,
                MigrationMode = _migrationMode,
            });

        result.PatchPointsFound = moduleRun.TotalPatchPointsFound;
        result.PatchPointsApplied = moduleRun.TotalPatchPointsApplied;
        foreach (var err in moduleRun.Errors)
            result.Errors.Add(err);

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
            var isX86Target = IsX86Target(peBytes);
            
            // In Probe/Full mode with x86 PE, we will rewrite CorFlags to allow 64-bit execution.
            // Therefore, we must extract x64 natives, not x86, so they're compatible with the 64-bit process.
            var shouldUse64BitNatives = (isX86Target && (_migrationMode == MigrationMode.Probe || _migrationMode == MigrationMode.Full));
            var effectiveArchitecture = shouldUse64BitNatives ? "x64" : (isX86Target ? "x86" : "x64");
            
            var onnxNativeResource = shouldUse64BitNatives 
                ? "onnxruntime.native.win-x64.dll" 
                : GetOnnxNativeResourceName(peBytes);
            
            result.OnnxNativeResource = onnxNativeResource;
            if (onnxNativeResource != null)
                Log($"Native diagnostics: selected ONNX runtime bundle '{onnxNativeResource}' (effective arch: {effectiveArchitecture}).");

            Log("Extracting PAIcom.OWW.dll and dependencies …");

            // Pick the full set of embedded library resource names for this target.
            var embeddedDlls = NativeLibraryExtractor.BuildEmbeddedDllNames(shouldUse64BitNatives, isX86Target);
            if (shouldUse64BitNatives)
                Log("Migration mode is Probe/Full with x86 PE: extracting x64 Vosk natives for 64-bit probe execution.");
            else if (isX86Target)
                Log("Target machine is x86 (Stable mode): extracting win32 Vosk native libraries.");
            else
                Log("Target machine is x64; extracting win64 Vosk native libraries.");

            // Load each embedded resource and map it to its deployment name.
            var nativeSources = new List<(string SourceName, Stream Stream)>();
            foreach (var dll in embeddedDlls)
            {
                var s = typeof(AssemblyPatcher).Assembly.GetManifestResourceStream(dll);
                if (s != null)
                    nativeSources.Add((dll, s));
            }

            foreach (var artifact in NativeLibraryExtractor.ExtractFromStreams(nativeSources))
            {
                var destPath = Path.Combine(outputDir, artifact.TargetName);
                File.WriteAllBytes(destPath, artifact.Bytes);
                result.ExtractedNativeLibraries.Add(artifact.TargetName);
                Log($"Native diagnostics: extracted {artifact.TargetName}.");
            }

            if (onnxNativeResource != null)
            {
                using var s = typeof(AssemblyPatcher).Assembly.GetManifestResourceStream(onnxNativeResource);
                if (s != null)
                {
                    var destPath = Path.Combine(outputDir, "onnxruntime.dll");
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    File.WriteAllBytes(destPath, ms.ToArray());
                    result.ExtractedNativeLibraries.Add("onnxruntime.dll");
                    Log($"Native diagnostics: extracted onnxruntime.dll from {onnxNativeResource}.");
                }
            }

            bool probeCorFlagsApplied = false;
            var finalOutputPath = outputPath;
            var stagedOutputPath = Path.Combine(outputDir, Path.GetFileName(outputPath) + ".staged");

            if (File.Exists(stagedOutputPath))
                File.Delete(stagedOutputPath);

            // If no IL patching happened, preserve the original bytes exactly.
            // This avoids resource mapping regressions in heavily obfuscated builds.
            if (result.PatchPointsApplied == 0)
            {
                File.WriteAllBytes(stagedOutputPath, peBytes);
                Log("Compat build: wrote byte-for-byte copy (no IL/resource rewrite).");
            }
            else
            {
                // Inject the OpenWakeWord helper assembly reference so that cross-assembly calls work
                var owwRef = new AssemblyRefUser("PAIcom.OWW", new Version(1, 0, 0, 0));

                // NOTE: DO NOT clear or modify the OnAudioChunkAvailable method body here.
                // OpenWakeWordCompatibilityPatcher has already built complex logic into it including:
                // - Initialization
                // - First audio logging
                // - Wake-lock detection and triggering  
                // - Optional call to external EnqueueAudio
                // By adding the assembly reference above, we enable the injected code to successfully
                // reference and call OpenWakeWordHelper.EnqueueAudio() from PAIcom.OWW.dll.
                // Just keep the method as-is.

                // Use the managed writer - NativeModuleWriter requires all existing
                // RIDs to be preserved which conflicts with newly added types.
                var writerOptions = new ModuleWriterOptions(module)
                {
                    WritePdb = false,
                };

                module.Write(stagedOutputPath, writerOptions);
            }

            probeCorFlagsApplied = ApplyProbeCorFlags(stagedOutputPath, peMachine, result);

            if (File.Exists(finalOutputPath))
                File.Delete(finalOutputPath);

            File.Move(stagedOutputPath, finalOutputPath);

            // Manifest AFTER the probe: result.ProbeCorFlags* now carry what
            // actually happened. Writing it earlier asserts defaults over an
            // applied rewrite (observed live: Attempted/Applied False on a
            // migrated binary). Effects over acknowledgments, in our own paperwork.
            NativeManifestWriter.Write(
                Path.Combine(outputDir, "NATIVES_MANIFEST.txt"),
                new NativeManifestData(
                    result.TargetMachine ?? string.Empty,
                    effectiveArchitecture,
                    result.OnnxNativeResource,
                    result.ProbeCorFlagsAttempted,
                    result.ProbeCorFlagsApplied,
                    result.ProbeCorFlagsReason,
                    _migrationMode,
                    result.ExtractedNativeLibraries));

            // Bake the ACTUAL post-patch CLI flags into the launchers so the
            // runtime gate compares against measured bytes, not assumptions.
            uint bakedExeCorFlags = 0;
            var bakedFlagsKnown = false;
            try
            {
                var finalBytes = File.ReadAllBytes(finalOutputPath);
                if (CrossPlatformPatcher.PeImage.CorFlagsRewriter.TryReadCliFlags(
                        finalBytes, out var readFlags, out _))
                {
                    bakedExeCorFlags = readFlags;
                    bakedFlagsKnown = true;
                }
            }
            catch (Exception ex)
            {
                Log($"Bitness bake: could not read final CLI flags: {ex.GetType().Name}; launchers ship unknown.");
            }

            Log($"Compat build: Wrote PAIcom.OWW.dll and dependencies to output directory.");

            // Generate OS launcher scripts alongside the patched exe
            LauncherGenerator.WriteAll(outputDir, Path.GetFileName(finalOutputPath), _migrationMode, peMachineLabel, probeCorFlagsApplied, bakedFlagsKnown ? (uint?)bakedExeCorFlags : null);
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

    private static bool IsX86Target(byte[] peBytes)
    {
        return GetMachineValue(peBytes) == 0x014c;
    }

    private static ushort GetMachineValue(byte[] peBytes)
    {
        if (peBytes.Length < 0x40)
            return 0x014c;

        var peHeaderOffset = BitConverter.ToInt32(peBytes, 0x3C);
        if (peHeaderOffset <= 0 || peHeaderOffset + 6 >= peBytes.Length)
            return 0x014c;

        return BitConverter.ToUInt16(peBytes, peHeaderOffset + 4);
    }

    private static string FormatMachine(ushort machine)
    {
        return machine switch
        {
            0x014c => "x86",
            0x8664 => "x64",
            0xAA64 => "arm64",
            _ => "unknown",
        };
    }

    private static string? GetOnnxNativeResourceName(byte[] peBytes)
    {
        if (peBytes.Length < 0x40)
            return null;

        var peHeaderOffset = BitConverter.ToInt32(peBytes, 0x3C);
        if (peHeaderOffset <= 0 || peHeaderOffset + 6 >= peBytes.Length)
            return null;

        var machine = BitConverter.ToUInt16(peBytes, peHeaderOffset + 4);
        return machine switch
        {
            0x014c => "onnxruntime.native.win-x86.dll",
            0x8664 => "onnxruntime.native.win-x64.dll",
            0xAA64 => "onnxruntime.native.win-arm64.dll",
            _ => null,
        };
    }

    /// <summary>
    /// Orchestrates the 64-bit probe CorFlags rewrite.  Guards on migration mode
    /// and the original machine type stay here (they depend on instance state and
    /// the PE machine); the byte-level rewrite is delegated to
    /// <see cref="CorFlagsRewriter"/> and the resulting buffer is persisted back
    /// to the staged output file when it was actually modified.
    /// </summary>
    private bool ApplyProbeCorFlags(string stagedOutputPath, ushort originalMachine, PatchResult result)
    {
        if (_migrationMode == MigrationMode.Stable)
            return false;

        if (originalMachine != 0x014c)
        {
            Log("Probe migration: target is already non-x86; skipping CorFlags probe rewrite.");
            return false;
        }

        result.ProbeCorFlagsAttempted = true;

        try
        {
            var bytes = File.ReadAllBytes(stagedOutputPath);
            if (!CorFlagsRewriter.TryApply64BitProbeCorFlags(bytes, out var flagsOffset, out var reason))
            {
                Log($"Probe migration: {reason}; keeping stable x86 metadata.");
                result.ProbeCorFlagsReason = reason;
                return false;
            }

            result.ProbeCorFlagsApplied = true;
            result.ProbeCorFlagsReason = reason;

            if (reason == "APPLIED")
            {
                // The buffer was modified in place; persist it back to the staged file.
                File.WriteAllBytes(stagedOutputPath, bytes);
                var flags = BitConverter.ToUInt32(bytes, flagsOffset);
                var rewritten = flags & ~0x00000002u & ~0x00020000u;
                Log($"Probe migration: cleared CorFlags 32BITREQUIRED/32BITPREFERRED (0x{flags:X8} -> 0x{rewritten:X8}).");
            }
            else
            {
                Log("Probe migration: 32-bit flags were already cleared.");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"Probe migration: failed to update CorFlags: {ex.GetType().Name}: {ex.Message}");
            result.ProbeCorFlagsReason = "WRITE_ERROR";
            return false;
        }
    }
}

/// <summary>Results returned after a patch run.</summary>
public class PatchResult
{
    public int MethodsScanned      { get; set; }
    public int PatchPointsFound    { get; set; }
    public int PatchPointsApplied  { get; set; }
    public string? TargetMachine   { get; set; }
    public string? TargetArchitecture { get; set; }
    public string? OnnxNativeResource { get; set; }
    public bool ProbeCorFlagsAttempted { get; set; }
    public bool ProbeCorFlagsApplied { get; set; }
    public string? ProbeCorFlagsReason { get; set; }
    public List<string> ExtractedNativeLibraries { get; } = [];
    public List<string> Errors     { get; } = [];
}

