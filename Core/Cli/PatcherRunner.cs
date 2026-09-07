using System.Security.Cryptography;
using CrossPlatformPatcher.Core;

namespace CrossPlatformPatcher.Cli;

/// <summary>
/// Data-oriented outcome of a patch run. Human-readable summary content is
/// routed through the <c>log</c> callback; machine-usable state (exit code,
/// hash, counters, warnings, errors) is returned here so callers — and tests —
/// don't have to capture stdout/stderr.
/// </summary>
public sealed record PatcherRunOutcome(
    int ExitCode,
    string? OutputPath,
    string? Sha256,
    int PatchPointsFound,
    int PatchPointsApplied,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    bool SkippedDueToDryRun);

/// <summary>
/// Encapsulates the end-to-end patch flow previously inlined in
/// <c>Program.RunPatch</c>: validate input, hash, optional backup, dry-run
/// short-circuit, patch invocation, error-to-exit-code mapping, and result
/// rendering. This keeps <c>Program</c> a genuine thin dispatcher.
/// </summary>
public static class PatcherRunner
{
    /// <summary>
    /// Run the patch flow described by <paramref name="options"/>.
    /// </summary>
    /// <param name="options">Parsed CLI options (see <see cref="CliOptions"/>).</param>
    /// <param name="log">Receives the human-readable summary lines that the CLI
    /// would otherwise print to stdout. Defaults to <see cref="Console.WriteLine"/>.</param>
    /// <returns>A <see cref="PatcherRunOutcome"/> with exit code, hash, counters, and
    /// any collected warnings/errors.</returns>
    public static PatcherRunOutcome Run(CliOptions options, Action<string>? log = null)
    {
        var logger = log ?? Console.WriteLine;
        var errors = new List<string>();
        var warnings = new List<string>(options.Warnings);
        var outputPath = options.OutputPath!;

        if (options.PrepareOnnxNatives)
        {
            OnnxNativeLibraryManager.PrepareFromNuGetCache(Directory.GetCurrentDirectory(), logger);
        }

        var inputPath = options.InputPath!;

        // ── Validate input ───────────────────────────────────────────────
        if (!File.Exists(inputPath))
        {
            errors.Add($"[ERROR] File not found: {inputPath}");
            return new PatcherRunOutcome(1, null, null, 0, 0, warnings, errors, SkippedDueToDryRun: false);
        }

        byte[] rawBytes;
        try
        {
            using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            rawBytes = new byte[fs.Length];
            fs.ReadExactly(rawBytes);
        }
        catch (IOException ioEx)
        {
            errors.Add($"[ERROR] Cannot read {inputPath}: {ioEx.Message}");
            errors.Add("       Make sure the application is NOT running before patching.");
            return new PatcherRunOutcome(1, null, null, 0, 0, warnings, errors, SkippedDueToDryRun: false);
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(rawBytes));
        logger($"Input   : {inputPath}");
        logger($"SHA-256 : {sha256}");
        logger($"Output  : {(options.DryRun ? "(dry-run – no write)" : outputPath)}");
        logger("");

        // ── Analyze mode (early exit) ────────────────────────────────────
        if (options.Analyze)
        {
            AssemblyAnalyzer.Analyze(inputPath);
            return new PatcherRunOutcome(0, null, sha256, 0, 0, warnings, errors, SkippedDueToDryRun: false);
        }

        // ── Optional backup ──────────────────────────────────────────────
        if (options.Backup && !options.DryRun)
        {
            var bakPath = inputPath + ".bak";
            File.Copy(inputPath, bakPath, overwrite: true);
            logger($"[INFO] Backup written to {bakPath}");
        }

        // ── Run patcher ──────────────────────────────────────────────────
        try
        {
            PrintOwwSettings(options.OwwSettings, options.MigrationMode, logger);

            var patcher = new AssemblyPatcher(options.Verbose, options.OwwSettings, options.MigrationMode);
            var result = patcher.Patch(inputPath, outputPath, options.DryRun);

            logger("");
            logger("── Patch Results ───────────────────────────────────────────────");
            logger($"  Methods scanned   : {result.MethodsScanned}");
            logger($"  Patch points found: {result.PatchPointsFound}");
            logger($"  Patch points hit  : {result.PatchPointsApplied}");

            if (result.Errors.Count > 0)
            {
                logger("");
                logger("[ERRORS]");
                foreach (var e in result.Errors)
                    errors.Add($"  {e}");
                return new PatcherRunOutcome(2, outputPath, sha256, result.PatchPointsFound, result.PatchPointsApplied, warnings, errors, SkippedDueToDryRun: false);
            }

            if (options.DryRun)
            {
                logger("");
                logger("[DRY-RUN] No file written.  All patch points located successfully.");
                return new PatcherRunOutcome(0, outputPath, sha256, result.PatchPointsFound, result.PatchPointsApplied, warnings, errors, SkippedDueToDryRun: true);
            }

            logger("");
            logger($"[OK] Patched assembly written to: {outputPath}");
            return new PatcherRunOutcome(0, outputPath, sha256, result.PatchPointsFound, result.PatchPointsApplied, warnings, errors, SkippedDueToDryRun: false);
        }
        catch (Exception ex)
        {
            errors.Add($"\n[FATAL] {ex.GetType().Name}: {ex.Message}");
            if (options.Verbose && ex.StackTrace != null)
                errors.Add(ex.StackTrace);
            return new PatcherRunOutcome(3, outputPath, sha256, 0, 0, warnings, errors, SkippedDueToDryRun: false);
        }
    }

    private static void PrintOwwSettings(OpenWakeWordSettings s, MigrationMode mode, Action<string> log)
    {
        log("");
        log("OpenWakeWord Settings:");
        log($"  Threshold        : {s.ConfidenceThreshold:F3}");
        log($"  Lock Duration    : {s.LockDurationMs} ms");
        log($"  Audio Chunk Size : {s.AudioChunkSize} samples");
        log($"  Thread Scale     : {s.InferenceThreadPoolScale:F2}");
        log($"  Mic Buffer       : {s.MicrophoneBufferMilliseconds} ms");
        log($"  Fuzzy Confidence : {s.FuzzyMatchMinConfidence:F2}");
        log($"  Wake Grace       : {s.PostWakeSilenceGraceMilliseconds} ms");
        log($"  Silence Cutoff   : {s.SpeechSilenceCutoffMilliseconds} ms");
        log($"  Migration Mode   : {MigrationModeParser.ToCliString(mode)}");
        log("");
    }
}
