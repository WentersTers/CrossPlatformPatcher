using CrossPlatformPatcher.Core;
using System.Security.Cryptography;

namespace CrossPlatformPatcher;

/// <summary>
/// Cross-Platform PAIcom Binary-Patch Injector Entry Point
///
/// Runs on Windows, Linux, and macOS. Patches the user's own copy of PAIcom.exe
/// to replace Windows-only System.Speech with cross-platform Vosk speech recognition.
class Program
{
    private const string BuildFlavor = "Compat";

    static int Main(string[] args)
    {
        Console.WriteLine($"PAIcom Binary-Patch Injector ({BuildFlavor}) v1.0");
        Console.WriteLine("==================================");

        if (args.Length >= 1 && args[0] == "--prepare-onnx-natives")
        {
            var copied = OnnxNativeLibraryManager.PrepareFromNuGetCache(
                Directory.GetCurrentDirectory(),
                Console.WriteLine);
            return copied > 0 ? 0 : 4;
        }

        if (args.Length >= 2 && args[0] == "--test-commands")
        {
            TestCommandInjection.Run(args.Skip(1).ToArray());
            return 0;
        }

        // ── Parse arguments ──────────────────────────────────────────────
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        if (args[0] is "-V" or "--version")
        {
            Console.WriteLine($"CrossPlatformPatcher ({BuildFlavor}) v1.0");
            return 0;
        }

        var inputPath  = args[0];
        string? outPath = null;
        bool dryRun    = false;
        bool backup    = false;
        bool verbose   = false;
        bool analyze   = false;
        bool prepareOnnxNatives = false;
        var migrationMode = MigrationMode.Stable;
        
        // OpenWakeWord settings (parsed from CLI args)
        var defaultOwwSettings = OpenWakeWordSettings.CreateDefault();
        var owwBuilder = OpenWakeWordSettings.CreateBuilder()
            .WithThreshold(defaultOwwSettings.ConfidenceThreshold)
            .WithLockDurationMs(defaultOwwSettings.LockDurationMs)
            .WithAudioChunkSize(defaultOwwSettings.AudioChunkSize)
            .WithInferenceThreadScale(defaultOwwSettings.InferenceThreadPoolScale)
            .WithModelResourceName(defaultOwwSettings.ModelResourceName)
            .WithAudioSampleRate(defaultOwwSettings.AudioSampleRate)
            .WithVerboseLogging(defaultOwwSettings.EnableVerboseLogging)
            .WithMicrophoneBufferMilliseconds(defaultOwwSettings.MicrophoneBufferMilliseconds)
            .WithFuzzyMatchMinConfidence(defaultOwwSettings.FuzzyMatchMinConfidence)
            .WithPostWakeSilenceGraceMilliseconds(defaultOwwSettings.PostWakeSilenceGraceMilliseconds)
            .WithSpeechSilenceCutoffMilliseconds(defaultOwwSettings.SpeechSilenceCutoffMilliseconds);

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":      outPath = args[++i]; break;
                case "--dry-run":  dryRun  = true;      break;
                case "--backup":   backup  = true;      break;
                case "--verbose":  verbose = true;      break;
                case "--analyze":  analyze = true;      break;
                case "--prepare-onnx-natives": prepareOnnxNatives = true; break;
                case "--migration-mode":
                    if (i + 1 < args.Length && MigrationModeParser.TryParse(args[++i], out var parsedMode))
                    {
                        migrationMode = parsedMode;
                    }
                    else
                    {
                        var invalid = i < args.Length ? args[i] : "<missing>";
                        Console.Error.WriteLine($"[WARN] Invalid migration mode: {invalid}. Using stable mode.");
                        migrationMode = MigrationMode.Stable;
                    }
                    break;
                
                // OpenWakeWord CLI arguments
                case "--oww-threshold":
                    if (i + 1 < args.Length && float.TryParse(args[++i], out var thresh))
                        owwBuilder.WithThreshold(thresh);
                    break;
                case "--oww-lock-ms":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var lockMs))
                        owwBuilder.WithLockDurationMs(lockMs);
                    break;
                case "--oww-audio-chunk-size":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var chunkSize))
                        owwBuilder.WithAudioChunkSize(chunkSize);
                    break;
                case "--oww-inference-thread-scale":
                    if (i + 1 < args.Length && float.TryParse(args[++i], out var scale))
                        owwBuilder.WithInferenceThreadScale(scale);
                    break;
                case "--oww-model-resource":
                    if (i + 1 < args.Length)
                        owwBuilder.WithModelResourceName(args[++i]);
                    break;
                case "--oww-audio-sample-rate":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var sampleRate))
                        owwBuilder.WithAudioSampleRate(sampleRate);
                    break;
                case "--oww-verbose-log":
                    owwBuilder.WithVerboseLogging(true);
                    break;
                case "--oww-mic-buffer-ms":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var micBufferMs))
                        owwBuilder.WithMicrophoneBufferMilliseconds(micBufferMs);
                    break;
                case "--oww-fuzzy-match-confidence":
                    if (i + 1 < args.Length && float.TryParse(args[++i], out var fuzzyConfidence))
                        owwBuilder.WithFuzzyMatchMinConfidence(fuzzyConfidence);
                    break;
                case "--oww-post-wake-silence-grace-ms":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var postWakeSilenceGraceMs))
                        owwBuilder.WithPostWakeSilenceGraceMilliseconds(postWakeSilenceGraceMs);
                    break;
                case "--oww-speech-silence-cutoff-ms":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var speechSilenceCutoffMs))
                        owwBuilder.WithSpeechSilenceCutoffMilliseconds(speechSilenceCutoffMs);
                    break;
                    
                default:
                    Console.Error.WriteLine($"[WARN] Unknown argument: {args[i]}");
                    break;
            }
        }

        outPath ??= inputPath + ".patched.exe";

        if (prepareOnnxNatives)
        {
            OnnxNativeLibraryManager.PrepareFromNuGetCache(
                Directory.GetCurrentDirectory(),
                Console.WriteLine);
        }

        // ── Validate input ───────────────────────────────────────────────
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"[ERROR] File not found: {inputPath}");
            return 1;
        }

        // Print SHA-256 so the user can confirm which build they're patching
        byte[] rawBytes;
        try
        {
            using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            rawBytes = new byte[fs.Length];
            fs.ReadExactly(rawBytes);
        }
        catch (IOException ioEx)
        {
            Console.Error.WriteLine($"[ERROR] Cannot read {inputPath}: {ioEx.Message}");
            Console.Error.WriteLine("       Make sure the application is NOT running before patching.");
            return 1;
        }
        var sha256 = Convert.ToHexString(SHA256.HashData(rawBytes));
        Console.WriteLine($"Input   : {inputPath}");
        Console.WriteLine($"SHA-256 : {sha256}");
        Console.WriteLine($"Output  : {(dryRun ? "(dry-run – no write)" : outPath)}");
        Console.WriteLine();

        // ── Analyze mode (early exit) ────────────────────────────────────
        if (analyze)
        {
            AssemblyAnalyzer.Analyze(inputPath);
            return 0;
        }

        // ── Optional backup ──────────────────────────────────────────────
        if (backup && !dryRun)
        {
            var bakPath = inputPath + ".bak";
            File.Copy(inputPath, bakPath, overwrite: true);
            Console.WriteLine($"[INFO] Backup written to {bakPath}");
        }

        // ── Run patcher ──────────────────────────────────────────────────
        try
        {
            var owwSettings = owwBuilder.Build();
            Console.WriteLine();
            Console.WriteLine("OpenWakeWord Settings:");
            Console.WriteLine($"  Threshold        : {owwSettings.ConfidenceThreshold:F3}");
            Console.WriteLine($"  Lock Duration    : {owwSettings.LockDurationMs} ms");
            Console.WriteLine($"  Audio Chunk Size : {owwSettings.AudioChunkSize} samples");
            Console.WriteLine($"  Thread Scale     : {owwSettings.InferenceThreadPoolScale:F2}");
            Console.WriteLine($"  Mic Buffer       : {owwSettings.MicrophoneBufferMilliseconds} ms");
            Console.WriteLine($"  Fuzzy Confidence : {owwSettings.FuzzyMatchMinConfidence:F2}");
            Console.WriteLine($"  Wake Grace       : {owwSettings.PostWakeSilenceGraceMilliseconds} ms");
            Console.WriteLine($"  Silence Cutoff   : {owwSettings.SpeechSilenceCutoffMilliseconds} ms");
            Console.WriteLine($"  Migration Mode   : {MigrationModeParser.ToCliString(migrationMode)}");
            Console.WriteLine();
            
            var patcher = new AssemblyPatcher(verbose, owwSettings, migrationMode);
            var result  = patcher.Patch(inputPath, outPath, dryRun);

            Console.WriteLine();
            Console.WriteLine("── Patch Results ───────────────────────────────────────────────");
            Console.WriteLine($"  Methods scanned   : {result.MethodsScanned}");
            Console.WriteLine($"  Patch points found: {result.PatchPointsFound}");
            Console.WriteLine($"  Patch points hit  : {result.PatchPointsApplied}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("[ERRORS]");
                foreach (var e in result.Errors)
                    Console.Error.WriteLine($"  {e}");
                return 2;
            }

            if (dryRun)
            {
                Console.WriteLine();
                Console.WriteLine("[DRY-RUN] No file written.  All patch points located successfully.");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine($"[OK] Patched assembly written to: {outPath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[FATAL] {ex.GetType().Name}: {ex.Message}");
            if (verbose)
                Console.Error.WriteLine(ex.StackTrace);
            return 3;
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
        Usage:
            CrossPlatformPatcher <path-to-PAIcom.exe> [options]

        Options:
            --out <file>                    Output path  (default: <input>.patched.exe)
            --dry-run                       Find patch points without writing output
            --backup                        Write <input>.bak before patching
            --verbose                       Detailed IL scan output
            --analyze                       Analyze assembly and print method report (no patch)
            --prepare-onnx-natives          Copy ONNX Runtime native libs from NuGet cache into Core/NativeLibraries
            --migration-mode <stable|probe|full>
                                          Launcher migration mode (default: stable)
            -V, --version                   Print version and exit

        OpenWakeWord Options:
            --oww-threshold <0.0-1.0>       Wake word confidence threshold (default: 0.7)
            --oww-lock-ms <ms>              Hard lock duration in milliseconds (default: 3000, arm64: 3200)
            --oww-audio-chunk-size <n>      Audio chunk size in samples (default: 1024, arm64: 960)
            --oww-inference-thread-scale <n> ThreadPool scaling 0.5-2.0 (default: 1.0)
            --oww-model-resource <name>     ONNX model resource name (default: oww.model.hey_pie_com.quant.onnx)
            --oww-audio-sample-rate <hz>    Audio sample rate in Hz (default: 16000)
            --oww-verbose-log               Enable verbose OWW logging
            --oww-mic-buffer-ms <ms>        Microphone buffer size in milliseconds (default: 200, arm64: 160)
            --oww-fuzzy-match-confidence <f> Fuzzy command match confidence (default: 0.80)
            --oww-post-wake-silence-grace-ms <ms>
                                         Silence grace after wake before cut-off starts (default: 450, arm64: 350)
            --oww-speech-silence-cutoff-ms <ms>
                                         Silence duration that ends Vosk capture (default: 1000, arm64: 850)

        Description:
            Replaces Windows-only System.Speech with cross-platform Vosk
            speech recognition + OpenWakeWord wake word detection.
            Outputs a patched executable compatible with Windows, Linux,
            and macOS via Wine/Mono.

        Example:
            CrossPlatformPatcher PAIcom.exe --oww-threshold 0.65 --oww-lock-ms 2500 --verbose
        """);
    }
}
