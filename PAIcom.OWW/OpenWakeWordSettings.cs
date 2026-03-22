using System;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Immutable configuration for OpenWakeWord (OWW) wake word detection.
/// 
/// All properties are read-only; construct via factory methods or builder pattern.
/// Thread-safe by design (no mutable state, value types only).
/// 
/// Factory methods support:
/// - CreateDefault() — sensible defaults
/// - FromEnvironmentVariables() — runtime overrides via env vars with PAICOM_OWW_* prefix
/// 
/// Environment variable naming convention:
///   PAICOM_OWW_THRESHOLD              → ConfidenceThreshold (0.0-1.0)
///   PAICOM_OWW_LOCK_MS                → LockDurationMs (milliseconds)
///   PAICOM_OWW_AUDIO_CHUNK_SIZE       → AudioChunkSize (samples)
///   PAICOM_OWW_INFERENCE_THREAD_SCALE → InferenceThreadPoolScale (0.5-2.0)
///   PAICOM_OWW_VERBOSE_LOG            → EnableVerboseLogging (true/false)
/// </summary>
public sealed class OpenWakeWordSettings
{
    /// <summary>Confidence threshold [0.0, 1.0] for wake word detection. Default: 0.7.</summary>
    public float ConfidenceThreshold { get; }

    /// <summary>Hard lock duration in milliseconds. Default: 3000 (3 seconds).</summary>
    public int LockDurationMs { get; }

    /// <summary>Audio chunk size in samples (e.g., 1024 for ~32ms @ 16kHz). Default: 1024.</summary>
    public int AudioChunkSize { get; }

    /// <summary>ThreadPool scaling factor [0.5, 2.0]. 1.0 = normal, 0.5 = low-priority, 2.0 = aggressive. Default: 1.0.</summary>
    public float InferenceThreadPoolScale { get; }

    /// <summary>ONNX model resource name in patched assembly. Default: "oww.model.hey_pie_com.quant.onnx".</summary>
    public string ModelResourceName { get; }

    /// <summary>Audio sample rate in Hz. Default: 16000 (16 kHz).</summary>
    public int AudioSampleRate { get; }

    /// <summary>Enable verbose logging with [oww] prefix. Default: false.</summary>
    public bool EnableVerboseLogging { get; }

    /// <summary>Microphone buffer size in milliseconds. Default: 200 (larger chunks reduce fragmentation). Range: [20, 1000].</summary>
    public int MicrophoneBufferMilliseconds { get; }

    /// <summary>Fuzzy matching minimum confidence for command matching [0.0, 1.0]. Default: 0.80 (80%).</summary>
    public float FuzzyMatchMinConfidence { get; }

    /// <summary>
    /// Internal constructor for immutability. Use CreateDefault() or builder pattern.
    /// </summary>
    internal OpenWakeWordSettings(
        float confidenceThreshold = 0.7f,
        int lockDurationMs = 5000,
        int audioChunkSize = 1024,
        float inferenceThreadPoolScale = 1.0f,
        string modelResourceName = "oww.model.hey_pie_com.quant.onnx",
        int audioSampleRate = 16000,
        bool enableVerboseLogging = false,
        int microphoneBufferMilliseconds = 200,
        float fuzzyMatchMinConfidence = 0.80f)
    {
        // Validate ranges
        if (confidenceThreshold < 0.0f || confidenceThreshold > 1.0f)
            throw new ArgumentException($"ConfidenceThreshold must be in [0.0, 1.0], got {confidenceThreshold}");
        
        if (lockDurationMs < 500 || lockDurationMs > 20000)
            throw new ArgumentException($"LockDurationMs must be in [500, 20000], got {lockDurationMs}");
        
        if (audioChunkSize < 128 || audioChunkSize > 8192)
            throw new ArgumentException($"AudioChunkSize must be in [128, 8192], got {audioChunkSize}");
        
        if (inferenceThreadPoolScale < 0.25f || inferenceThreadPoolScale > 3.0f)
            throw new ArgumentException($"InferenceThreadPoolScale must be in [0.25, 3.0], got {inferenceThreadPoolScale}");
        
        if (string.IsNullOrWhiteSpace(modelResourceName))
            throw new ArgumentException("ModelResourceName cannot be null or empty");
        
        if (audioSampleRate < 8000 || audioSampleRate > 48000)
            throw new ArgumentException($"AudioSampleRate must be in [8000, 48000], got {audioSampleRate}");

        if (microphoneBufferMilliseconds < 20 || microphoneBufferMilliseconds > 1000)
            throw new ArgumentException($"MicrophoneBufferMilliseconds must be in [20, 1000], got {microphoneBufferMilliseconds}");

        if (fuzzyMatchMinConfidence < 0.0f || fuzzyMatchMinConfidence > 1.0f)
            throw new ArgumentException($"FuzzyMatchMinConfidence must be in [0.0, 1.0], got {fuzzyMatchMinConfidence}");

        ConfidenceThreshold = confidenceThreshold;
        LockDurationMs = lockDurationMs;
        AudioChunkSize = audioChunkSize;
        InferenceThreadPoolScale = inferenceThreadPoolScale;
        ModelResourceName = modelResourceName;
        AudioSampleRate = audioSampleRate;
        EnableVerboseLogging = enableVerboseLogging;
        MicrophoneBufferMilliseconds = microphoneBufferMilliseconds;
        FuzzyMatchMinConfidence = fuzzyMatchMinConfidence;
    }

    /// <summary>
    /// Create default settings:
    /// - Threshold: 0.7
    /// - Lock: 5000 ms (increased for better speech recognition)
    /// - ChunkSize: 1024 samples
    /// - ThreadScale: 1.0
    /// - Model: "oww.model.hey_pie_com.quant.onnx"
    /// - SampleRate: 16000 Hz
    /// - VerboseLog: false
    /// </summary>
    public static OpenWakeWordSettings CreateDefault() =>
        new();

    /// <summary>
    /// Create settings from environment variables with PAICOM_OWW_* prefix.
    /// Falls back to defaults for any missing/invalid values.
    /// 
    /// Supports:
    ///   PAICOM_OWW_THRESHOLD (float)
    ///   PAICOM_OWW_LOCK_MS (int)
    ///   PAICOM_OWW_AUDIO_CHUNK_SIZE (int)
    ///   PAICOM_OWW_INFERENCE_THREAD_SCALE (float)
    ///   PAICOM_OWW_MODEL_RESOURCE (string)
    ///   PAICOM_OWW_AUDIO_SAMPLE_RATE (int)
    ///   PAICOM_OWW_VERBOSE_LOG (bool: true/false/1/0)
    ///   PAICOM_OWW_MIC_BUFFER_MS (int)
    ///   PAICOM_OWW_FUZZY_MATCH_CONFIDENCE (float)
    /// </summary>
    public static OpenWakeWordSettings FromEnvironmentVariables()
    {
        var defaults = CreateDefault();

        var threshold = ParseEnvVar("PAICOM_OWW_THRESHOLD", 
            v => float.TryParse(v, out var f) ? f : (float?)null) 
            ?? defaults.ConfidenceThreshold;
        
        var lockMs = ParseEnvVar("PAICOM_OWW_LOCK_MS",
            v => int.TryParse(v, out var i) ? i : (int?)null)
            ?? defaults.LockDurationMs;
        
        var chunkSize = ParseEnvVar("PAICOM_OWW_AUDIO_CHUNK_SIZE",
            v => int.TryParse(v, out var i) ? i : (int?)null)
            ?? defaults.AudioChunkSize;
        
        var threadScale = ParseEnvVar("PAICOM_OWW_INFERENCE_THREAD_SCALE",
            v => float.TryParse(v, out var f) ? f : (float?)null)
            ?? defaults.InferenceThreadPoolScale;
        
        var model = Environment.GetEnvironmentVariable("PAICOM_OWW_MODEL_RESOURCE")
            ?? defaults.ModelResourceName;
        
        var sampleRate = ParseEnvVar("PAICOM_OWW_AUDIO_SAMPLE_RATE",
            v => int.TryParse(v, out var i) ? i : (int?)null)
            ?? defaults.AudioSampleRate;
        
        var verbose = ParseEnvVar("PAICOM_OWW_VERBOSE_LOG",
            v => v switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                _ => (bool?)null
            })
            ?? defaults.EnableVerboseLogging;

        var micBufferMs = ParseEnvVar("PAICOM_OWW_MIC_BUFFER_MS",
            v => int.TryParse(v, out var i) ? i : (int?)null)
            ?? defaults.MicrophoneBufferMilliseconds;

        var fuzzyConfidence = ParseEnvVar("PAICOM_OWW_FUZZY_MATCH_CONFIDENCE",
            v => float.TryParse(v, out var f) ? f : (float?)null)
            ?? defaults.FuzzyMatchMinConfidence;

        return new(threshold, lockMs, chunkSize, threadScale, model, sampleRate, verbose, micBufferMs, fuzzyConfidence);
    }

    /// <summary>Create builder for fluent configuration.</summary>
    public static OpenWakeWordSettingsBuilder CreateBuilder() =>
        new();

    /// <summary>Helper: parse environment variable with fallback.</summary>
    private static T? ParseEnvVar<T>(string varName, Func<string, T?> parser) where T : struct
    {
        var value = Environment.GetEnvironmentVariable(varName);
        return string.IsNullOrWhiteSpace(value) ? null : parser(value);
    }

    /// <summary>User-friendly string representation.</summary>
    public override string ToString() =>
        "OpenWakeWordSettings {\n" +
        $"  ConfidenceThreshold={ConfidenceThreshold:F3},\n" +
        $"  LockDurationMs={LockDurationMs},\n" +
        $"  AudioChunkSize={AudioChunkSize},\n" +
        $"  InferenceThreadPoolScale={InferenceThreadPoolScale:F2},\n" +
        $"  ModelResourceName=\"{ModelResourceName}\",\n" +
        $"  AudioSampleRate={AudioSampleRate},\n" +
        $"  EnableVerboseLogging={EnableVerboseLogging},\n" +
        $"  MicrophoneBufferMilliseconds={MicrophoneBufferMilliseconds},\n" +
        $"  FuzzyMatchMinConfidence={FuzzyMatchMinConfidence:F2}\n" +
        "}";
}
