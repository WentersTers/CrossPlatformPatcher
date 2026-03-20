using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

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
    private static OpenWakeWordFeaturePipeline? _featurePipeline;
    private static OpenWakeWordWrapperModel? _model;
    private static OpenWakeWordInferenceWorker? _worker;
    private static OpenWakeWordMicrophoneCapture? _microphoneCapture;
    private static VoskSpeechRecognizer? _voskRecognizer;
    private static readonly object _initLock = new();
    private static int _unsupportedAudioArgLogCount;
    private static int _enqueueDropCount;
    private static int _enqueueSuccessCount;
    private static bool _voskInitAttempted;
    private static bool _voskListening;

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

                // Build the OpenWakeWord preprocessing pipeline
                _featurePipeline = new OpenWakeWordFeaturePipeline(LogEvent);

                // Load ONNX model from embedded resources
                _model = OpenWakeWordWrapperModel.GetOrCreateSession(
                    _settings.ModelResourceName,
                    LogEvent);

                // Create and start background inference worker
                _worker = new OpenWakeWordInferenceWorker(
                    _settings,
                    _lockManager,
                    _featurePipeline,
                    _model,
                    (detected, confidence) =>
                    {
                        if (detected)
                        {
                            LogEvent($"Wake word detected! Confidence: {confidence:F3}");
                            LogEvent($"Wake->speech handoff: lock window opened for {_settings.LockDurationMs}ms; expecting speech/command events next");
                            // Trigger app's speech recognition handler
                            TriggerSpeechRecognitionOnWake();
                        }
                        else if (_settings.EnableVerboseLogging)
                        {
                            LogEvent($"Audio processed, confidence: {confidence:F3}");
                        }
                    },
                    LogEvent);

                _worker.StartProcessing();
                LogEvent("OpenWakeWord inference worker started");

                // Start direct microphone capture fallback so OWW does not rely solely
                // on obfuscated in-app audio callback hooks.
                try
                {
                    _microphoneCapture = new OpenWakeWordMicrophoneCapture(
                        chunk =>
                        {
                            if (_lockManager?.IsLocked != true)
                                _worker.EnqueueAudio(chunk);
                        },
                        LogEvent);

                    if (!_microphoneCapture.Start())
                        LogEvent("Direct microphone capture unavailable; relying on injected callbacks");
                }
                catch (Exception micEx)
                {
                    _microphoneCapture = null;
                    LogEvent($"Direct microphone capture init failed (non-fatal): {micEx.GetType().Name}: {micEx.Message}");
                }
                
                _initialized = true;
            }
            catch (Exception ex)
            {
                _initError = ex;
                LogEvent($"OpenWakeWord initialization failed: {ex}");
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
        {
            if (_unsupportedAudioArgLogCount < 3)
            {
                _unsupportedAudioArgLogCount++;
                LogEvent($"Enqueue skipped due to init error: {_initError.GetType().Name}: {_initError.Message}");
            }
            return; // Init failed, graceful degradation
        }

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
            if (floatChunk.Length > 65536)
            {
                if (_enqueueDropCount <= 5 || _enqueueDropCount % 25 == 0)
                {
                    LogEvent($"Skipping oversized audio chunk len={floatChunk.Length}; likely non-stream callback payload");
                }
                _enqueueDropCount++;
                return;
            }

            // Enqueue for background inference
            var added = _worker?.EnqueueAudio(floatChunk) ?? false;
            if (added)
            {
                _enqueueSuccessCount++;
                if (_enqueueSuccessCount == 1 || _enqueueSuccessCount % 200 == 0)
                {
                    LogEvent($"Audio enqueue ok: chunks={_enqueueSuccessCount}, lastLen={floatChunk.Length}");
                }
            }
            else
            {
                _enqueueDropCount++;
                if (_enqueueDropCount <= 5 || _enqueueDropCount % 50 == 0)
                {
                    LogEvent($"Audio enqueue dropped: drops={_enqueueDropCount}, len={floatChunk.Length}");
                }
            }
        }
        else
        {
            if (_unsupportedAudioArgLogCount < 8)
            {
                _unsupportedAudioArgLogCount++;
                var argType = audioArg?.GetType().FullName ?? "<null>";
                LogEvent($"Unsupported audio argument type for OWW enqueue: {argType}");
            }
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
    /// When wake word is detected, start speech recognition via Vosk.
    /// Runs in background thread to feed audio from lock queue to recognizer.
    /// </summary>
    public static void TriggerSpeechRecognitionOnWake()
    {
        LogEvent("Wake-word lock issued; starting Vosk speech recognition...");
        
        // Initialize Vosk if not already attempted
        if (!_voskInitAttempted)
        {
            _voskInitAttempted = true;
            try
            {
                _voskRecognizer = new VoskSpeechRecognizer(LogEvent);
                if (!_voskRecognizer.Initialize())
                {
                    LogEvent("Vosk initialization failed; speech recognition unavailable");
                    _voskRecognizer?.Dispose();
                    _voskRecognizer = null;
                }
            }
            catch (Exception ex)
            {
                LogEvent($"Exception initializing Vosk: {ex.Message}");
                _voskRecognizer?.Dispose();
                _voskRecognizer = null;
            }
        }

        if (_voskRecognizer == null)
        {
            LogEvent("Vosk recognizer not available; speech recognition skipped");
            return;
        }

        // Start background task to process audio during lock window
        _voskListening = true;
        System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            ProcessSpeechRecognitionLocked();
        }, null);
    }

    /// <summary>
    /// Background task: feed queued audio to Vosk recognizer during lock window.
    /// Runs in ThreadPool thread.
    /// </summary>
    private static void ProcessSpeechRecognitionLocked()
    {
        if (_voskRecognizer == null || _lockManager == null)
            return;

        try
        {
            LogEvent("Vosk speech recognition started");
            int processedChunks = 0;
            int lockTimeoutMs = (_settings?.LockDurationMs ?? 3000) + 500; // Add buffer
            var stopTime = DateTime.UtcNow.AddMilliseconds(lockTimeoutMs);

            // Process audio while lock is active or timeout expires
            while (DateTime.UtcNow < stopTime && _voskListening)
            {
                var chunk = _lockManager.TryDequeueAudio();
                if (chunk != null && chunk.Length > 0)
                {
                    // Convert float array to PCM bytes for Vosk
                    byte[] pcmData = new byte[chunk.Length * 2];
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        short sample = (short)(chunk[i] * 32768f);
                        BitConverter.GetBytes(sample).CopyTo(pcmData, i * 2);
                    }

                    var result = _voskRecognizer.ProcessAudioChunk(pcmData);
                    if (!string.IsNullOrEmpty(result))
                    {
                        LogEvent($"Vosk result: {result}");
                    }

                    processedChunks++;
                }
                else
                {
                    // Small delay to avoid busy-waiting
                    System.Threading.Thread.Sleep(50);
                }
            }

            var finalResult = _voskRecognizer.GetFinalResult();
            if (!string.IsNullOrEmpty(finalResult))
                LogEvent($"Vosk final result: {finalResult}");

            LogEvent($"Vosk speech recognition completed (processed {processedChunks} audio chunks)");
        }
        catch (Exception ex)
        {
            LogEvent($"Exception in Vosk recognition: {ex.Message}");
        }
        finally
        {
            _voskListening = false;
        }
    }

    /// <summary>
    /// Graceful shutdown (called on app exit or cleanup).
    /// Stops background inference, flushes queues.
    /// </summary>
    public static void Shutdown()
    {
        _voskListening = false;
        lock (_initLock)
        {
            _worker?.StopProcessing();
            _worker?.Dispose();
            _microphoneCapture?.Dispose();
            _featurePipeline?.Dispose();
            _model?.Dispose();
            _lockManager?.Dispose();
            _voskRecognizer?.Dispose();
            
            _initialized = false;
            _worker = null;
            _microphoneCapture = null;
            _featurePipeline = null;
            _model = null;
            _lockManager = null;
            _settings = null;
            _initError = null;
            _voskRecognizer = null;
            _voskListening = false;
            _voskInitAttempted = false;
        }
    }

    /// <summary>Internal: get current lock state (for testing).</summary>
    internal static bool IsLocked => _lockManager?.IsLocked ?? false;

    /// <summary>Internal: get initialization error (for diagnostics).</summary>
    internal static Exception? InitializationError => _initError;
}
