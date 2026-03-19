using System;
using System.Diagnostics;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Runtime helper methods injected into patched PAIcom.exe.
/// 
/// These static methods are called via IL injection at audio event sites.
/// They manage OpenWakeWord initialization, audio queueing, and logging.
/// 
/// Methods in this class are injected into the target assembly via dnlib,
/// so they execute in the context of the patched .exe at runtime.
/// </summary>
public static class OpenWakeWordHelper
{
    // Lazy-loaded state (initialized once at runtime)
    private static bool _initialized;
    private static Exception? _initError;
    private static OpenWakeWordSettings? _settings;
    private static AudioLockManager? _lockManager;
    private static OpenWakeWordWrapperModel? _model;
    private static OpenWakeWordInferenceWorker? _worker;
    private static readonly object _initLock = new();

    /// <summary>
    /// Initialize OpenWakeWord runtime system.
    /// Called once by injected code before first audio processing.
    /// 
    /// Loads settings from environment variables, creates lock manager,
    /// loads ONNX model, starts background inference worker.
    /// 
    /// Thread-safe: uses double-checked locking.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
            return;

        lock (_initLock)
        {
            if (_initialized)
                return;

            try
            {
                // Load settings from environment or use embedded defaults
                var embeddedSettings = OpenWakeWordSettings.CreateDefault();
                _settings = OpenWakeWordSettings.FromEnvironmentVariables();
                
                LogEvent($"Initialized with settings: {_settings}");

                // Create lock manager (hard lock = 3000ms)
                _lockManager = new AudioLockManager(_settings.LockDurationMs);

                // Load ONNX model from embedded resources
                _model = OpenWakeWordWrapperModel.GetOrCreateSession(
                    _settings.ModelResourceName,
                    LogEvent);

                // Create and start background inference worker
                _worker = new OpenWakeWordInferenceWorker(
                    _settings,
                    _lockManager,
                    _model,
                    (detected, confidence) =>
                    {
                        if (detected)
                        {
                            LogEvent($"Wake word detected! Confidence: {confidence:F3}");
                        }
                        else if (_settings.EnableVerboseLogging)
                        {
                            LogEvent($"Audio processed, confidence: {confidence:F3}");
                        }
                    },
                    LogEvent);

                _worker.StartProcessing();
                LogEvent("OpenWakeWord inference worker started");
                
                _initialized = true;
            }
            catch (Exception ex)
            {
                _initError = ex;
                LogEvent($"OpenWakeWord initialization failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Enqueue audio chunk for inference.
    /// Called via IL injection at audio event sites.
    /// 
    /// Returns immediately (non-blocking):
    /// - If not initialized: logs warning and returns
    /// - If locked: skips audio (prevents back-queuing)
    /// - Otherwise: enqueues audio for background inference
    /// 
    /// Thread-safe: safe to call from audio callbacks.
    /// </summary>
    public static void EnqueueAudio(object audioArg)
    {
        if (!_initialized)
            Initialize();

        if (_initError != null)
            return; // Init failed, graceful degradation

        if (_lockManager?.IsLocked == true)
            return; // Hard lock active, skip audio

        float[]? floatChunk = null;

        if (audioArg is float[] fArr)
        {
            floatChunk = fArr;
        }
        else if (audioArg is byte[] bArr)
        {
            // Convert 16-bit PCM bytes to floats (-1.0 to 1.0)
            floatChunk = new float[bArr.Length / 2];
            for (int i = 0; i < floatChunk.Length; i++)
            {
                short val = BitConverter.ToInt16(bArr, i * 2);
                floatChunk[i] = val / 32768.0f;
            }
        }
        else if (audioArg is short[] sArr)
        {
            floatChunk = new float[sArr.Length];
            for (int i = 0; i < floatChunk.Length; i++)
                floatChunk[i] = sArr[i] / 32768.0f;
        }

        if (floatChunk != null && floatChunk.Length > 0)
        {
            // Enqueue for background inference
            _worker?.EnqueueAudio(floatChunk);
        }
    }

    /// <summary>
    /// Log OWW event.
    /// Writes to console and launcher-runtime.log with [oww] prefix.
    /// Thread-safe.
    /// </summary>
    public static void LogEvent(string message)
    {
        var prefix = "[oww]";
        var fullMessage = $"{prefix} {message}";
        
        try
        {
            Console.WriteLine(fullMessage);
            Debug.WriteLine(fullMessage);
            
            // Attempt to log to launcher-runtime.log if available
            try
            {
                var logPath = "launcher-runtime.log";
                System.IO.File.AppendAllText(logPath, $"{DateTime.UtcNow:O} {fullMessage}\n");
            }
            catch
            {
                // Log file unavailable, ignore
            }
        }
        catch
        {
            // Logging failed, silently degrade
        }
    }

    /// <summary>
    /// Graceful shutdown (called on app exit or cleanup).
    /// Stops background inference, flushes queues.
    /// </summary>
    public static void Shutdown()
    {
        lock (_initLock)
        {
            _worker?.StopProcessing();
            _worker?.Dispose();
            _model?.Dispose();
            _lockManager?.Dispose();
            
            _initialized = false;
            _worker = null;
            _model = null;
            _lockManager = null;
            _settings = null;
            _initError = null;
        }
    }

    /// <summary>Internal: get current lock state (for testing).</summary>
    internal static bool IsLocked => _lockManager?.IsLocked ?? false;

    /// <summary>Internal: get initialization error (for diagnostics).</summary>
    internal static Exception? InitializationError => _initError;
}
