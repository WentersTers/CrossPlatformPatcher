namespace CrossPlatformPatcher.Cli;

/// <summary>Centralised help text so the entry point stays small.</summary>
public static class CliHelpText
{
    public static string Text { get; } = """
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
        """;
}
