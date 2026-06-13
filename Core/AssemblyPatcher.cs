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
            var embeddedDlls = new List<string>
            {
                "PAIcom.OWW.dll",
                "System.Memory.dll",
                "System.Buffers.dll",
                "System.Numerics.Vectors.dll",
                "System.Runtime.CompilerServices.Unsafe.dll",
                "onnxruntime.managed.dll",
                "vosk.managed.dll",
                "naudio.core.dll",
                "naudio.winmm.dll"
            };

            if (shouldUse64BitNatives)
            {
                embeddedDlls.AddRange([
                    "vosk.native.win-x64.dll",
                    "vosk.native.win-gcc.dll",
                    "vosk.native.win-stdc.dll",
                    "vosk.native.win-pthread.dll"
                ]);
                Log("Migration mode is Probe/Full with x86 PE: extracting x64 Vosk natives for 64-bit probe execution.");
            }
            else if (isX86Target)
            {
                embeddedDlls.AddRange([
                    "vosk.native.win-x86.dll",
                    "vosk.native.win-gcc-x86.dll",
                    "vosk.native.win-stdc-x86.dll",
                    "vosk.native.win-pthread-x86.dll"
                ]);
                Log("Target machine is x86 (Stable mode): extracting win32 Vosk native libraries.");
            }
            else
            {
                embeddedDlls.AddRange([
                    "vosk.native.win-x64.dll",
                    "vosk.native.win-gcc.dll",
                    "vosk.native.win-stdc.dll",
                    "vosk.native.win-pthread.dll"
                ]);
                Log("Target machine is x64; extracting win64 Vosk native libraries.");
            }

            foreach (var dll in embeddedDlls)
            {
                using var s = typeof(AssemblyPatcher).Assembly.GetManifestResourceStream(dll);
                if (s != null)
                {
                    var outName = dll switch
                    {
                        "onnxruntime.managed.dll" => "Microsoft.ML.OnnxRuntime.dll",
                        "vosk.managed.dll" => "Vosk.dll",
                        "vosk.native.win-x86.dll" => "libvosk.dll",
                        "vosk.native.win-gcc-x86.dll" => "libgcc_s_sjlj-1.dll",
                        "vosk.native.win-stdc-x86.dll" => "libstdc++-6.dll",
                        "vosk.native.win-pthread-x86.dll" => "libwinpthread-1.dll",
                        "vosk.native.win-x64.dll" => "libvosk.dll",
                        "vosk.native.win-gcc.dll" => "libgcc_s_seh-1.dll",
                        "vosk.native.win-stdc.dll" => "libstdc++-6.dll",
                        "vosk.native.win-pthread.dll" => "libwinpthread-1.dll",
                        "naudio.core.dll" => "NAudio.Core.dll",
                        "naudio.winmm.dll" => "NAudio.WinMM.dll",
                        _ => dll,
                    };
                    var destPath = Path.Combine(outputDir, outName);
                    using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, false);
                    s.CopyTo(fs);
                    result.ExtractedNativeLibraries.Add(outName);
                    Log($"Native diagnostics: extracted {outName} from {dll}.");
                }
            }

            if (onnxNativeResource != null)
            {
                using var s = typeof(AssemblyPatcher).Assembly.GetManifestResourceStream(onnxNativeResource);
                if (s != null)
                {
                    var destPath = Path.Combine(outputDir, "onnxruntime.dll");
                    using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, false);
                    s.CopyTo(fs);
                    result.ExtractedNativeLibraries.Add("onnxruntime.dll");
                    Log($"Native diagnostics: extracted onnxruntime.dll from {onnxNativeResource}.");
                }
            }

            // Write native library manifest for verification
            WriteNativeLibraryManifest(outputDir, result, effectiveArchitecture, _migrationMode);

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

            probeCorFlagsApplied = TryApply64BitProbeCorFlags(stagedOutputPath, peMachine, result);

            if (File.Exists(finalOutputPath))
                File.Delete(finalOutputPath);

            File.Move(stagedOutputPath, finalOutputPath);

            Log($"Compat build: Wrote PAIcom.OWW.dll and dependencies to output directory.");

            // Generate OS launcher scripts alongside the patched exe
            LauncherGenerator.WriteAll(outputDir, Path.GetFileName(finalOutputPath), _migrationMode, peMachineLabel, probeCorFlagsApplied);
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

    private bool TryApply64BitProbeCorFlags(string outputPath, ushort originalMachine, PatchResult result)
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
            var bytes = File.ReadAllBytes(outputPath);
            if (!TryGetCliFlagsOffset(bytes, out var flagsOffset))
            {
                Log("Probe migration: CLI header flags not found; keeping stable x86 metadata.");
                result.ProbeCorFlagsReason = "CLI_HEADER_NOT_FOUND";
                return false;
            }

            var flags = BitConverter.ToUInt32(bytes, flagsOffset);
            const uint ComImageFlagsILOnly = 0x00000001;
            const uint ComImageFlags32BitRequired = 0x00000002;
            const uint ComImageFlags32BitPreferred = 0x00020000;

            if ((flags & ComImageFlagsILOnly) == 0)
            {
                Log("Probe migration: module is not IL-only; skipping CorFlags probe rewrite.");
                result.ProbeCorFlagsReason = "NOT_IL_ONLY";
                return false;
            }

            var rewritten = flags & ~ComImageFlags32BitRequired & ~ComImageFlags32BitPreferred;
            if (rewritten == flags)
            {
                Log("Probe migration: 32-bit flags were already cleared.");
                result.ProbeCorFlagsApplied = true;
                result.ProbeCorFlagsReason = "ALREADY_CLEARED";
                return true;
            }

            var rewrittenBytes = BitConverter.GetBytes(rewritten);
            Buffer.BlockCopy(rewrittenBytes, 0, bytes, flagsOffset, rewrittenBytes.Length);
            File.WriteAllBytes(outputPath, bytes);

            Log($"Probe migration: cleared CorFlags 32BITREQUIRED/32BITPREFERRED (0x{flags:X8} -> 0x{rewritten:X8}).");
            result.ProbeCorFlagsApplied = true;
            result.ProbeCorFlagsReason = "APPLIED";
            return true;
        }
        catch (Exception ex)
        {
            Log($"Probe migration: failed to update CorFlags: {ex.GetType().Name}: {ex.Message}");
            result.ProbeCorFlagsReason = "WRITE_ERROR";
            return false;
        }
    }

    private static bool TryGetCliFlagsOffset(byte[] peBytes, out int flagsOffset)
    {
        flagsOffset = -1;
        if (peBytes.Length < 0x100)
            return false;

        var peHeaderOffset = BitConverter.ToInt32(peBytes, 0x3C);
        if (peHeaderOffset <= 0 || peHeaderOffset + 24 >= peBytes.Length)
            return false;

        if (peBytes[peHeaderOffset] != 'P' || peBytes[peHeaderOffset + 1] != 'E')
            return false;

        var numberOfSections = BitConverter.ToUInt16(peBytes, peHeaderOffset + 6);
        var optionalHeaderSize = BitConverter.ToUInt16(peBytes, peHeaderOffset + 20);
        var optionalHeaderOffset = peHeaderOffset + 24;
        if (optionalHeaderOffset + optionalHeaderSize >= peBytes.Length)
            return false;

        var magic = BitConverter.ToUInt16(peBytes, optionalHeaderOffset);
        int dataDirectoryOffset = magic switch
        {
            0x10b => optionalHeaderOffset + 96,
            0x20b => optionalHeaderOffset + 112,
            _ => -1,
        };

        if (dataDirectoryOffset < 0)
            return false;

        const int cliDirectoryIndex = 14;
        var cliDirectoryOffset = dataDirectoryOffset + (cliDirectoryIndex * 8);
        if (cliDirectoryOffset + 8 > peBytes.Length)
            return false;

        var cliHeaderRva = BitConverter.ToInt32(peBytes, cliDirectoryOffset);
        if (cliHeaderRva <= 0)
            return false;

        var sectionTableOffset = optionalHeaderOffset + optionalHeaderSize;
        var cliHeaderOffset = RvaToFileOffset(peBytes, sectionTableOffset, numberOfSections, cliHeaderRva);
        if (cliHeaderOffset <= 0)
            return false;

        var candidateFlagsOffset = cliHeaderOffset + 16;
        if (candidateFlagsOffset + 4 > peBytes.Length)
            return false;

        flagsOffset = candidateFlagsOffset;
        return true;
    }

    private static int RvaToFileOffset(byte[] peBytes, int sectionTableOffset, int numberOfSections, int rva)
    {
        for (int i = 0; i < numberOfSections; i++)
        {
            var sectionOffset = sectionTableOffset + (i * 40);
            if (sectionOffset + 40 > peBytes.Length)
                return -1;

            var virtualSize = BitConverter.ToInt32(peBytes, sectionOffset + 8);
            var virtualAddress = BitConverter.ToInt32(peBytes, sectionOffset + 12);
            var sizeOfRawData = BitConverter.ToInt32(peBytes, sectionOffset + 16);
            var pointerToRawData = BitConverter.ToInt32(peBytes, sectionOffset + 20);
            var span = Math.Max(virtualSize, sizeOfRawData);

            if (rva >= virtualAddress && rva < virtualAddress + span)
                return pointerToRawData + (rva - virtualAddress);
        }

        return -1;
    }

    private static void WriteNativeLibraryManifest(string outputDir, PatchResult result, string targetArchitecture, MigrationMode migrationMode)
    {
        try
        {
            var manifestPath = Path.Combine(outputDir, "NATIVES_MANIFEST.txt");
            var manifestLines = new List<string>
            {
                "PAIcom Cross-Platform Patcher - Native Library Manifest",
                "======================================",
                $"Generated: {DateTime.UtcNow:O}",
                $"Effective Runtime Architecture: {targetArchitecture}",
                $"Migration Mode: {MigrationModeParser.ToCliString(migrationMode)}",
                $"",
                "Extracted Native Libraries:",
            };

            foreach (var lib in result.ExtractedNativeLibraries)
            {
                manifestLines.Add($"  {lib}");
            }

            manifestLines.AddRange([
                "",
                "ONNX Runtime Bundle:",
                $"  {result.OnnxNativeResource ?? "(not selected)"}",
                "",
                "Architecture Selection:",
                $"  PE Machine Type: {result.TargetMachine}",
                $"  Effective Architecture: {targetArchitecture}",
                $"  Migration Mode: {MigrationModeParser.ToCliString(migrationMode)}",
                $"  Note: In Probe/Full modes with x86 PE, x64 natives are extracted",
                $"        because CorFlags rewriting allows the process to run as 64-bit.",
                "",
                "CorFlags Probe Details:",
                $"  Probe Attempted: {result.ProbeCorFlagsAttempted}",
                $"  Probe Applied: {result.ProbeCorFlagsApplied}",
                $"  Reason: {result.ProbeCorFlagsReason ?? "(none)"}",
                "",
                "Verification:",
                "  Run the launcher script with verbose logging enabled.",
                "  Check /launcher-runtime.log for [diag] and [native-load] entries.",
            ]);

            File.WriteAllLines(manifestPath, manifestLines);
            Console.WriteLine($"  [manifest] Wrote manifest to {manifestPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] Failed to write natives manifest: {ex.Message}");
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

