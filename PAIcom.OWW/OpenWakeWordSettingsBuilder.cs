namespace CrossPlatformPatcher.Core;

/// <summary>
/// Fluent builder for OpenWakeWordSettings.
///
/// Example:
///   var settings = OpenWakeWordSettings.CreateBuilder()
///       .WithThreshold(0.65f)
///       .WithLockDurationMs(2500)
///       .WithVerboseLogging(true)
///       .Build();
/// </summary>
public sealed class OpenWakeWordSettingsBuilder
{
    private float _threshold = 0.7f;
    private int _lockMs = 5000;
    private int _chunkSize = 1024;
    private float _threadScale = 1.0f;
    private string _modelResource = "oww.model.hey_pie_com.quant.onnx";
    private int _sampleRate = 16000;
    private bool _verboseLog;
    private int _micBufferMs = 200;
    private float _fuzzyMatchConfidence = 0.80f;

    /// <summary>Set confidence threshold [0.0, 1.0].</summary>
    public OpenWakeWordSettingsBuilder WithThreshold(float value)
    {
        _threshold = value;
        return this;
    }

    /// <summary>Set hard lock duration in milliseconds.</summary>
    public OpenWakeWordSettingsBuilder WithLockDurationMs(int milliseconds)
    {
        _lockMs = milliseconds;
        return this;
    }

    /// <summary>Set audio chunk size in samples.</summary>
    public OpenWakeWordSettingsBuilder WithAudioChunkSize(int samples)
    {
        _chunkSize = samples;
        return this;
    }

    /// <summary>Set ThreadPool inference scaling [0.5, 2.0].</summary>
    public OpenWakeWordSettingsBuilder WithInferenceThreadScale(float scale)
    {
        _threadScale = scale;
        return this;
    }

    /// <summary>Set ONNX model resource name.</summary>
    public OpenWakeWordSettingsBuilder WithModelResourceName(string resourceName)
    {
        _modelResource = resourceName;
        return this;
    }

    /// <summary>Set audio sample rate in Hz.</summary>
    public OpenWakeWordSettingsBuilder WithAudioSampleRate(int hz)
    {
        _sampleRate = hz;
        return this;
    }

    /// <summary>Enable or disable verbose logging.</summary>
    public OpenWakeWordSettingsBuilder WithVerboseLogging(bool enabled)
    {
        _verboseLog = enabled;
        return this;
    }

    /// <summary>Set microphone buffer size in milliseconds [20, 1000].</summary>
    public OpenWakeWordSettingsBuilder WithMicrophoneBufferMilliseconds(int milliseconds)
    {
        _micBufferMs = milliseconds;
        return this;
    }

    /// <summary>Set fuzzy matching minimum confidence [0.0, 1.0].</summary>
    public OpenWakeWordSettingsBuilder WithFuzzyMatchMinConfidence(float confidence)
    {
        _fuzzyMatchConfidence = confidence;
        return this;
    }

    /// <summary>Build immutable settings instance. Throws if any value is invalid.</summary>
    public OpenWakeWordSettings Build() =>
        new(_threshold, _lockMs, _chunkSize, _threadScale, _modelResource, _sampleRate, _verboseLog, _micBufferMs, _fuzzyMatchConfidence);
}
