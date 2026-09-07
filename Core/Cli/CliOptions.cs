using CrossPlatformPatcher.Core;

namespace CrossPlatformPatcher.Cli;

/// <summary>
/// Immutable, parsed representation of the command line.
/// Extracted from the entry point so argument parsing is testable and the
/// entry point stays a thin dispatcher.
/// </summary>
public sealed class CliOptions
{
    public CliVerb Verb { get; init; } = CliVerb.Patch;

    public string? InputPath { get; init; }

    /// <summary>Effective output path (defaults to <c><input>.patched.exe</c>).</summary>
    public string? OutputPath { get; init; }

    public bool DryRun { get; init; }
    public bool Backup { get; init; }
    public bool Verbose { get; init; }
    public bool Analyze { get; init; }

    /// <summary>True when <c>--prepare-onnx-natives</c> appeared as a later flag.</summary>
    public bool PrepareOnnxNatives { get; init; }

    public MigrationMode MigrationMode { get; init; } = MigrationMode.Stable;
    public OpenWakeWordSettings OwwSettings { get; init; } = OpenWakeWordSettings.CreateDefault();

    /// <summary>Non-fatal parser warnings, printed by the entry point.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Arguments following <c>--test-commands</c>.</summary>
    public string[] TestCommandsArgs { get; init; } = [];

    /// <summary>
    /// Parse <paramref name="args"/> into a <see cref="CliOptions"/>.
    /// Does not write to the console; warnings are collected for the caller.
    /// </summary>
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
            return new CliOptions { Verb = CliVerb.Help };

        if (args[0] is "-V" or "--version")
            return new CliOptions { Verb = CliVerb.Version };

        if (args[0] == "--prepare-onnx-natives")
            return new CliOptions { Verb = CliVerb.PrepareOnnxNatives };

        if (args.Length >= 2 && args[0] == "--test-commands")
            return new CliOptions { Verb = CliVerb.TestCommands, TestCommandsArgs = args.Skip(1).ToArray() };

        // ── Patch mode ─────────────────────────────────────────────────────
        var warnings = new List<string>();
        var inputPath = args[0];

        string? outPath = null;
        var dryRun = false;
        var backup = false;
        var verbose = false;
        var analyze = false;
        var prepareOnnx = false;
        var migrationMode = MigrationMode.Stable;

        var owwBuilder = OpenWakeWordSettings.CreateBuilder();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outPath = args[++i]; break;
                case "--dry-run": dryRun = true; break;
                case "--backup": backup = true; break;
                case "--verbose": verbose = true; break;
                case "--analyze": analyze = true; break;
                case "--prepare-onnx-natives": prepareOnnx = true; break;

                case "--migration-mode":
                    if (i + 1 < args.Length && MigrationModeParser.TryParse(args[++i], out var parsedMode))
                    {
                        migrationMode = parsedMode;
                    }
                    else
                    {
                        var invalid = i < args.Length ? args[i] : "<missing>";
                        warnings.Add($"[WARN] Invalid migration mode: {invalid}. Using stable mode.");
                        migrationMode = MigrationMode.Stable;
                    }
                    break;

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
                    warnings.Add($"[WARN] Unknown argument: {args[i]}");
                    break;
            }
        }

        return new CliOptions
        {
            Verb = CliVerb.Patch,
            InputPath = inputPath,
            OutputPath = outPath ?? inputPath + ".patched.exe",
            DryRun = dryRun,
            Backup = backup,
            Verbose = verbose,
            Analyze = analyze,
            PrepareOnnxNatives = prepareOnnx,
            MigrationMode = migrationMode,
            OwwSettings = owwBuilder.Build(),
            Warnings = warnings,
        };
    }
}
