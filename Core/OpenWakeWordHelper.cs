using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;

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
    
    // Audio queue specifically for Vosk during lock window
    private static readonly Queue<float[]> _voskLockAudioQueue = new();
    private static readonly object _voskLockQueueLock = new();
    private static int _voskLockAudioQueuedCount;
    private static int _voskLockAudioDequeuedCount;
    private static int _enqueueAudioCallCount;  // Track total EnqueueAudio calls to diagnose if it's being called during lock
    
    private static int _unsupportedAudioArgLogCount;
    private static int _enqueueDropCount;
    private static int _enqueueSuccessCount;
    private static bool _voskInitAttempted;
    private static bool _voskListening;

    private const float SilenceAmplitudeThreshold = 0.01f;

    private static readonly Lazy<IReadOnlyList<string>> KnownCommands = new(LoadKnownCommands, true);

    private static readonly Dictionary<string, string> CommandResponses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["open the browser"] = "Opening the browser.",
        ["open browser"] = "Opening the browser.",
        ["launch browser"] = "Opening the browser.",
        ["open the web"] = "Opening the browser.",
        ["pause the music"] = "Pausing the music.",
        ["resume the music"] = "Resuming the music.",
        ["play the next song"] = "Playing the next song.",
        ["play the previous song"] = "Playing the previous song.",
        ["play the previous song on spotify"] = "Playing the previous song on Spotify."
    };

    private static readonly string[] WakeWordPrefixes =
    {
        "hey paicom",
        "hey pie com",
        "hey p a i com",
        "paicom"
    };

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
                // Report architecture diagnostics at startup
                ReportStartupDiagnostics();

                // Load settings from environment or use embedded defaults
                var embeddedSettings = OpenWakeWordSettings.CreateDefault();
                _settings = OpenWakeWordSettings.FromEnvironmentVariables();

                var migrationMode = Environment.GetEnvironmentVariable("PAICOM_MIGRATION_MODE") ?? "stable";
                var processBitness = Environment.Is64BitProcess ? "64" : "32";
                LogEvent($"arch.process_bitness={processBitness}");
                if ((string.Equals(migrationMode, "probe", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(migrationMode, "full", StringComparison.OrdinalIgnoreCase)) &&
                    !Environment.Is64BitProcess)
                {
                    LogEvent("reason.code=PROBE_STILL_32BIT");
                }
                
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
                            try
                            {
                                // ALWAYS enqueue audio - EnqueueAudio() will route it appropriately
                                // (to Vosk if locked for speech recognition, to OWW if not locked)
                                EnqueueAudio(chunk);
                            }
                            catch (Exception ex)
                            {
                                LogEvent($"[vosk-ERROR] Exception in microphone callback: {ex.GetType().Name}: {ex.Message}");
                            }
                        },
                        LogEvent,
                        _settings?.MicrophoneBufferMilliseconds ?? 200);

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
                
                // Emit specific reason codes for common failure modes
                if (ex is FileNotFoundException || ex.Message.Contains("not found"))
                {
                    LogEvent("reason.code=PROBE_RESOURCE_NOT_FOUND");
                }
                else if (ex is InvalidOperationException && ex.Message.Contains("ONNX"))
                {
                    LogEvent("reason.code=PROBE_ONNX_INIT_FAILED");
                    if (ex.InnerException != null)
                    {
                        LogEvent($"reason.detail={ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    }
                }
                else if (ex is PlatformNotSupportedException)
                {
                    LogEvent("reason.code=PROBE_PLATFORM_NOT_SUPPORTED");
                }
            }
        }
    }

    /// <summary>
    /// Enqueue audio chunk for inference.
    /// Called via IL injection at audio event sites.
    /// 
    /// Returns immediately (non-blocking):
    /// - If not initialized: logs warning and returns
    /// - If locked: queues audio to Vosk lock buffer for speech recognition
    /// - Otherwise: enqueues audio for OWW wake word inference
    /// 
    /// Thread-safe: safe to call from audio callbacks.
    /// </summary>
    public static void EnqueueAudio(object audioArg)
    {
        _enqueueAudioCallCount++;
        
        // Log EVERY call for debugging
        if (_enqueueAudioCallCount <= 10 || _enqueueAudioCallCount % 100 == 0)
        {
            bool isLocked = _lockManager?.IsLocked == true;
            LogEvent($"[vosk-diag] EnqueueAudio CALLED #{_enqueueAudioCallCount}, locked={isLocked}, argType={audioArg?.GetType().Name ?? "null"}");
        }
        
        // Log every N calls to track if this is even being called
        if (_enqueueAudioCallCount == 1 || _enqueueAudioCallCount % 100 == 0)
        {
            bool isLocked = _lockManager?.IsLocked == true;
            LogEvent($"[vosk-diag] EnqueueAudio call #{_enqueueAudioCallCount}, locked={isLocked}, voskQueued={_voskLockAudioQueuedCount}");
        }
        
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

            // During lock window, queue for Vosk speech recognition instead of OWW
            if (_lockManager?.IsLocked == true)
            {
                lock (_voskLockQueueLock)
                {
                    _voskLockAudioQueue.Enqueue(floatChunk);
                    _voskLockAudioQueuedCount++;
                    if (_voskLockAudioQueuedCount <= 3 || _voskLockAudioQueuedCount % 10 == 0)
                    {
                        LogEvent($"[vosk-audio-queue] Queued audio chunk #{_voskLockAudioQueuedCount} during lock window (len={floatChunk.Length}, queueSize={_voskLockAudioQueue.Count})");
                    }
                }
                return;
            }

            // Enqueue for OWW background inference
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
    /// Report architecture and runtime diagnostics at process initialization.
    /// Logs information for later analysis and fallback decision-making.
    /// </summary>
    private static void ReportStartupDiagnostics()
    {
        try
        {
            var processBitness = Environment.Is64BitProcess ? "x64" : "x86";
            var migrationMode = Environment.GetEnvironmentVariable("PAICOM_MIGRATION_MODE") ?? "stable";
            var verifiedRuntime = Environment.GetEnvironmentVariable("PAICOM_RUNTIME_VERIFIED_64BIT") ?? "unknown";
            var winePrefix = Environment.GetEnvironmentVariable("WINEPREFIX") ?? "<not-set>";

            LogEvent($"[startup-diag] process.bitness={processBitness}");
            LogEvent($"[startup-diag] migration.mode={migrationMode}");
            LogEvent($"[startup-diag] launcher.verified_64bit={verifiedRuntime}");
            LogEvent($"[startup-diag] wine.prefix={winePrefix}");
            LogEvent($"[startup-diag] processor_count={Environment.ProcessorCount}");

            // Check for architecture mismatch conditions
            if (string.Equals(migrationMode, "probe", StringComparison.OrdinalIgnoreCase) && processBitness == "x86")
            {
                LogEvent("[startup-diag] reason.code=PROBE_MODE_BUT_RUNNING_32BIT");
            }

            if (string.Equals(verifiedRuntime, "1") && processBitness == "x86")
            {
                LogEvent("[startup-diag] reason.code=LAUNCHER_VERIFIED_64BIT_BUT_RUNNING_32BIT");
            }

            // Report Vosk bridge status
            var voskEnabled = !Environment.Is64BitProcess ? "disabled:32bit_process" : "enabled";
            LogEvent($"[startup-diag] vosk.bridge_status={voskEnabled}");
        }
        catch (Exception ex)
        {
            LogEvent($"[startup-diag-error] {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// When wake word is detected, start speech recognition via Vosk.
    /// Runs in background thread to feed audio from lock queue to recognizer.
    /// </summary>
    public static void TriggerSpeechRecognitionOnWake()
    {
        LogEvent("Wake-word lock issued; starting Vosk speech recognition...");
        EnsureMicrophoneCaptureStarted("wake-handoff", forceRestart: false);
        
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

        if (_voskListening)
        {
            LogEvent("Vosk speech recognition already active for current lock window");
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
            var settings = _settings ?? OpenWakeWordSettings.CreateDefault();
            var wakeUtc = DateTime.UtcNow;
            var stopTime = wakeUtc.AddMilliseconds(settings.LockDurationMs + 500);
            var graceDeadline = wakeUtc.AddMilliseconds(settings.PostWakeSilenceGraceMilliseconds);
            var silenceCutoffMs = settings.SpeechSilenceCutoffMilliseconds;
            var silenceStartUtc = (DateTime?)null;
            var attemptedMicRecovery = false;
            var loggedQueueEmptyOnce = false;
            var firstAudioGraceDeadline = wakeUtc.AddMilliseconds(Math.Max(700, settings.PostWakeSilenceGraceMilliseconds));
            var lockQueuedAtStart = _voskLockAudioQueuedCount;
            var accumulatedAudio = new List<float>();
            var dequeuedChunks = 0;
            var hasSpeechAudio = false;

            LogEvent($"Vosk speech recognition started. Silence grace={settings.PostWakeSilenceGraceMilliseconds}ms, cutoff={silenceCutoffMs}ms.");
            LogEvent($"[vosk-audio-queue] Accumulating until silence exceeds cutoff or lock window ends ({settings.LockDurationMs}ms lock + buffer)");

            while (_voskListening && DateTime.UtcNow < stopTime)
            {
                float[]? chunk = null;

                lock (_voskLockQueueLock)
                {
                    if (_voskLockAudioQueue.Count > 0)
                    {
                        chunk = _voskLockAudioQueue.Dequeue();
                        _voskLockAudioDequeuedCount++;
                        dequeuedChunks++;
                        if (_voskLockAudioDequeuedCount <= 3 || _voskLockAudioDequeuedCount % 10 == 0)
                        {
                            LogEvent($"[vosk-audio-queue] Dequeued chunk #{_voskLockAudioDequeuedCount} (len={chunk?.Length}, remaining={_voskLockAudioQueue.Count})");
                        }
                    }
                    else if (!loggedQueueEmptyOnce && dequeuedChunks == 0 && _voskLockAudioQueuedCount == 0)
                    {
                        loggedQueueEmptyOnce = true;
                        LogEvent($"[vosk-audio-queue] Queue empty: queued={_voskLockAudioQueuedCount}, dequeued={_voskLockAudioDequeuedCount}");
                    }
                }

                if (chunk != null && chunk.Length > 0)
                {
                    var chunkIsSilent = IsSilentChunk(chunk);
                    if (!chunkIsSilent)
                    {
                        hasSpeechAudio = true;
                        silenceStartUtc = null;
                        firstAudioGraceDeadline = DateTime.UtcNow.AddMilliseconds(settings.PostWakeSilenceGraceMilliseconds);
                        loggedQueueEmptyOnce = false;
                        accumulatedAudio.AddRange(chunk);
                    }
                    else if (DateTime.UtcNow >= graceDeadline)
                    {
                        accumulatedAudio.AddRange(chunk);
                        silenceStartUtc ??= DateTime.UtcNow;
                    }

                    if (hasSpeechAudio && silenceStartUtc.HasValue && DateTime.UtcNow >= silenceStartUtc.Value.AddMilliseconds(silenceCutoffMs))
                    {
                        LogEvent($"[vosk-speech] Silence cutoff reached after {silenceCutoffMs}ms; finalizing speech capture.");
                        break;
                    }
                }
                else
                {
                    var queuedDelta = _voskLockAudioQueuedCount - lockQueuedAtStart;
                    if (!attemptedMicRecovery && DateTime.UtcNow >= firstAudioGraceDeadline && queuedDelta == 0)
                    {
                        attemptedMicRecovery = true;
                        var callbacks = _microphoneCapture?.DataAvailableEventCount ?? 0;
                        var lastCallbackUtc = _microphoneCapture?.LastDataAvailableUtc;
                        var lastCallback = lastCallbackUtc.HasValue ? lastCallbackUtc.Value.ToString("O") : "none";
                        LogEvent($"[vosk-audio-queue] No lock-window audio queued after wake (callbacks={callbacks}, lastCallback={lastCallback}); attempting mic capture restart");
                        EnsureMicrophoneCaptureStarted("lock-window-recovery", forceRestart: true);
                    }

                    if (DateTime.UtcNow >= graceDeadline)
                    {
                        silenceStartUtc ??= DateTime.UtcNow;
                        if (DateTime.UtcNow >= silenceStartUtc.Value.AddMilliseconds(silenceCutoffMs))
                        {
                            LogEvent($"[vosk-speech] Silence cutoff reached after {silenceCutoffMs}ms without additional speech; finalizing speech capture.");
                            break;
                        }
                    }

                    System.Threading.Thread.Sleep(50);
                }
            }

            LogEvent($"Speech window ended. Processing {accumulatedAudio.Count} accumulated audio samples.");

            if (accumulatedAudio.Count > 0)
            {
                // Convert ALL accumulated float audio to PCM bytes for Vosk
                byte[] pcmData = new byte[accumulatedAudio.Count * 2];
                for (int i = 0; i < accumulatedAudio.Count; i++)
                {
                    short sample = (short)(accumulatedAudio[i] * 32768f);
                    BitConverter.GetBytes(sample).CopyTo(pcmData, i * 2);
                }

                var result = _voskRecognizer.ProcessAudioChunk(pcmData);
                if (!string.IsNullOrEmpty(result))
                {
                    LogEvent($"Vosk result: {result}");
                    HandleRecognizedSpeech(result);
                }
            }

            var finalResult = _voskRecognizer.GetFinalResult();
            if (!string.IsNullOrEmpty(finalResult))
            {
                LogEvent($"Vosk final result: {finalResult}");
                HandleRecognizedSpeech(finalResult);
            }

            LogEvent($"Vosk speech recognition completed (processed {dequeuedChunks} audio chunks, total accumulated samples: {accumulatedAudio.Count}, queued={_voskLockAudioQueuedCount}, dequeued={_voskLockAudioDequeuedCount})");
        }
        catch (Exception ex)
        {
            LogEvent($"Exception in Vosk recognition: {ex.Message}");
        }
        finally
        {
            lock (_voskLockQueueLock)
            {
                _voskLockAudioQueue.Clear();
            }

            _lockManager?.Reset();
            _voskListening = false;
        }
    }

    public static string? HandleRecognizedSpeech(string? rawResult)
    {
        var transcript = ExtractRecognizedText(rawResult);
        if (string.IsNullOrWhiteSpace(transcript))
            return null;

        LogEvent($"[vosk-speech] Transcript: {transcript}");

        var response = ResolveCommandResponse(transcript);
        if (!string.IsNullOrWhiteSpace(response))
        {
            LogEvent($"[oww-command] Assistant line: {response}");
        }

        return response;
    }

    public static string? ResolveCommandResponse(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return null;

        var commandText = NormalizeCommandText(transcript!);
        if (string.IsNullOrWhiteSpace(commandText))
            return null;

        var knownCommands = KnownCommands.Value;
        if (knownCommands.Count == 0)
            return null;

        var match = FuzzyMatcher.FindClosestMatch(
            commandText,
            knownCommands,
            _settings?.FuzzyMatchMinConfidence ?? 0.80f,
            LogEvent);

        if (match == null)
            return null;

        var matchedCommand = match.MatchedCommand;
        if (string.IsNullOrWhiteSpace(matchedCommand))
            return null;

        LogEvent($"[oww-command] command='{matchedCommand}', confidence={match.Confidence:P1}");

        return CommandResponses.TryGetValue(matchedCommand, out var response)
            ? response
            : null;
    }

    private static IReadOnlyList<string> LoadKnownCommands()
    {
        var manifestPath = FindCommandManifestPath();
        if (manifestPath == null)
        {
            LogEvent("[oww-command] Command manifest not found; fuzzy matching limited to built-in responses.");
            return Array.Empty<string>();
        }

        try
        {
            var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in System.IO.File.ReadLines(manifestPath))
            {
                if (ParseCommandLine(line) is string commandText)
                    commands.Add(commandText);
            }

            LogEvent($"[oww-command] Loaded {commands.Count} commands from {manifestPath}");
            return commands.ToArray();
        }
        catch (Exception ex)
        {
            LogEvent($"[oww-command] Failed to load command manifest '{manifestPath}': {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static string? ParseCommandLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            return null;

        var commandText = trimmed;
        var scriptDelimiter = commandText.LastIndexOf(" (", StringComparison.Ordinal);
        if (scriptDelimiter > 0 && commandText.EndsWith(")", StringComparison.Ordinal))
            commandText = commandText.Substring(0, scriptDelimiter);

        return NormalizeCommandText(commandText!);
    }

    private static string? FindCommandManifestPath()
    {
        var baseDirectories = new[]
        {
            AppDomain.CurrentDomain.BaseDirectory,
            System.IO.Directory.GetCurrentDirectory()
        };

        foreach (var root in baseDirectories)
        {
            var current = new System.IO.DirectoryInfo(root!);
            while (true)
            {
                var fullName = current.FullName;
                var directPath = System.IO.Path.Combine(fullName, "custom-commands", "commands.txt");
                if (System.IO.File.Exists(directPath))
                    return directPath;

                var legacyPath = System.IO.Path.Combine(fullName, "PAIcom_Player_Folder", "custom-commands", "commands.txt");
                if (System.IO.File.Exists(legacyPath))
                    return legacyPath;

                var parent = current.Parent;
                if (parent == null)
                    break;

                current = parent;
            }
        }

        return null;
    }

    private static string? ExtractRecognizedText(string? rawResult)
    {
        if (string.IsNullOrWhiteSpace(rawResult))
            return null;

        var trimmed = rawResult.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            return trimmed;

        const string key = "\"text\"";
        var keyIndex = trimmed.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
            return trimmed;

        var colonIndex = trimmed.IndexOf(':', keyIndex);
        if (colonIndex < 0)
            return null;

        var start = colonIndex + 1;
        while (start < trimmed.Length && char.IsWhiteSpace(trimmed[start]))
            start++;

        if (start >= trimmed.Length)
            return null;

        if (trimmed[start] == '"')
            start++;

        var builder = new StringBuilder();
        var escaping = false;
        for (var i = start; i < trimmed.Length; i++)
        {
            var current = trimmed[i];
            if (escaping)
            {
                builder.Append(current switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => current
                });
                escaping = false;
                continue;
            }

            if (current == '\\')
            {
                escaping = true;
                continue;
            }

            if (current == '"' || current == '}')
                break;

            builder.Append(current);
        }

        return builder.ToString().Trim();
    }

    private static string NormalizeCommandText(string transcript)
    {
        var normalized = transcript.Trim();
        foreach (var prefix in WakeWordPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(prefix.Length).Trim();
                break;
            }
        }

        return normalized.TrimStart(',', '.', '!', '?', ':', ';');
    }

    private static bool IsSilentChunk(float[] chunk)
    {
        if (chunk.Length == 0)
            return true;

        double sumAbs = 0.0;
        for (var i = 0; i < chunk.Length; i++)
            sumAbs += Math.Abs(chunk[i]);

        var meanAbs = sumAbs / chunk.Length;
        return meanAbs < SilenceAmplitudeThreshold;
    }

    private static void EnsureMicrophoneCaptureStarted(string reason, bool forceRestart)
    {
        lock (_initLock)
        {
            try
            {
                if (_microphoneCapture == null)
                {
                    _microphoneCapture = new OpenWakeWordMicrophoneCapture(
                        chunk => EnqueueAudio(chunk),
                        LogEvent);
                }

                if (forceRestart && _microphoneCapture.IsStarted)
                {
                    LogEvent($"[oww-mic-capture] Restarting direct microphone capture ({reason})");
                    _microphoneCapture.Stop();
                    System.Threading.Thread.Sleep(25);
                }

                if (_microphoneCapture.Start())
                {
                    LogEvent($"[oww-mic-capture] Direct microphone capture ready ({reason})");
                }
                else
                {
                    LogEvent($"[oww-mic-capture] Direct microphone capture unavailable ({reason})");
                }
            }
            catch (Exception ex)
            {
                LogEvent($"[oww-mic-capture] Failed to ensure microphone capture ({reason}): {ex.GetType().Name}: {ex.Message}");
            }
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
