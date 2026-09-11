using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

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
    private static int _voskLockAudioDroppedCount;
    private static int _enqueueAudioCallCount;  // Track total EnqueueAudio calls to diagnose if it's being called during lock
    
    private static int _unsupportedAudioArgLogCount;
    private static int _enqueueDropCount;
    private static int _enqueueSuccessCount;
    private static int _audioClipLogCount;
    private static int _pcmClipLogCount;
    private static bool _voskInitAttempted;
    private static bool _voskListening;
    private static long _wakeSequenceCounter;
    private static long _activeWakeSequenceId;
    private static long _firstQueuedAudioMarkerWakeId;

    private const float SilenceAmplitudeThreshold = 0.01f;
    private const int MaxVoskLockQueueChunks = 96;
    private const int DefaultFallbackScriptTimeoutMs = 8000;
    private static readonly Stopwatch TimingStopwatch = Stopwatch.StartNew();

    // Sequential method testing (for diagnostics and compatibility testing)
    private static bool _sequentialMethodTestMode;
    private static string? _testCategoryFilter;
    private static readonly object _methodTestLock = new();
    private static bool _patreonFormattingApplied;

    // File-based command input mode (read from input-command.txt)
    private static bool _fileCommandInputEnabled;
    private static string? _fileCommandInputPath;
    private static bool _fileCommandInputThreadStarted;
    private static readonly object _fileCommandInputLock = new();
    private static readonly object _exceptionHookLock = new();
    private static bool _exceptionHooksInstalled;
    private const int MaxExceptionStackLines = 12;

    private static readonly object _commandManifestLock = new();
    private static Lazy<IReadOnlyList<CommandManifestEntry>> KnownCommands = new(LoadKnownCommands, true);
    private static readonly object _dispatcherLock = new();
    private static readonly ICommandDispatcher[] CommandDispatchers =
        BuildCommandDispatcherPipeline();

    /// <summary>
    /// Builds the command dispatcher pipeline, optimizing for the current platform.
    /// On Unix systems (including Wine), ProcessFallback runs before Reflection to prefer native commands
    /// over trying to dispatch into the Wine-running game.
    /// </summary>
    private static ICommandDispatcher[] BuildCommandDispatcherPipeline()
    {
        var commonDispatchers = new ICommandDispatcher[]
        {
            new SpeechEmulationCommandDispatcher(),
            new UiSimulationCommandDispatcher(),
        };

        // Detect if we're running under Wine (Windows emulation on Unix)
        // Wine sets OSPlatform.Windows to true, so we need to check for it explicitly
        bool isWine = IsRunningUnderWine();
        bool isRealWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) && !isWine;

        if (isRealWindows)
        {
            // Real Windows: try game reflection first, then fall back to processes
            return commonDispatchers.Concat(new ICommandDispatcher[]
            {
                new ReflectionCommandDispatcher(),
                new ProcessFallbackCommandDispatcher()
            }).ToArray();
        }
        else
        {
            // Unix (macOS/Linux) or Wine on Unix: try native process commands BEFORE game reflection
            // This ensures commands like "show steam friends" or "open task manager"
            // execute natively instead of trying to call into the Wine-running game
            return commonDispatchers.Concat(new ICommandDispatcher[]
            {
                new ProcessFallbackCommandDispatcher(),
                new ReflectionCommandDispatcher(),
            }).ToArray();
        }
    }

    /// <summary>
    /// Detects if the application is running under Wine (Windows compatibility layer on Unix).
    /// </summary>
    private static bool IsRunningUnderWine()
    {
        try
        {
            if (TryParseBooleanEnvironmentVariable("PAICOM_RUNTIME_FORCE_WINE", out var forcedWine))
                return forcedWine;

            var hostOs = Environment.GetEnvironmentVariable("PAICOM_RUNTIME_HOST_OS");
            if (!string.IsNullOrWhiteSpace(hostOs))
            {
                if (string.Equals(hostOs, "windows", StringComparison.OrdinalIgnoreCase))
                    return false;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return true;
            }

            var winePrefix = Environment.GetEnvironmentVariable("WINEPREFIX");
            var wineLoader = Environment.GetEnvironmentVariable("WINELOADER");
            var wineDllPath = Environment.GetEnvironmentVariable("WINEDLLPATH");
            var wineVar = Environment.GetEnvironmentVariable("WINE");
            if (!string.IsNullOrWhiteSpace(winePrefix) ||
                !string.IsNullOrWhiteSpace(wineLoader) ||
                !string.IsNullOrWhiteSpace(wineDllPath) ||
                !string.IsNullOrWhiteSpace(wineVar))
                return true;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;

            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(systemRoot))
            {
                var wineSystemFile = Path.Combine(systemRoot, "system32", "wineboot.exe");
                if (File.Exists(wineSystemFile))
                    return true;
            }

            var currentDir = Directory.GetCurrentDirectory();
            if (currentDir.StartsWith("Z:\\", StringComparison.OrdinalIgnoreCase) ||
                currentDir.StartsWith("Z:/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseBooleanEnvironmentVariable(string name, out bool value)
    {
        value = false;
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                value = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                value = false;
                return true;
            default:
                return false;
        }
    }

    private static bool IsVerboseAudioLoggingEnabled()
    {
        if (_settings?.EnableVerboseLogging == true)
            return true;

        return TryParseBooleanEnvironmentVariable("PAICOM_OWW_VERBOSE_LOG", out var enabled) && enabled;
    }

    private static int GetFallbackScriptTimeoutMs()
    {
        var configured = Environment.GetEnvironmentVariable("PAICOM_FALLBACK_SCRIPT_TIMEOUT_MS");
        if (!string.IsNullOrWhiteSpace(configured) && int.TryParse(configured, out var parsed))
            return parsed < 1000 ? 1000 : (parsed > 120000 ? 120000 : parsed);

        return DefaultFallbackScriptTimeoutMs;
    }

    private static void LogTimingMarker(string marker, long wakeId, string? detail = null)
    {
        var prefix = $"[oww-timing] wake.id={wakeId} marker={marker} t.ms={TimingStopwatch.ElapsedMilliseconds}";
        if (string.IsNullOrWhiteSpace(detail))
        {
            LogEvent(prefix);
            return;
        }

        LogEvent($"{prefix} detail={detail}");
    }

    private static bool TryNormalizeCommandToken(string rawToken, out string normalizedToken, out string reason)
    {
        normalizedToken = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(rawToken))
        {
            reason = "token is empty";
            return false;
        }

        var token = rawToken.Trim();
        if (token.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            token = token.Substring(0, token.Length - 4);

        token = token.Trim();
        if (token.Length == 0)
        {
            reason = "token is empty after trim";
            return false;
        }

        if (Path.IsPathRooted(token))
        {
            reason = "absolute paths are not allowed";
            return false;
        }

        if (token.IndexOf("..", StringComparison.Ordinal) >= 0)
        {
            reason = "path traversal sequence '..' is not allowed";
            return false;
        }

        if (token.IndexOf('/') >= 0 || token.IndexOf('\\') >= 0)
        {
            reason = "path separators are not allowed";
            return false;
        }

        if (token.StartsWith("~", StringComparison.Ordinal))
        {
            reason = "home-directory shorthand is not allowed";
            return false;
        }

        if (token.IndexOfAny(new[] { ';', '&', '|', '`', '>', '<', '\r', '\n', '\t', '\0', ':' }) >= 0)
        {
            reason = "shell metacharacters are not allowed";
            return false;
        }

        normalizedToken = token;
        return true;
    }

    private static void SanitizeAudioChunk(float[] chunk, out int clippedSamples)
    {
        clippedSamples = 0;
        for (var i = 0; i < chunk.Length; i++)
        {
            var sample = chunk[i];
            if (float.IsNaN(sample) || float.IsInfinity(sample))
            {
                chunk[i] = 0f;
                clippedSamples++;
                continue;
            }

            if (sample > 1f)
            {
                chunk[i] = 1f;
                clippedSamples++;
            }
            else if (sample < -1f)
            {
                chunk[i] = -1f;
                clippedSamples++;
            }
        }
    }

    private static short FloatToPcm16(float sample, ref int clippedSamples)
    {
        if (float.IsNaN(sample) || float.IsInfinity(sample))
        {
            clippedSamples++;
            return 0;
        }

        if (sample > 1f)
        {
            clippedSamples++;
            sample = 1f;
        }
        else if (sample < -1f)
        {
            clippedSamples++;
            sample = -1f;
        }

        return (short)Math.Round(sample * 32767f);
    }
    private static MethodInfo? _cachedGameHandlerMethod;
    private static object? _cachedGameHandlerTarget;
    private static readonly object _testCommandQueueLock = new();
    private static Thread? _testCommandQueueThread;
    private static bool _testCommandQueueRunning;
    private static string? _testCommandQueuePath;
    private static long _testCommandQueueOffset;
    private static int _testCommandQueueDispatchCount;
    private static readonly object _runtimeDiagnosticLock = new();
    private static bool _runtimeDiagnosticStarted;
    private static bool _runtimeDiagnosticCompleted;
    private static string? _runtimeDiagnosticRawPath;
    private static string? _runtimeDiagnosticSummaryPath;
    private static DateTime _runtimeDiagnosticStartedUtc;
    private static DateTime _runtimeDiagnosticLastRawFlushUtc;
    private static DateTime _runtimeDiagnosticLastSummaryWriteUtc;
    private static readonly List<string> _runtimeDiagnosticPendingLines = new();
    private static readonly HashSet<string> _runtimeDiagnosticSeenEntries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _runtimeDiagnosticScannedTypes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> _runtimeDiagnosticCategoryCounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> _runtimeDiagnosticLikelyScores = new(StringComparer.Ordinal);
    private static readonly string[] RuntimeDiagnosticLikelyKeywords =
    {
        "speech", "recogn", "emulate", "command", "dispatch", "anim", "audio",
        "textbox", "text", "input", "button", "click", "picturebox", "show", "hide",
        "key", "form", "control", "sound", "play"
    };
    private const int RuntimeDiagnosticMaxLinesPerFlush = 250;
    private const int RuntimeDiagnosticRawFlushMs = 900;
    private const int RuntimeDiagnosticSummaryWriteMs = 4000;
    private const int RuntimeDiagnosticSnapshotIntervalMs = 2000;

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

    private sealed class CommandManifestEntry
    {
        public CommandManifestEntry(string matchPhrase, string dispatchPhrase, string commandToken, string? scriptReference)
        {
            MatchPhrase = matchPhrase;
            DispatchPhrase = dispatchPhrase;
            CommandToken = commandToken;
            ScriptReference = scriptReference;
        }

        public string MatchPhrase { get; }
        public string DispatchPhrase { get; }
        public string CommandToken { get; }
        public string? ScriptReference { get; }
    }

    public sealed class CommandAction
    {
        internal CommandAction(
            string transcript,
            string matchPhrase,
            string dispatchPhrase,
            string commandToken,
            float confidence,
            string? scriptReference,
            string? assistantLine)
        {
            Transcript = transcript;
            MatchPhrase = matchPhrase;
            DispatchPhrase = dispatchPhrase;
            CommandToken = commandToken;
            Confidence = confidence;
            ScriptReference = scriptReference;
            AssistantLine = assistantLine;
        }

        public string Transcript { get; }
        public string MatchPhrase { get; }
        public string DispatchPhrase { get; }
        public string CommandToken { get; }
        public float Confidence { get; }
        public string? ScriptReference { get; }
        public string? AssistantLine { get; }
    }

    private interface ICommandDispatcher
    {
        string Name { get; }
        bool TryDispatch(CommandAction action, out string detail);
    }

    private sealed class ReflectionCommandDispatcher : ICommandDispatcher
    {
        public string Name => "game-reflection";

        public bool TryDispatch(CommandAction action, out string detail)
        {
            if (!TryGetGameCommandHandler(out var target, out var method, out detail))
                return false;

            LogEvent($"[oww-dispatch-debug] Found handler: {method?.Name}, DispatchPhrase: '{action.DispatchPhrase}', MatchPhrase: '{action.MatchPhrase}'");
            
            try
            {
                // Try dispatching with the full dispatch phrase (including wake word)
                if (TryInvokeOnUiThread(target!, method!, action.DispatchPhrase, out detail))
                {
                    LogEvent($"[oww-dispatch-result] UI thread invoke succeeded");
                    return true;
                }

                method!.Invoke(target, new object[] { action.DispatchPhrase });
                detail = $"Invoked {method.DeclaringType?.FullName}.{method.Name}(\"{action.DispatchPhrase}\") with dispatch phrase.";
                LogEvent($"[oww-dispatch-result] Direct invoke succeeded");
                return true;
            }
            catch (Exception ex)
            {
                detail = $"Reflection dispatch failed via {method!.Name}: {ex.GetType().Name}: {ex.Message}";
                LogEvent($"[oww-dispatch-error] {detail}");
                return false;
            }
        }
    }

    private sealed class ProcessFallbackCommandDispatcher : ICommandDispatcher
    {
        public string Name => "process-fallback";

        public bool TryDispatch(CommandAction action, out string detail)
        {
            // Detect Wine to ensure native commands run on Unix even under Wine emulation
            bool isWine = IsRunningUnderWine();
            bool isRealWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) && !isWine;
            
            LogEvent($"[oww-wine-detect] IsWine={isWine}, IsOSPlatform.Windows={System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)}, IsRealWindows={isRealWindows}");
            
            // First, try to handle common commands natively on Unix systems (including Wine on Unix)
            if (!isRealWindows)
            {
                LogEvent($"[oww-native-cmd] Trying native command dispatch for token: '{action.CommandToken}'");
                if (TryDispatchNativeCommand(action, out detail))
                {
                    LogEvent($"[oww-native-cmd-success] {detail}");
                    return true;
                }
                LogEvent($"[oww-native-cmd-failed] Native dispatch returned false: {detail}");
            }

            // Fall back to script execution
            if (TryResolveScriptPath(action.CommandToken, out var scriptPath))
            {
                if (TryStartScript(scriptPath!, out detail))
                    return true;

                return false;
            }

            detail = $"No fallback script found for token '{action.CommandToken}'.";
            return false;
        }

        /// <summary>
        /// Handles common commands natively on macOS/Linux without requiring script files.
        /// </summary>
        private static bool TryDispatchNativeCommand(CommandAction action, out string detail)
        {
            // Check the MatchPhrase (user's spoken command) instead of the token
            // Token might be 'steam2' or 'task', but MatchPhrase is 'show my steam friends'
            var matchPhrase = action.MatchPhrase.ToLowerInvariant();
            LogEvent($"[oww-native-cmd-detail] Token='{action.CommandToken}', MatchPhrase='{matchPhrase}', ScriptRef='{action.ScriptReference ?? "<null>"}'");
            
            try
            {
                // Browser commands
                if (matchPhrase.Contains("browser") || matchPhrase.Contains("web"))
                {
                    LogEvent($"[oww-native-cmd] Matched browser command");
                    return ExecuteNativeCommand("open-default-browser", out detail);
                }
                
                // Task Manager / Activity Monitor
                if ((matchPhrase.Contains("task") || matchPhrase.Contains("activity")) && 
                    (matchPhrase.Contains("manager") || matchPhrase.Contains("monitor")))
                {
                    LogEvent($"[oww-native-cmd] Matched task manager command");
                    return ExecuteNativeCommand("open-task-manager", out detail);
                }
                
                // Steam commands
                if (matchPhrase.Contains("steam"))
                {
                    LogEvent($"[oww-native-cmd] Matched steam command");
                    
                    // Check for status-related commands (invisible, offline, hide status)
                    if (matchPhrase.Contains("invisible") || 
                        matchPhrase.Contains("offline") || 
                        (matchPhrase.Contains("hide") && matchPhrase.Contains("status")) ||
                        (matchPhrase.Contains("appear") && matchPhrase.Contains("offline")))
                    {
                        return ExecuteNativeCommand("steam-set-invisible", out detail);
                    }
                    if (matchPhrase.Contains("online") || 
                        matchPhrase.Contains("active") ||
                        (matchPhrase.Contains("show") && matchPhrase.Contains("status")))
                    {
                        return ExecuteNativeCommand("steam-set-online", out detail);
                    }
                    if (matchPhrase.Contains("friend"))
                    {
                        return ExecuteNativeCommand("steam-friends", out detail);
                    }
                    if (matchPhrase.Contains("library") || matchPhrase.Contains("game"))
                    {
                        return ExecuteNativeCommand("steam-library", out detail);
                    }
                    if (matchPhrase.Contains("overlay") || matchPhrase.Contains("settings"))
                    {
                        return ExecuteNativeCommand("steam-overlay", out detail);
                    }
                    // Generic steam command - open Steam
                    return ExecuteNativeCommand("steam-launch", out detail);
                }
                
                detail = $"Not a native command token (matchPhrase='{matchPhrase}').";
                LogEvent($"[oww-native-cmd] {detail}");
                return false;
            }
            catch (Exception ex)
            {
                detail = $"Native command execution failed: {ex.Message}";
                LogEvent($"[oww-native-command-error] {detail}");
                return false;
            }
        }

        /// <summary>
        /// Executes a native platform command.
        /// </summary>
        private static bool ExecuteNativeCommand(string commandType, out string detail)
        {
            try
            {
                ProcessStartInfo? psi = null;
                bool isMacOS = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);
                bool isLinux = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);
                bool isWine = IsRunningUnderWine();
                
                // Under Wine on macOS, we should still execute macOS commands
                bool useMacOSCommands = isMacOS || (isWine && !isLinux);
                bool useLinuxCommands = isLinux;
                
                LogEvent($"[oww-native-exec] commandType='{commandType}', isMacOS={isMacOS}, isLinux={isLinux}, isWine={isWine}, useMacOSCommands={useMacOSCommands}");
                
                if (useMacOSCommands)
                {
                    if (isWine)
                    {
                        // Under Wine, execute commands through the host macOS's /bin/sh
                        // Wine maps /bin/sh to the host macOS's /bin/sh
                        psi = commandType switch
                        {
                            "open-default-browser" => new ProcessStartInfo
                            {
                                FileName = "/bin/sh",
                                Arguments = "-c \"open -a Safari 'https://'\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            "open-task-manager" => new ProcessStartInfo
                            {
                                FileName = "/bin/sh",
                                Arguments = "-c \"open -a 'Activity Monitor'\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            "steam-friends" => new ProcessStartInfo
                            {
                                FileName = "/bin/sh",
                                Arguments = "-c \"open 'steam://open/friends'\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            "steam-library" => new ProcessStartInfo
                            {
                                FileName = "/bin/sh",
                                Arguments = "-c \"open 'steam://open/library'\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            "steam-overlay" => new ProcessStartInfo
                            {
                                FileName = "/bin/sh",
                                Arguments = "-c \"open 'steam://open/settings'\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            "steam-launch" => new ProcessStartInfo
                            {
                                FileName = "/bin/sh",
                                Arguments = "-c \"open -a 'Steam' || open 'steam://open/main'\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            "steam-set-invisible" => new ProcessStartInfo
                            {
                                FileName = "/bin/sh",
                                Arguments = "-c \"echo 'tell application \\\"Steam\\\" to activate' > /tmp/steam-status.scpt; echo 'delay 0.5' >> /tmp/steam-status.scpt; echo 'tell application \\\"System Events\\\"' >> /tmp/steam-status.scpt; echo 'tell process \\\"Steam\\\"' >> /tmp/steam-status.scpt; echo 'set frontmost to true' >> /tmp/steam-status.scpt; echo 'end tell' >> /tmp/steam-status.scpt; echo 'try' >> /tmp/steam-status.scpt; echo 'click menu item \\\"Invisible\\\" of menu \\\"Friends\\\" of menu bar item \\\"Friends\\\" of menu bar 1' >> /tmp/steam-status.scpt; echo 'end try' >> /tmp/steam-status.scpt; echo 'end tell' >> /tmp/steam-status.scpt; osascript /tmp/steam-status.scpt 2>&1 || open 'steam://friends'\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            "steam-set-online" => new ProcessStartInfo
                            {
                                FileName = "/bin/sh",
                                Arguments = "-c \"echo 'tell application \\\"Steam\\\" to activate' > /tmp/steam-status.scpt; echo 'delay 0.5' >> /tmp/steam-status.scpt; echo 'tell application \\\"System Events\\\"' >> /tmp/steam-status.scpt; echo 'tell process \\\"Steam\\\"' >> /tmp/steam-status.scpt; echo 'set frontmost to true' >> /tmp/steam-status.scpt; echo 'end tell' >> /tmp/steam-status.scpt; echo 'try' >> /tmp/steam-status.scpt; echo 'click menu item \\\"Online\\\" of menu \\\"Friends\\\" of menu bar item \\\"Friends\\\" of menu bar 1' >> /tmp/steam-status.scpt; echo 'end try' >> /tmp/steam-status.scpt; echo 'end tell' >> /tmp/steam-status.scpt; osascript /tmp/steam-status.scpt 2>&1 || open 'steam://friends'\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            _ => null
                        };
                    }
                    else
                    {
                        // Native macOS (not under Wine)
                        psi = commandType switch
                        {
                            "open-default-browser" => new ProcessStartInfo
                            {
                                FileName = "open",
                                Arguments = "-a Safari https://",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            "open-task-manager" => new ProcessStartInfo
                            {
                                FileName = "open",
                                Arguments = "-a 'Activity Monitor'",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            "steam-friends" => new ProcessStartInfo
                            {
                                FileName = "open",
                                Arguments = "steam://open/friends",
                                UseShellExecute = true
                            },
                            "steam-library" => new ProcessStartInfo
                            {
                                FileName = "open",
                                Arguments = "steam://open/library",
                                UseShellExecute = true
                            },
                            "steam-overlay" => new ProcessStartInfo
                            {
                                FileName = "open",
                                Arguments = "steam://open/settings",
                                UseShellExecute = true
                            },
                            "steam-launch" => new ProcessStartInfo
                            {
                                FileName = "open",
                                Arguments = "-a Steam",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            "steam-set-invisible" => new ProcessStartInfo
                            {
                                FileName = "/bin/sh",
                                Arguments = "-c \"echo 'tell application \\\"Steam\\\" to activate' > /tmp/steam-status.scpt; echo 'delay 0.5' >> /tmp/steam-status.scpt; echo 'tell application \\\"System Events\\\"' >> /tmp/steam-status.scpt; echo 'tell process \\\"Steam\\\"' >> /tmp/steam-status.scpt; echo 'set frontmost to true' >> /tmp/steam-status.scpt; echo 'end tell' >> /tmp/steam-status.scpt; echo 'try' >> /tmp/steam-status.scpt; echo 'click menu item \\\"Invisible\\\" of menu \\\"Friends\\\" of menu bar item \\\"Friends\\\" of menu bar 1' >> /tmp/steam-status.scpt; echo 'end try' >> /tmp/steam-status.scpt; echo 'end tell' >> /tmp/steam-status.scpt; osascript /tmp/steam-status.scpt 2>&1 || open 'steam://friends'\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            "steam-set-online" => new ProcessStartInfo
                            {
                                FileName = "/bin/sh",
                                Arguments = "-c \"echo 'tell application \\\"Steam\\\" to activate' > /tmp/steam-status.scpt; echo 'delay 0.5' >> /tmp/steam-status.scpt; echo 'tell application \\\"System Events\\\"' >> /tmp/steam-status.scpt; echo 'tell process \\\"Steam\\\"' >> /tmp/steam-status.scpt; echo 'set frontmost to true' >> /tmp/steam-status.scpt; echo 'end tell' >> /tmp/steam-status.scpt; echo 'try' >> /tmp/steam-status.scpt; echo 'click menu item \\\"Online\\\" of menu \\\"Friends\\\" of menu bar item \\\"Friends\\\" of menu bar 1' >> /tmp/steam-status.scpt; echo 'end try' >> /tmp/steam-status.scpt; echo 'end tell' >> /tmp/steam-status.scpt; osascript /tmp/steam-status.scpt 2>&1 || open 'steam://friends'\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            },
                            _ => null
                        };
                    }
                }
                else if (useLinuxCommands)
                {
                    psi = commandType switch
                    {
                        "open-default-browser" => new ProcessStartInfo
                        {
                            FileName = "xdg-open",
                            Arguments = "https://",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        "open-task-manager" => new ProcessStartInfo
                        {
                            FileName = "bash",
                            Arguments = "-c \"gnome-system-monitor || mate-system-monitor || xterm -e htop || htop\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        "steam-friends" => new ProcessStartInfo
                        {
                            FileName = "xdg-open",
                            Arguments = "steam://open/friends",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        "steam-library" => new ProcessStartInfo
                        {
                            FileName = "xdg-open",
                            Arguments = "steam://open/library",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        "steam-overlay" => new ProcessStartInfo
                        {
                            FileName = "xdg-open",
                            Arguments = "steam://open/settings",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        "steam-launch" => new ProcessStartInfo
                        {
                            FileName = "bash",
                            Arguments = "-c \"command -v steam >/dev/null 2>&1 && steam || xdg-open steam://open/main\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        "steam-set-invisible" => new ProcessStartInfo
                        {
                            FileName = "bash",
                            Arguments = "-c \"steam +opensteamweb +friends_status_invisible 2>/dev/null || (xdg-open 'steam://friends' && echo 'Steam friends opened - manually set to invisible')\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        "steam-set-online" => new ProcessStartInfo
                        {
                            FileName = "bash",
                            Arguments = "-c \"steam +opensteamweb +friends_status_online 2>/dev/null || (xdg-open 'steam://friends' && echo 'Steam friends opened - manually set to online')\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        _ => null
                    };
                }
                
                if (psi != null)
                {
                    LogEvent($"[oww-native-command] Executing: {psi.FileName} {psi.Arguments}");
                    Process.Start(psi);
                    detail = $"Executed native command: {psi.FileName} {psi.Arguments}";
                    return true;
                }
                
                detail = $"No native command handler for: {commandType}";
                return false;
            }
            catch (Exception ex)
            {
                detail = $"Failed to execute native command: {ex.Message}";
                LogEvent($"[oww-native-command-error] {detail}");
                return false;
            }
        }

        private static string[] GetPlatformSpecificScriptExtensions()
        {
            var isWine = IsRunningUnderWine();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !isWine)
            {
                return new[] { ".bat", ".cmd", ".ps1", ".exe", ".sh", ".command" };
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || isWine)
            {
                return new[] { ".command", ".sh", ".exe", ".bat", ".cmd", ".ps1" };
            }

            return new[] { ".sh", ".exe", ".bat", ".cmd", ".ps1", ".command" };
        }

        private static bool TryResolveScriptPath(string commandToken, out string? scriptPath)
        {
            scriptPath = null;
            if (!TryNormalizeCommandToken(commandToken, out var sanitizedToken, out var reason))
            {
                LogEvent($"[oww-command] Skipping unsafe command token '{commandToken}': {reason}");
                return false;
            }

            var baseDirectory = ResolveCommandRootDirectory();
            if (string.IsNullOrWhiteSpace(baseDirectory))
                return false;

            var searchRoots = new[]
            {
                System.IO.Path.Combine(baseDirectory!, "files"),
                System.IO.Path.Combine(baseDirectory!, "custom-commands"),
                System.IO.Path.Combine(baseDirectory!, "animations"),
                baseDirectory!
            };

            var extensions = GetPlatformSpecificScriptExtensions();

            foreach (var root in searchRoots)
            {
                var fullRoot = System.IO.Path.GetFullPath(root);
                var normalizedRoot = fullRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                var rootPrefix = normalizedRoot + System.IO.Path.DirectorySeparatorChar;
                foreach (var extension in extensions)
                {
                    var candidate = System.IO.Path.Combine(root, sanitizedToken + extension);
                    var fullCandidate = System.IO.Path.GetFullPath(candidate);
                    if (!fullCandidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (System.IO.File.Exists(fullCandidate))
                    {
                        scriptPath = fullCandidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryStartScript(string scriptPath, out string detail)
        {
            try
            {
                var extension = System.IO.Path.GetExtension(scriptPath);
                var timeoutMs = GetFallbackScriptTimeoutMs();
                ProcessStartInfo startInfo;

                if (string.Equals(extension, ".sh", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".command", StringComparison.OrdinalIgnoreCase))
                {
                    startInfo = new ProcessStartInfo
                    {
                        FileName = "sh",
                        Arguments = QuoteArgument(scriptPath),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(scriptPath)
                    };
                }
                else if (string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
                {
                    var isWine = IsRunningUnderWine();
                    var realWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !isWine;
                    if (!realWindows)
                    {
                        return TryTranslateAndExecuteBatch(scriptPath, timeoutMs, out detail);
                    }

                    startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {QuoteArgument(scriptPath)}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(scriptPath)
                    };
                }
                else
                {
                    startInfo = new ProcessStartInfo
                    {
                        FileName = scriptPath,
                        UseShellExecute = true,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(scriptPath)
                    };
                }

                var process = Process.Start(startInfo);
                if (process == null)
                {
                    detail = $"Process launch returned null for script '{scriptPath}'.";
                    return false;
                }

                // Keep fallback commands bounded; if they run long, treat as background and avoid hangs.
                if (WaitForProcessExit(process, timeoutMs, killOnTimeout: false, out var timedOut))
                {
                    detail = process.ExitCode == 0
                        ? $"Fallback script completed: '{scriptPath}'."
                        : $"Fallback script exited with code {process.ExitCode}: '{scriptPath}'.";
                    return process.ExitCode == 0;
                }

                if (timedOut)
                {
                    LogEvent($"[oww-command] Script still running after {timeoutMs}ms; leaving background process active: {scriptPath}");
                    detail = $"Fallback script launched and left running after timeout budget ({timeoutMs}ms): '{scriptPath}'.";
                    return true;
                }

                detail = $"Fallback script launched: '{scriptPath}'.";
                return true;
            }
            catch (Exception ex)
            {
                detail = $"Failed to launch fallback script '{scriptPath}': {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static bool TryTranslateAndExecuteBatch(string batchFilePath, int timeoutMs, out string detail)
        {
            detail = string.Empty;

            try
            {
                if (!System.IO.File.Exists(batchFilePath))
                {
                    detail = $"Batch file not found: '{batchFilePath}'";
                    return false;
                }

                var batchContent = System.IO.File.ReadAllText(batchFilePath);
                var translatedContent = BatchFileTranslator.TranslateBatchContent(batchContent);
                if (string.IsNullOrWhiteSpace(translatedContent))
                {
                    detail = $"Batch file produced no executable commands after translation: '{batchFilePath}'";
                    return false;
                }

                var converterMode = Environment.GetEnvironmentVariable("PAICOM_BATCH_TO_SHELL_MODE") ?? "internal";
                if (converterMode.Equals("generate-and-test", StringComparison.OrdinalIgnoreCase) &&
                    BatchFileTranslator.TryGenerateShellEquivalent(batchFilePath, translatedContent, out var generatedPath, LogEvent))
                {
                    var (isValid, error) = BatchFileTranslator.ValidateShellSyntax(translatedContent);
                    if (!isValid)
                        LogEvent($"[batch-converter-warning] Generated shell script has syntax issues: {error}");
                    else
                        LogEvent($"[batch-converter] Shell equivalent validated successfully: {generatedPath}");
                }

                var unixWorkingDir = ConvertWinePathToUnix(System.IO.Path.GetDirectoryName(batchFilePath) ?? ".");
                var startInfo = new ProcessStartInfo
                {
                    FileName = "sh",
                    Arguments = "-c " + QuoteArgument(translatedContent),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = unixWorkingDir
                };

                var process = Process.Start(startInfo);
                if (process == null)
                {
                    detail = $"Failed to start translated batch process: '{batchFilePath}'";
                    return false;
                }

                if (!WaitForProcessExit(process, timeoutMs, killOnTimeout: true, out var timedOut))
                {
                    detail = timedOut
                        ? $"Translated batch timed out after {timeoutMs}ms and was terminated: '{batchFilePath}'"
                        : $"Translated batch did not complete cleanly: '{batchFilePath}'";
                    return false;
                }

                detail = process.ExitCode == 0
                    ? $"Translated and executed batch file: '{batchFilePath}'"
                    : $"Translated batch exited with code {process.ExitCode}: '{batchFilePath}'";
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                detail = $"Failed to translate and execute batch file '{batchFilePath}': {ex.GetType().Name}: {ex.Message}";
                LogEvent($"[batch-converter-error] {detail}");
                return false;
            }
        }

        private static string ConvertWinePathToUnix(string winePath)
        {
            if (string.IsNullOrEmpty(winePath))
                return ".";

            var unixPath = winePath.Replace('\\', '/');
            if (unixPath.StartsWith("Z:/", StringComparison.OrdinalIgnoreCase))
                unixPath = unixPath.Substring(2);

            if (!System.IO.Directory.Exists(unixPath) && !System.IO.Directory.Exists(winePath))
                return ".";

            return unixPath;
        }

        private static bool WaitForProcessExit(Process process, int timeoutMs, bool killOnTimeout, out bool timedOut)
        {
            timedOut = false;
            try
            {
                if (process.WaitForExit(timeoutMs))
                    return true;

                timedOut = true;
                if (killOnTimeout)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Best effort only.
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string QuoteArgument(string argument)
        {
            if (argument.IndexOf(' ') < 0 && argument.IndexOf('\t') < 0)
                return argument;

            return "\"" + argument.Replace("\"", "\\\"") + "\"";
        }
    }

    private sealed class SpeechEmulationCommandDispatcher : ICommandDispatcher
    {
        public string Name => "speech-emulation";

        public bool TryDispatch(CommandAction action, out string detail)
        {
            return TryDispatchViaSpeechEmulation(action.DispatchPhrase, out detail);
        }
    }

    private sealed class UiSimulationCommandDispatcher : ICommandDispatcher
    {
        public string Name => "ui-simulation";

        public bool TryDispatch(CommandAction action, out string detail)
        {
            return TryDispatchViaUiSimulation(action.DispatchPhrase, out detail);
        }
    }

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
                EnableUnhandledExceptionLogging();

                // Report architecture diagnostics at startup
                ReportStartupDiagnostics();

                // Load settings from environment or use embedded defaults
                var embeddedSettings = OpenWakeWordSettings.CreateDefault();
                _settings = OpenWakeWordSettings.FromEnvironmentVariables();

                var migrationMode = Environment.GetEnvironmentVariable("PAICOM_MIGRATION_MODE") ?? "full";
                var processBitness = Environment.Is64BitProcess ? "64" : "32";
                LogEvent($"arch.process_bitness={processBitness}");
                if ((string.Equals(migrationMode, "probe", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(migrationMode, "full", StringComparison.OrdinalIgnoreCase)) &&
                    !Environment.Is64BitProcess)
                {
                    LogEvent("reason.code=PROBE_STILL_32BIT");
                }

                // Force command manifest initialization during startup so runtime diagnostics
                // can start immediately after commands are loaded.
                try
                {
                    LogEvent("[oww-command] Initializing command manifest cache...");
                    _ = KnownCommands.Value;
                    LogEvent("[oww-command] Command manifest cache initialized.");
                }
                catch (Exception manifestEx)
                {
                    LogEvent($"[oww-command] Command manifest initialization failed: {manifestEx}");
                    lock (_commandManifestLock)
                    {
                        KnownCommands = new Lazy<IReadOnlyList<CommandManifestEntry>>(CreateFallbackCommandManifest, true);
                    }
                }

                if (IsRuntimeDiagnosticEnabled())
                    EnsureRuntimeDiagnosticStarted("startup");

                StartTestCommandQueueLoopIfEnabled();

                StartPatreonFormattingFixLoop();
                
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
                
                // Initialize sequential method testing mode if enabled
                InitializeMethodTestingMode();
                
                // Initialize file-based command input if enabled
                InitializeFileCommandInput();
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
    /// Initialize sequential method testing mode for diagnostics.
    /// Enables focused testing of method categories during handler discovery.
    /// </summary>
    private static void InitializeMethodTestingMode()
    {
        LogEvent($"[TRACE] InitializeMethodTestingMode() called");
        lock (_methodTestLock)
        {
            var testModeVar = Environment.GetEnvironmentVariable("PAICOM_SEQUENTIAL_METHOD_TEST");
            LogEvent($"[TRACE] testModeVar={testModeVar ?? "(null)"}");
            
            _sequentialMethodTestMode = string.Equals(testModeVar, "1", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(testModeVar, "true", StringComparison.OrdinalIgnoreCase);
            
            LogEvent($"[TRACE] _sequentialMethodTestMode={_sequentialMethodTestMode}");
            
            if (_sequentialMethodTestMode)
            {
                _testCategoryFilter = Environment.GetEnvironmentVariable("PAICOM_TEST_CATEGORY");
                var testNum = Environment.GetEnvironmentVariable("PAICOM_METHOD_TEST_NUM");
                
                LogEvent($"[methodtest] Sequential method testing ENABLED");
                if (!string.IsNullOrEmpty(_testCategoryFilter))
                    LogEvent($"[methodtest] Category filter: {_testCategoryFilter}");
                if (!string.IsNullOrEmpty(testNum))
                    LogEvent($"[methodtest] Test number: {testNum}");
            }
        }
    }

    /// <summary>
    /// Initialize file-based command input mode.
    /// Enables reading commands from input-command.txt file for automation/testing.
    /// </summary>
    private static void InitializeFileCommandInput()
    {
        lock (_fileCommandInputLock)
        {
            var fileInputVar = Environment.GetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT");
            _fileCommandInputEnabled = string.Equals(fileInputVar, "1", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(fileInputVar, "true", StringComparison.OrdinalIgnoreCase);
            
            if (_fileCommandInputEnabled)
            {
                _fileCommandInputPath = Environment.GetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT_PATH");
                if (string.IsNullOrWhiteSpace(_fileCommandInputPath))
                {
                    _fileCommandInputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "input-command.txt");
                }

                LogEvent($"[fileinput] File-based command input ENABLED");
                LogEvent($"[fileinput] Input file path: {_fileCommandInputPath}");

                // Create the input file if it doesn't exist
                try
                {
                    if (!File.Exists(_fileCommandInputPath))
                    {
                        File.WriteAllText(_fileCommandInputPath, string.Empty);
                        LogEvent($"[fileinput] Created input file: {_fileCommandInputPath}");
                    }
                }
                catch (Exception ex)
                {
                    LogEvent($"[fileinput-error] Failed to create input file: {ex.Message}");
                    _fileCommandInputEnabled = false;
                }

                // Start the file monitoring thread if enabled
                if (_fileCommandInputEnabled && !_fileCommandInputThreadStarted)
                {
                    _fileCommandInputThreadStarted = true;
                    var monitorThread = new Thread(() => MonitorFileCommandInput()) 
                    { 
                        IsBackground = true, 
                        Name = "FileCommandInputMonitor"
                    };
                    monitorThread.Start();
                    LogEvent($"[fileinput] File monitoring thread started");
                }
            }
        }
    }

    /// <summary>
    /// Background thread that monitors input-command.txt for new commands.
    /// Reads command, processes it through animation pipeline, and clears file.
    /// </summary>
    private static void MonitorFileCommandInput()
    {
        const int checkIntervalMs = 500;
        
        while (_fileCommandInputEnabled && !string.IsNullOrEmpty(_fileCommandInputPath))
        {
            try
            {
                Thread.Sleep(checkIntervalMs);

                if (!File.Exists(_fileCommandInputPath))
                    continue;

                var content = File.ReadAllText(_fileCommandInputPath).Trim();
                
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                // Found a command - process it
                LogEvent($"[fileinput] Received command: {content}");
                
                // Resolve and dispatch the command through the animation pipeline
                var action = ResolveCommandAction(content);
                if (action != null)
                {
                    var success = DispatchCommandAction(action, out var detail);
                    LogEvent($"[fileinput] Dispatch result: {(success ? "SUCCESS" : "FAILED")} - {detail}");
                    
                    // Queue animation script execution if one is referenced (same as voice path)
                    if (success && !string.IsNullOrWhiteSpace(action.ScriptReference))
                    {
                        LogEvent($"[fileinput] Queuing animation script execution: {action.ScriptReference}");
                        System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ =>
                        {
                            try
                            {
                                TryExecuteAnimationScriptSync(action.ScriptReference);
                            }
                            catch (Exception ex)
                            {
                                LogEvent($"[fileinput] Exception in animation script: {ex.GetType().Name}: {ex.Message}");
                            }
                        }, null);
                    }
                }
                else
                {
                    LogEvent($"[fileinput] Command not recognized: {content}");
                }

                // Clear the file for next command
                try
                {
                    File.WriteAllText(_fileCommandInputPath, string.Empty);
                    LogEvent($"[fileinput] Input file cleared, ready for next command");
                }
                catch (Exception ex)
                {
                    LogEvent($"[fileinput-error] Failed to clear input file: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                LogEvent($"[fileinput-error] Exception in file input monitor: {ex.GetType().Name}: {ex.Message}");
                Thread.Sleep(1000); // Back off on error
            }
        }

        LogEvent($"[fileinput] File command input monitor thread exiting");
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
        var verboseAudio = IsVerboseAudioLoggingEnabled();

        if (verboseAudio && (_enqueueAudioCallCount <= 10 || _enqueueAudioCallCount % 100 == 0))
        {
            bool isLocked = _lockManager?.IsLocked == true;
            LogEvent($"[vosk-diag] EnqueueAudio CALLED #{_enqueueAudioCallCount}, locked={isLocked}, argType={audioArg?.GetType().Name ?? "null"}");
        }

        if (_enqueueAudioCallCount == 1 || _enqueueAudioCallCount % 500 == 0)
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
            SanitizeAudioChunk(floatChunk, out var clippedSamples);
            if (clippedSamples > 0)
            {
                _audioClipLogCount++;
                if (verboseAudio || _audioClipLogCount <= 3 || _audioClipLogCount % 25 == 0)
                {
                    LogEvent($"[vosk-audio-guard] Clipped {clippedSamples} sample(s) before enqueue");
                }
            }

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
                    if (_voskLockAudioQueue.Count >= MaxVoskLockQueueChunks)
                    {
                        _voskLockAudioQueue.Dequeue();
                        _voskLockAudioDequeuedCount++;
                        _voskLockAudioDroppedCount++;
                        if (verboseAudio || _voskLockAudioDroppedCount <= 3 || _voskLockAudioDroppedCount % 25 == 0)
                        {
                            LogEvent($"[vosk-audio-queue] Queue full; dropped oldest chunk (drops={_voskLockAudioDroppedCount}, max={MaxVoskLockQueueChunks})");
                        }
                    }

                    _voskLockAudioQueue.Enqueue(floatChunk);
                    _voskLockAudioQueuedCount++;
                    var wakeId = Interlocked.Read(ref _activeWakeSequenceId);
                    if (wakeId > 0 && Interlocked.Read(ref _firstQueuedAudioMarkerWakeId) != wakeId)
                    {
                        Interlocked.Exchange(ref _firstQueuedAudioMarkerWakeId, wakeId);
                        LogTimingMarker("first_queued_audio", wakeId, $"queue.size={_voskLockAudioQueue.Count};chunk.len={floatChunk.Length}");
                    }

                    if (verboseAudio && (_voskLockAudioQueuedCount <= 3 || _voskLockAudioQueuedCount % 10 == 0))
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
                if (verboseAudio && (_enqueueSuccessCount == 1 || _enqueueSuccessCount % 200 == 0))
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

    private static void EnableUnhandledExceptionLogging()
    {
        if (!TryParseBooleanEnvironmentVariable("PAICOM_LOG_UNHANDLED_EXCEPTIONS", out var enabled) || !enabled)
            return;

        lock (_exceptionHookLock)
        {
            if (_exceptionHooksInstalled)
                return;

            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
            TryHookWinFormsThreadException();
            _exceptionHooksInstalled = true;
            LogEvent("[oww-exception] Global exception logging enabled.");
        }
    }

    private static void HandleUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        try
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogException("unhandled", ex);
                return;
            }

            LogEvent($"[oww-exception] unhandled {args.ExceptionObject}");
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static void HandleUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs args)
    {
        try
        {
            LogException("task-unobserved", args.Exception);
            args.SetObserved();
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static void TryHookWinFormsThreadException()
    {
        try
        {
            var appType = Type.GetType("System.Windows.Forms.Application, System.Windows.Forms", throwOnError: false);
            if (appType == null)
                return;

            var threadExceptionEvent = appType.GetEvent("ThreadException", BindingFlags.Public | BindingFlags.Static);
            if (threadExceptionEvent?.EventHandlerType == null)
                return;

            var handlerMethod = typeof(OpenWakeWordHelper).GetMethod(
                nameof(HandleWinFormsThreadException),
                BindingFlags.NonPublic | BindingFlags.Static);

            if (handlerMethod == null)
                return;

            var handler = Delegate.CreateDelegate(threadExceptionEvent.EventHandlerType, handlerMethod);
            threadExceptionEvent.AddEventHandler(null, handler);
            LogEvent("[oww-exception] WinForms ThreadException handler registered.");
        }
        catch (Exception ex)
        {
            LogEvent($"[oww-exception] WinForms ThreadException hook failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void HandleWinFormsThreadException(object? sender, object? args)
    {
        try
        {
            var ex = args?.GetType().GetProperty("Exception")?.GetValue(args) as Exception;
            if (ex != null)
            {
                LogException("winforms-thread", ex);
                return;
            }

            LogEvent("[oww-exception] winforms-thread exception raised (no Exception property)." );
        }
        catch (Exception ex)
        {
            LogEvent($"[oww-exception] winforms-thread handler failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogException(string category, Exception ex)
    {
        LogEvent($"[oww-exception] {category} {ex.GetType().Name}: {ex.Message}");

        var stack = ex.StackTrace;
        if (!string.IsNullOrWhiteSpace(stack))
        {
            var lines = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length && i < MaxExceptionStackLines; i++)
            {
                LogEvent($"[oww-exception] {category} {lines[i]}");
            }
        }

        if (ex.InnerException != null)
        {
            LogEvent($"[oww-exception] {category} inner {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }

        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var modules = proc.Modules;
            var count = Math.Min(modules.Count, 40);
            LogEvent($"[oww-exception] {category} loaded_modules_count={modules.Count}");
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var m = modules[i];
                    LogEvent($"[oww-exception] {category} module={m.ModuleName};file={m.FileName};base=0x{m.BaseAddress.ToInt64():X};size={m.ModuleMemorySize}");
                }
                catch { }
            }
        }
        catch
        {
            // Best-effort only
        }

        try
        {
            // Attempt to write a full-memory dump for native crash analysis
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                var diagDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics");
                Directory.CreateDirectory(diagDir);
                var dumpPath = Path.Combine(diagDir, $"native-crash-oww-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.dmp");
                using (var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var hProcess = proc.Handle;
                    var pid = (uint)proc.Id;
                    const uint MiniDumpWithFullMemory = 0x00000002;
                    var ok = MiniDumpWriteDump(hProcess, pid, fs.SafeFileHandle.DangerousGetHandle(), MiniDumpWithFullMemory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    LogEvent($"[oww-exception] dump_write_result={ok};dump_path={dumpPath}");
                }
            }
            catch (Exception dumpEx)
            {
                try { LogEvent($"[oww-exception] dump_write_failed={dumpEx.Message}"); } catch { }
            }
        }
        catch
        {
            // Best-effort only
        }

        [System.Runtime.InteropServices.DllImport("Dbghelp.dll", SetLastError = true)]
        static extern bool MiniDumpWriteDump(IntPtr hProcess, uint ProcessId, IntPtr hFile, uint DumpType, IntPtr ExceptionParam, IntPtr UserStreamParam, IntPtr CallbackParam);

        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            LogEvent($"[oww-exception] {category} managed_assemblies_count={assemblies.Length}");
            foreach (var a in assemblies)
            {
                try
                {
                    var loc = string.Empty;
                    try { loc = a.Location; } catch { loc = "<no-location>"; }
                    LogEvent($"[oww-exception] {category} assembly={a.GetName().Name};version={a.GetName().Version};location={loc}");
                }
                catch { }
            }
        }
        catch
        {
            // Best-effort only
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
            var migrationMode = Environment.GetEnvironmentVariable("PAICOM_MIGRATION_MODE") ?? "full";
            var verifiedRuntime = Environment.GetEnvironmentVariable("PAICOM_RUNTIME_VERIFIED_64BIT") ?? "unknown";
            var winePrefix = Environment.GetEnvironmentVariable("WINEPREFIX") ?? "<not-set>";
            var runtimeDiagnosticMode = Environment.GetEnvironmentVariable("PAICOM_RUNTIME_DIAGNOSTIC_MODE") ?? "<not-set>";
            var runtimeDiagnosticDuration = Environment.GetEnvironmentVariable("PAICOM_RUNTIME_DIAGNOSTIC_DURATION_SECONDS") ?? "<not-set>";

            LogEvent($"[startup-diag] process.bitness={processBitness}");
            LogEvent($"[startup-diag] migration.mode={migrationMode}");
            LogEvent($"[startup-diag] launcher.verified_64bit={verifiedRuntime}");
            LogEvent($"[startup-diag] wine.prefix={winePrefix}");
            LogEvent($"[startup-diag] runtime.diagnostic.mode={runtimeDiagnosticMode}");
            LogEvent($"[startup-diag] runtime.diagnostic.duration_seconds={runtimeDiagnosticDuration}");
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
            var voskEnabled = "enabled";
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
        var wakeId = Interlocked.Increment(ref _wakeSequenceCounter);
        Interlocked.Exchange(ref _activeWakeSequenceId, wakeId);
        Interlocked.Exchange(ref _firstQueuedAudioMarkerWakeId, 0);
        LogTimingMarker("wake_detected", wakeId);

        LogEvent("Wake-word lock issued; starting Vosk speech recognition...");
        EnsureMicrophoneCaptureStarted("wake-handoff", forceRestart: false);
        
        // Initialize Vosk if not already attempted
        if (!_voskInitAttempted)
        {
            LogTimingMarker("vosk_init_start", wakeId);
            _voskInitAttempted = true;
            try
            {
                _voskRecognizer = new VoskSpeechRecognizer(LogEvent);
                if (!_voskRecognizer.Initialize())
                {
                    LogTimingMarker("vosk_init_end", wakeId, "status=failed");
                    LogEvent("Vosk initialization failed; speech recognition unavailable");
                    _voskRecognizer?.Dispose();
                    _voskRecognizer = null;
                }
                else
                {
                    LogTimingMarker("vosk_init_end", wakeId, "status=ok");
                }
            }
            catch (Exception ex)
            {
                LogTimingMarker("vosk_init_end", wakeId, "status=exception");
                LogEvent($"Exception initializing Vosk: {ex.Message}");
                _voskRecognizer?.Dispose();
                _voskRecognizer = null;
            }
        }
        else
        {
            LogTimingMarker("vosk_init_end", wakeId, "status=cached");
        }

        if (_voskRecognizer == null)
        {
            LogTimingMarker("vosk_unavailable", wakeId);
            LogEvent("Vosk recognizer not available; speech recognition skipped");
            return;
        }

        if (_voskListening)
        {
            LogTimingMarker("lock_window_skip", wakeId, "already_listening=true");
            LogEvent("Vosk speech recognition already active for current lock window");
            return;
        }

        // Start background task to process audio during lock window
        _voskListening = true;
        LogTimingMarker("lock_window_start", wakeId, $"lock.ms={_settings?.LockDurationMs ?? 0}");
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
            var wakeId = Interlocked.Read(ref _activeWakeSequenceId);
            var settings = _settings ?? OpenWakeWordSettings.CreateDefault();
            var wakeUtc = DateTime.UtcNow;
            var stopTime = wakeUtc.AddMilliseconds(settings.LockDurationMs + 500);
            var graceDeadline = wakeUtc.AddMilliseconds(settings.PostWakeSilenceGraceMilliseconds);
            var silenceCutoffMs = settings.SpeechSilenceCutoffMilliseconds;
            var lockWindowEndReason = "lock_timeout";
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
                        lockWindowEndReason = "silence_cutoff";
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
                            lockWindowEndReason = "silence_cutoff_no_audio";
                            LogEvent($"[vosk-speech] Silence cutoff reached after {silenceCutoffMs}ms without additional speech; finalizing speech capture.");
                            break;
                        }
                    }

                    System.Threading.Thread.Sleep(50);
                }
            }

            if (DateTime.UtcNow >= stopTime)
                lockWindowEndReason = "lock_window_timeout";

            LogTimingMarker("lock_window_end", wakeId, $"reason={lockWindowEndReason};chunks={dequeuedChunks};samples={accumulatedAudio.Count};dropped={_voskLockAudioDroppedCount}");
            LogEvent($"Speech window ended. Processing {accumulatedAudio.Count} accumulated audio samples.");

            if (accumulatedAudio.Count > 0)
            {
                // Convert ALL accumulated float audio to PCM bytes for Vosk
                byte[] pcmData = new byte[accumulatedAudio.Count * 2];
                var pcmClippedSamples = 0;
                for (int i = 0; i < accumulatedAudio.Count; i++)
                {
                    short sample = FloatToPcm16(accumulatedAudio[i], ref pcmClippedSamples);
                    BitConverter.GetBytes(sample).CopyTo(pcmData, i * 2);
                }

                if (pcmClippedSamples > 0)
                {
                    _pcmClipLogCount++;
                    if (_pcmClipLogCount <= 3 || _pcmClipLogCount % 25 == 0)
                    {
                        LogEvent($"[vosk-audio-guard] Clipped {pcmClippedSamples} PCM sample(s) before Vosk processing");
                    }
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
            else
            {
                var partialFallback = _voskRecognizer.GetPartialResult();
                if (!string.IsNullOrWhiteSpace(partialFallback))
                {
                    LogEvent("[vosk-speech] Final result empty; using partial-result fallback.");
                    LogTimingMarker("transcript_fallback_partial", wakeId);
                    HandleRecognizedSpeech(partialFallback);
                }
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
            Interlocked.Exchange(ref _activeWakeSequenceId, 0);
        }
    }

    public static string? HandleRecognizedSpeech(string? rawResult)
    {
        var wakeId = Interlocked.Read(ref _activeWakeSequenceId);
        var transcript = ExtractRecognizedText(rawResult);
        if (string.IsNullOrWhiteSpace(transcript))
            return null;

        LogTimingMarker("transcript_emitted", wakeId, $"chars={transcript.Length}");
        LogEvent($"[vosk-speech] Transcript: {transcript}");

        var action = ResolveCommandAction(transcript);
        if (action == null)
            return null;

        LogEvent($"[oww-command] Resolved action: MatchPhrase='{action.MatchPhrase}', Token='{action.CommandToken}', ScriptRef='{action.ScriptReference ?? "<null>"}'");

        if (!string.IsNullOrWhiteSpace(action.AssistantLine))
        {
            LogEvent($"[oww-command] Assistant line: {action.AssistantLine}");
        }

        if (DispatchCommandAction(action, out var dispatchDetail))
        {
            LogEvent($"[oww-command] Dispatch succeeded: {dispatchDetail}");
            LogTimingMarker("dispatch_success", wakeId, $"token={action.CommandToken}");
            
            // Queue animation script execution if one is referenced  
            if (!string.IsNullOrWhiteSpace(action.ScriptReference))
            {
                LogEvent($"[oww-command] Queuing animation script execution: {action.ScriptReference}");
                System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    try
                    {
                        TryExecuteAnimationScriptSync(action.ScriptReference);
                    }
                    catch (Exception ex)
                    {
                        LogEvent($"[oww-command] Exception in animation script: {ex.GetType().Name}: {ex.Message}");
                    }
                }, null);
            }
            else
            {
                LogEvent($"[oww-command] No script reference set for this command");
            }
        }
        else
        {
            LogEvent($"[oww-command] Dispatch unavailable: {dispatchDetail}");
            LogTimingMarker("dispatch_failed", wakeId, $"token={action.CommandToken}");
        }

        return action.AssistantLine;
    }

    public static CommandAction? ResolveCommandAction(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return null;

        var commandText = NormalizeCommandText(transcript!);
        if (string.IsNullOrWhiteSpace(commandText))
            return null;

        var knownCommands = KnownCommands.Value;
        if (knownCommands.Count == 0)
            return null;

        var candidatePhrases = knownCommands
            .Select(entry => entry.MatchPhrase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidatePhrases.Length == 0)
            return null;

        var match = FuzzyMatcher.FindClosestMatch(
            commandText,
            candidatePhrases,
            _settings?.FuzzyMatchMinConfidence ?? 0.80f,
            LogEvent);

        if (match == null || string.IsNullOrWhiteSpace(match.MatchedCommand))
            return null;

        var entry = knownCommands.FirstOrDefault(c =>
            string.Equals(c.MatchPhrase, match.MatchedCommand, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
            return null;

        LogEvent($"[oww-command] command='{entry.MatchPhrase}', token='{entry.CommandToken}', confidence={match.Confidence:P1}");

        CommandResponses.TryGetValue(entry.MatchPhrase, out var response);

        return new CommandAction(
            transcript!,
            entry.MatchPhrase,
            entry.DispatchPhrase,
            entry.CommandToken,
            match.Confidence,
            entry.ScriptReference,
            response);
    }

    public static string? ResolveCommandResponse(string? transcript)
    {
        return ResolveCommandAction(transcript)?.AssistantLine;
    }

    private static bool DispatchCommandAction(CommandAction action, out string detail)
    {
        LogEvent($"[oww-dispatch] Starting dispatch for command token: '{action.CommandToken}'");
        LogEvent($"[oww-dispatch] Dispatchers will be tried in order: {string.Join(", ", CommandDispatchers.Select(d => d.Name))}");

        // Try all dispatchers (don't stop on first success) so animations and batch files both execute
        var results = new List<string>();
        var anySucceeded = false;
        
        for (int i = 0; i < CommandDispatchers.Length; i++)
        {
            var dispatcher = CommandDispatchers[i];
            LogEvent($"[oww-dispatch] Trying dispatcher [{i+1}/{CommandDispatchers.Length}]: {dispatcher.Name}");
            
            if (dispatcher.TryDispatch(action, out var dispatchDetail))
            {
                anySucceeded = true;
                results.Add($"[{dispatcher.Name}] {dispatchDetail}");
                LogEvent($"[oww-dispatch-success] Dispatcher '{dispatcher.Name}' succeeded: {dispatchDetail}");
            }
            else
            {
                results.Add($"[{dispatcher.Name}] skipped: {dispatchDetail}");
                LogEvent($"[oww-dispatch-skip] Dispatcher '{dispatcher.Name}' skipped: {dispatchDetail}");
            }
        }

        detail = anySucceeded
            ? $"Command executed: {string.Join("; ", results.Where(r => !r.Contains("skipped")))}"
            : "No dispatcher could execute the command.";

        LogEvent($"[oww-dispatch-final] Result: {(anySucceeded ? "SUCCESS" : "FAILED")} - {detail}");
        return anySucceeded;
    }

    private static void StartTestCommandQueueLoopIfEnabled()
    {
        var queuePath = Environment.GetEnvironmentVariable("PAICOM_TEST_COMMAND_QUEUE_FILE");
        if (string.IsNullOrWhiteSpace(queuePath))
            return;

        lock (_testCommandQueueLock)
        {
            if (_testCommandQueueRunning)
                return;

            _testCommandQueuePath = queuePath;
            _testCommandQueueOffset = 0;
            _testCommandQueueDispatchCount = 0;
            _testCommandQueueRunning = true;
            _testCommandQueueThread = new Thread(TestCommandQueueLoop)
            {
                IsBackground = true,
                Name = "OWW-TestCommandQueue"
            };
            _testCommandQueueThread.Start();
        }

        LogEvent($"[oww-test-ipc] Enabled command queue at '{queuePath}'.");
    }

    private static void StopTestCommandQueueLoop()
    {
        Thread? worker = null;
        lock (_testCommandQueueLock)
        {
            if (!_testCommandQueueRunning)
                return;

            _testCommandQueueRunning = false;
            worker = _testCommandQueueThread;
            _testCommandQueueThread = null;
        }

        try
        {
            worker?.Join(500);
        }
        catch
        {
            // Best-effort background shutdown.
        }
    }

    private static void TestCommandQueueLoop()
    {
        while (true)
        {
            lock (_testCommandQueueLock)
            {
                if (!_testCommandQueueRunning)
                    return;
            }

            try
            {
                DrainQueuedTestCommands();
            }
            catch (Exception ex)
            {
                LogEvent($"[oww-test-ipc] Queue loop error: {ex.GetType().Name}: {ex.Message}");
            }

            Thread.Sleep(120);
        }
    }

    private static void DrainQueuedTestCommands()
    {
        var path = _testCommandQueuePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        List<string> lines = new();
        var newOffset = _testCommandQueueOffset;

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (_testCommandQueueOffset > fs.Length)
                _testCommandQueueOffset = 0;

            fs.Seek(_testCommandQueueOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, true, 4096);
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }

            newOffset = fs.Length;
        }

        if (lines.Count == 0)
            return;

        _testCommandQueueOffset = newOffset;

        foreach (var rawLine in lines)
        {
            var phrase = rawLine;
            var tabIndex = rawLine.IndexOf('\t');
            if (tabIndex >= 0 && tabIndex + 1 < rawLine.Length)
                phrase = rawLine.Substring(tabIndex + 1).Trim();

            if (string.IsNullOrWhiteSpace(phrase))
                continue;

            DispatchQueuedTestPhrase(phrase);
        }
    }

    private static void DispatchQueuedTestPhrase(string dispatchPhrase)
    {
        var action = ResolveCommandAction(dispatchPhrase);
        if (action == null)
        {
            var matchPhrase = dispatchPhrase;
            if (dispatchPhrase.StartsWith("hey paicom ", StringComparison.OrdinalIgnoreCase))
                matchPhrase = dispatchPhrase.Substring("hey paicom ".Length).Trim();

            action = new CommandAction(
                dispatchPhrase,
                matchPhrase,
                dispatchPhrase,
                "test-token",
                0.95f,
                null,
                null);
        }

        var ok = DispatchCommandAction(action, out var detail);
        _testCommandQueueDispatchCount++;
        LogEvent($"[oww-test-ipc] Dispatch #{_testCommandQueueDispatchCount}: {(ok ? "SUCCESS" : "FAILED")} phrase='{dispatchPhrase}' detail='{detail}'");
    }

    private static bool TryGetGameCommandHandler(out object? target, out MethodInfo? method, out string detail)
    {
        lock (_dispatcherLock)
        {
            if (_cachedGameHandlerTarget != null && _cachedGameHandlerMethod != null)
            {
                target = _cachedGameHandlerTarget;
                method = _cachedGameHandlerMethod;
                detail = $"Using cached handler {_cachedGameHandlerMethod.DeclaringType?.FullName}.{_cachedGameHandlerMethod.Name}.";
                LogEvent($"[oww-handler-cache] {detail}");
                return true;
            }
        }

        var appType = Type.GetType("System.Windows.Forms.Application, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", throwOnError: false);
        if (appType == null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "System.Windows.Forms")
                {
                    appType = asm.GetType("System.Windows.Forms.Application");
                    break;
                }
            }
        }
        
        if (appType == null)
        {
            target = null;
            method = null;
            detail = "System.Windows.Forms.Application is unavailable.";
            return false;
        }

        var openFormsProperty = appType.GetProperty("OpenForms", BindingFlags.Public | BindingFlags.Static);
        var openForms = openFormsProperty?.GetValue(null) as IEnumerable;
        if (openForms == null)
        {
            target = null;
            method = null;
            detail = "OpenForms collection is unavailable.";
            return false;
        }

        object? bestTarget = null;
        MethodInfo? bestMethod = null;
        var bestScore = int.MinValue;
        var forms = new List<object>();

        // Safely extract forms with a short timeout to prevent blocking during UI startup
        var enumTask = System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var form in openForms)
            {
                if (form != null) forms.Add(form);
            }
        });

        if (!enumTask.Wait(TimeSpan.FromSeconds(2)))
        {
            target = null;
            method = null;
            detail = "Timed out trying to enumerate OpenForms. UI might be blocked or starting up.";
            return false;
        }

        foreach (var form in forms)
        {
            var candidates = form.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m =>
                {
                    if (m.IsSpecialName)
                        return false;

                    var parameters = m.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
                });

            foreach (var candidate in candidates)
            {
                var score = ScoreGameHandlerCandidate(candidate);
                LogEvent($"[oww-handler-scan] Candidate: {candidate.Name}, Score: {score}");
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = form;
                    bestMethod = candidate;
                }
            }
        }

        if (bestTarget == null || bestMethod == null || bestScore < 4)
        {
            target = null;
            method = null;
            detail = $"No high-confidence in-process command handler discovered. Highest score was {bestScore} for {bestMethod?.Name ?? "none"}.";
            return false;
        }

        lock (_dispatcherLock)
        {
            _cachedGameHandlerTarget = bestTarget;
            _cachedGameHandlerMethod = bestMethod;
        }

        target = bestTarget;
        method = bestMethod;
        detail = $"Selected handler {bestMethod.DeclaringType?.FullName}.{bestMethod.Name} (score={bestScore}).";
        return true;
    }

    private static int ScoreGameHandlerCandidate(MethodInfo method)
    {
        var score = 0;
        if (method.ReturnType == typeof(void))
            score += 3;

        var name = method.Name;
        if (name.IndexOf("command", StringComparison.OrdinalIgnoreCase) >= 0)
            score += 10;
        if (name.IndexOf("dispatch", StringComparison.OrdinalIgnoreCase) >= 0)
            score += 10;
        if (name.IndexOf("speech", StringComparison.OrdinalIgnoreCase) >= 0)
            score += 6;
        if (name.IndexOf("recogn", StringComparison.OrdinalIgnoreCase) >= 0)
            score += 5;

        try
        {
            var ilSize = method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
            if (ilSize >= 32 && ilSize <= 4096)
                score += 4;
        }
        catch
        {
            // Reflection can throw for dynamic/protected methods; keep score as-is.
        }

        return score;
    }

    private static bool TryInvokeOnUiThread(object target, MethodInfo method, string phrase, out string detail)
    {
        var beginInvoke = target.GetType().GetMethod(
            "BeginInvoke",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[] { typeof(Delegate), typeof(object[]) },
            null);

        if (beginInvoke == null)
        {
            beginInvoke = target.GetType().GetMethod(
                "BeginInvoke",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Delegate) },
                null);
            
            if (beginInvoke == null)
            {
                detail = "UI thread marshalling unavailable; invoking directly.";
                return false;
            }
            
            Action invokeAction = () => method.Invoke(target, new object[] { phrase });
            beginInvoke.Invoke(target, new object[] { invokeAction });
        }
        else
        {
            Action<string> invokeAction = p => method.Invoke(target, new object[] { p });
            beginInvoke.Invoke(target, new object[] { invokeAction, new object[] { phrase } });
        }

        detail = $"Queued '{phrase}' via UI dispatcher {method.Name}.";
        return true;
    }

    private static bool TryDispatchViaSpeechEmulation(string phrase, out string detail)
    {
        foreach (var form in GetOpenFormsSnapshot())
        {
            if (TryInvokeSpeechEngineOnObjectGraph(form, phrase, out detail))
                return true;
        }

        detail = "No compatible speech engine instance found for emulation.";
        return false;
    }

    private static bool TryDispatchViaUiSimulation(string phrase, out string detail)
    {
        foreach (var form in GetOpenFormsSnapshot())
        {
            if (TrySimulateTextAndClick(form, phrase, out detail))
                return true;
        }

        detail = "No suitable text input/button path found for UI simulation.";
        return false;
    }

    private static bool TryInvokeSpeechEngineOnObjectGraph(object root, string phrase, out string detail)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == null || !visited.Add(current))
                continue;

            if (TryInvokeSpeechEngine(current, phrase, out detail))
                return true;

            foreach (var next in EnumerateChildObjects(current))
            {
                if (next != null)
                    queue.Enqueue(next);
            }
        }

        detail = "No speech engine object exposing EmulateRecognize* methods was found.";
        return false;
    }

    private static bool TryInvokeSpeechEngine(object candidate, string phrase, out string detail)
    {
        var methods = candidate.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var emulateAsync = methods.FirstOrDefault(m =>
            string.Equals(m.Name, "EmulateRecognizeAsync", StringComparison.Ordinal) &&
            m.GetParameters().Length == 1 &&
            m.GetParameters()[0].ParameterType == typeof(string));

        var emulateSync = methods.FirstOrDefault(m =>
            string.Equals(m.Name, "EmulateRecognize", StringComparison.Ordinal) &&
            m.GetParameters().Length == 1 &&
            m.GetParameters()[0].ParameterType == typeof(string));

        if (emulateAsync == null && emulateSync == null)
        {
            detail = "EmulateRecognize* methods were not found on candidate object.";
            return false;
        }

        try
        {
            TryInvokeIfExists(candidate, "RecognizeAsyncCancel");
            TryInvokeIfExists(candidate, "RecognizeAsyncStop");

            if (emulateAsync != null)
            {
                emulateAsync.Invoke(candidate, new object[] { phrase });
                TryInvokeIfExists(candidate, "RecognizeAsync");
                detail = $"Speech emulation dispatched via {candidate.GetType().FullName}.EmulateRecognizeAsync(\"{phrase}\").";
                return true;
            }

            emulateSync!.Invoke(candidate, new object[] { phrase });
            TryInvokeIfExists(candidate, "RecognizeAsync");
            detail = $"Speech emulation dispatched via {candidate.GetType().FullName}.EmulateRecognize(\"{phrase}\").";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"Speech emulation failed on {candidate.GetType().FullName}: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TrySimulateTextAndClick(object form, string phrase, out string detail)
    {
        var controlTree = EnumerateControlTree(form);
        var controls = controlTree?.ToArray() ?? Array.Empty<object>();
        if (controls.Length == 0)
        {
            detail = "No WinForms controls discovered for simulation.";
            return false;
        }

        var textCandidates = controls.Where(IsTextInputControl).ToArray();
        if (textCandidates.Length == 0)
        {
            detail = "No writable text-input controls found.";
            return false;
        }

        var textControl = textCandidates
            .OrderByDescending(ScoreInputControl)
            .First();

        if (!TrySetControlText(textControl, phrase, out detail))
            return false;

        var button = controls
            .Where(IsClickableButtonControl)
            .OrderByDescending(ScoreButtonControl)
            .FirstOrDefault();

        if (button != null && TryPerformClick(button, out detail))
        {
            detail = $"UI simulation dispatched phrase via {textControl.GetType().FullName} + {button.GetType().FullName}.";
            return true;
        }

        if (TryRaiseEnterOnControl(textControl, out detail))
        {
            detail = $"UI simulation dispatched phrase via Enter key on {textControl.GetType().FullName}.";
            return true;
        }

        return false;
    }

    private static IEnumerable<object> EnumerateControlTree(object root)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == null || !visited.Add(current))
                continue;

            yield return current;

            foreach (var child in EnumerateChildControls(current))
                queue.Enqueue(child);
        }
    }

    private static IEnumerable<object> EnumerateChildControls(object candidate)
    {
        var controlsProperty = candidate.GetType().GetProperty("Controls", BindingFlags.Instance | BindingFlags.Public);
        if (controlsProperty?.GetValue(candidate) is not IEnumerable controls)
            yield break;

        foreach (var control in controls)
        {
            if (control != null)
                yield return control;
        }
    }

    private static IEnumerable<object> EnumerateChildObjects(object candidate)
    {
        foreach (var control in EnumerateChildControls(candidate))
            yield return control;

        var fields = candidate.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var field in fields)
        {
            if (field.FieldType.IsPrimitive || field.FieldType == typeof(string))
                continue;

            object? value;
            try
            {
                value = field.GetValue(candidate);
            }
            catch
            {
                continue;
            }

            if (value != null)
                yield return value;
        }
    }

    private static bool IsTextInputControl(object control)
    {
        var type = control.GetType();
        var textProperty = type.GetProperty("Text", BindingFlags.Instance | BindingFlags.Public);
        if (textProperty == null || !textProperty.CanWrite || textProperty.PropertyType != typeof(string))
            return false;

        var typeName = type.Name;
        if (typeName.IndexOf("TextBox", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (typeName.IndexOf("RichTextBox", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        var readOnlyProperty = type.GetProperty("ReadOnly", BindingFlags.Instance | BindingFlags.Public);
        if (readOnlyProperty?.PropertyType == typeof(bool) && readOnlyProperty.GetValue(control) is bool isReadOnly && isReadOnly)
            return false;

        return typeName.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int ScoreInputControl(object control)
    {
        var score = 0;
        if (TryGetStringProperty(control, "Name", out var name))
        {
            if (name.IndexOf("command", StringComparison.OrdinalIgnoreCase) >= 0) score += 8;
            if (name.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0) score += 6;
            if (name.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0) score += 4;
        }

        if (TryGetStringProperty(control, "PlaceholderText", out var placeholder))
        {
            if (placeholder.IndexOf("command", StringComparison.OrdinalIgnoreCase) >= 0) score += 6;
            if (placeholder.IndexOf("say", StringComparison.OrdinalIgnoreCase) >= 0) score += 4;
        }

        return score;
    }

    private static bool IsClickableButtonControl(object control)
    {
        var type = control.GetType();
        if (type.Name.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return type.GetMethod("PerformClick", BindingFlags.Instance | BindingFlags.Public) != null;
    }

    private static int ScoreButtonControl(object control)
    {
        var score = 0;
        if (TryGetStringProperty(control, "Name", out var name))
        {
            if (name.IndexOf("send", StringComparison.OrdinalIgnoreCase) >= 0) score += 8;
            if (name.IndexOf("submit", StringComparison.OrdinalIgnoreCase) >= 0) score += 8;
            if (name.IndexOf("command", StringComparison.OrdinalIgnoreCase) >= 0) score += 6;
            if (name.IndexOf("enter", StringComparison.OrdinalIgnoreCase) >= 0) score += 4;
            if (name.IndexOf("button", StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
        }

        if (TryGetStringProperty(control, "Text", out var text))
        {
            if (text.IndexOf("send", StringComparison.OrdinalIgnoreCase) >= 0) score += 8;
            if (text.IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0) score += 4;
            if (text.IndexOf("run", StringComparison.OrdinalIgnoreCase) >= 0) score += 4;
            if (text.IndexOf("enter", StringComparison.OrdinalIgnoreCase) >= 0) score += 4;
        }

        return score;
    }

    private static bool TrySetControlText(object control, string text, out string detail)
    {
        try
        {
            var textProperty = control.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public);
            if (textProperty == null || !textProperty.CanWrite)
            {
                detail = "Candidate text control does not expose writable Text property.";
                return false;
            }

            textProperty.SetValue(control, text);
            detail = $"Set UI text on {control.GetType().FullName}.";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"Setting UI text failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static void StartPatreonFormattingFixLoop()
    {
        if (_patreonFormattingApplied)
            return;

        var watcherThread = new Thread(() =>
        {
            try
            {
                for (var attempt = 0; attempt < 120 && !_patreonFormattingApplied; attempt++)
                {
                    if (attempt % 10 == 0)
                    {
                        var forms = GetOpenFormsSnapshot();
                        LogEvent($"[patreon-format] Startup scan attempt {attempt + 1}/120; openForms={forms.Length}.");
                    }

                    if (TryFixPatreonControlFormatting())
                    {
                        _patreonFormattingApplied = true;
                        LogEvent("[patreon-format] Applied readable foreground color to Patreon control.");
                        return;
                    }

                    System.Threading.Thread.Sleep(500);
                }

                if (!_patreonFormattingApplied)
                    LogEvent("[patreon-format] Patreon control was not found during the startup retry window.");
            }
            catch (Exception ex)
            {
                LogEvent($"[patreon-format] Fix loop failed: {ex.GetType().Name}: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "PatreonFormattingFixLoop"
        };

        watcherThread.Start();
    }

    private static bool TryFixPatreonControlFormatting(object? preferredRoot = null)
    {
        var forms = preferredRoot != null ? new[] { preferredRoot } : GetOpenFormsSnapshot();
        if (forms.Length == 0)
            return false;

        foreach (var form in forms)
        {
            if (form == null)
                continue;

            var applied = false;
            TryInvokeOnUiThread(form, () =>
            {
                applied = TryApplyPatreonFormattingToControlTree(form);
            });

            if (applied)
                return true;
        }

        return false;
    }

    private static bool TryApplyPatreonFormattingToControlTree(object root)
    {
        var controls = EnumerateControlTree(root).ToArray();
        if (!controls.Any(IsPatreonPopupSurface))
            return false;

        var target = controls.FirstOrDefault(IsPatreonRichTextTarget) ??
                     controls.FirstOrDefault(IsPatreonTextTarget);

        if (target == null)
            return false;

        if (TrySetReadableControlFormatting(target, out var detail))
        {
            LogEvent($"[patreon-format] {detail}");
            return true;
        }

        return false;
    }

    private static bool IsPatreonPopupSurface(object control)
    {
        var typeName = control.GetType().Name;
        var hasPatreonName = TryGetStringProperty(control, "Name", out var name) &&
                                                         (name.IndexOf("patreon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                            name.IndexOf("help", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                            name.IndexOf("richtextbox1", StringComparison.OrdinalIgnoreCase) >= 0);

        var hasPatreonText = TryGetStringProperty(control, "Text", out var text) &&
                                                        (text.IndexOf("patreon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                         text.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                         text.IndexOf("donator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                         text.IndexOf("support my software", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                         text.IndexOf("huge thanks", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                         text.IndexOf("check out my patreon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                         text.IndexOf("help:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                         text.IndexOf("news:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                         text.IndexOf("ver:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                         text.IndexOf("pai.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                         text.IndexOf("paicom", StringComparison.OrdinalIgnoreCase) >= 0);

        return hasPatreonName || hasPatreonText ||
               typeName.IndexOf("RichTextBox", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsPatreonRichTextTarget(object control)
    {
        var typeName = control.GetType().Name;
        if (typeName.IndexOf("RichTextBox", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        return TryGetStringProperty(control, "Name", out var name) &&
               (name.IndexOf("richtextbox1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("help", StringComparison.OrdinalIgnoreCase) >= 0) ||
               TryGetStringProperty(control, "Text", out var text) &&
               (text.IndexOf("help:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("patreon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("huge thanks", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("check out my patreon", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool IsPatreonTextTarget(object control)
    {
        var typeName = control.GetType().Name;
        if (typeName.IndexOf("TextBox", StringComparison.OrdinalIgnoreCase) < 0 &&
            typeName.IndexOf("Label", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (!TryGetStringProperty(control, "Text", out var text))
            text = string.Empty;

        if (!TryGetStringProperty(control, "Name", out var name))
            name = string.Empty;

        return name.IndexOf("patreon", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("help", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("richtextbox1", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("patreon", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("help:", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("news:", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("ver:", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("support my software", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("huge thanks", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("check out my patreon", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool TrySetReadableControlFormatting(object control, out string detail)
    {
        if (control.GetType().Name.IndexOf("RichTextBox", StringComparison.OrdinalIgnoreCase) >= 0 &&
            TrySetRichTextReadableFormatting(control, out detail))
        {
            return true;
        }

        return TrySetControlForeColor(control, out detail);
    }

    private static bool TrySetRichTextReadableFormatting(object control, out string detail)
    {
        detail = string.Empty;

        try
        {
            var textLength = 0;
            if (TryGetStringProperty(control, "Text", out var text))
                textLength = text.Length;

            var selectMethod = control.GetType().GetMethod("Select", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int), typeof(int) }, null);
            if (selectMethod != null)
            {
                selectMethod.Invoke(control, new object[] { 0, textLength });
            }

            var colorType = TryResolveRuntimeType(
                "System.Drawing.Color",
                "System.Drawing.Color, System.Drawing.Common",
                "System.Drawing.Color, System.Drawing");
            if (colorType == null)
            {
                detail = "System.Drawing.Color type not available for RichTextBox formatting.";
                return false;
            }

            var whiteProperty = colorType.GetProperty("White", BindingFlags.Public | BindingFlags.Static);
            if (whiteProperty == null)
            {
                detail = "System.Drawing.Color.White not available for RichTextBox formatting.";
                return false;
            }

            var selectionColorProperty = control.GetType().GetProperty("SelectionColor", BindingFlags.Instance | BindingFlags.Public);
            var foreColorProperty = control.GetType().GetProperty("ForeColor", BindingFlags.Instance | BindingFlags.Public);
            var colorValue = whiteProperty.GetValue(null);
            var operations = new List<string>();

            if (colorValue != null && selectionColorProperty != null && selectionColorProperty.CanWrite)
            {
                selectionColorProperty.SetValue(control, colorValue);
                operations.Add("SelectionColor=White");
            }

            if (colorValue != null && foreColorProperty != null && foreColorProperty.CanWrite)
            {
                foreColorProperty.SetValue(control, colorValue);
                operations.Add("ForeColor=White");
            }

            if (selectMethod != null)
                selectMethod.Invoke(control, new object[] { 0, 0 });

            if (operations.Count == 0)
            {
                detail = $"{control.GetType().FullName} does not expose writable RichTextBox color properties.";
                return false;
            }

            var controlName = TryGetStringProperty(control, "Name", out var name) ? name : control.GetType().FullName;
            var controlText = TryGetStringProperty(control, "Text", out var controlTextValue) ? controlTextValue : string.Empty;
            detail = $"Set {string.Join(", ", operations)} on {control.GetType().FullName} ('{controlName}', text='{TrimDiagnosticText(controlText)}').";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"Setting Patreon RichTextBox formatting failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TrySetControlForeColor(object control, out string detail)
    {
        detail = string.Empty;

        try
        {
            var colorType = TryResolveRuntimeType(
                "System.Drawing.Color",
                "System.Drawing.Color, System.Drawing.Common",
                "System.Drawing.Color, System.Drawing");
            if (colorType == null)
            {
                detail = "System.Drawing.Color type not available.";
                return false;
            }

            var whiteProperty = colorType.GetProperty("White", BindingFlags.Public | BindingFlags.Static);
            if (whiteProperty == null)
            {
                detail = "System.Drawing.Color.White not available.";
                return false;
            }

            var foreColorProperty = control.GetType().GetProperty("ForeColor", BindingFlags.Instance | BindingFlags.Public);
            if (foreColorProperty == null || !foreColorProperty.CanWrite)
            {
                detail = $"{control.GetType().FullName} does not expose a writable ForeColor property.";
                return false;
            }

            var colorValue = whiteProperty.GetValue(null);
            if (colorValue == null)
            {
                detail = "System.Drawing.Color.White returned null.";
                return false;
            }

            foreColorProperty.SetValue(control, colorValue);
            var controlName = TryGetStringProperty(control, "Name", out var name) ? name : control.GetType().FullName;
            var controlText = TryGetStringProperty(control, "Text", out var text) ? text : string.Empty;
            detail = $"Set ForeColor=White on {control.GetType().FullName} ('{controlName}', text='{TrimDiagnosticText(controlText)}').";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"Setting Patreon control ForeColor failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TryPerformClick(object control, out string detail)
    {
        try
        {
            var performClick = control.GetType().GetMethod("PerformClick", BindingFlags.Instance | BindingFlags.Public);
            if (performClick == null)
            {
                detail = "PerformClick is unavailable on selected control.";
                return false;
            }

            performClick.Invoke(control, Array.Empty<object>());
            detail = $"Performed click on {control.GetType().FullName}.";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"PerformClick failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TryRaiseEnterOnControl(object control, out string detail)
    {
        try
        {
            var focusMethod = control.GetType().GetMethod("Focus", BindingFlags.Instance | BindingFlags.Public);
            focusMethod?.Invoke(control, Array.Empty<object>());

            var keyEventArgsType = Type.GetType("System.Windows.Forms.KeyEventArgs, System.Windows.Forms", throwOnError: false);
            var keysType = Type.GetType("System.Windows.Forms.Keys, System.Windows.Forms", throwOnError: false);
            if (keyEventArgsType == null || keysType == null)
            {
                detail = "System.Windows.Forms KeyEventArgs/Keys unavailable for Enter simulation.";
                return false;
            }

            var enterValue = Enum.Parse(keysType, "Enter", ignoreCase: true);
            var ctor = keyEventArgsType.GetConstructor(new[] { typeof(int) });
            if (ctor == null)
            {
                detail = "KeyEventArgs constructor not found.";
                return false;
            }

            var args = ctor.Invoke(new object[] { (int)enterValue });
            var onKeyDown = control.GetType().GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
            if (onKeyDown == null)
            {
                detail = "OnKeyDown not available for Enter simulation.";
                return false;
            }

            onKeyDown.Invoke(control, new[] { args });
            detail = $"Raised Enter key on {control.GetType().FullName}.";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"Enter key simulation failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TryGetStringProperty(object target, string propertyName, out string value)
    {
        value = string.Empty;
        try
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property?.PropertyType != typeof(string))
                return false;

            value = property.GetValue(target) as string ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryInvokeIfExists(object target, string methodName)
    {
        try
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            method?.Invoke(target, Array.Empty<object>());
        }
        catch
        {
            // Best-effort compatibility call.
        }
    }

    private static object[] GetOpenFormsSnapshot()
    {
        try
        {
            var appType = Type.GetType("System.Windows.Forms.Application, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", throwOnError: false);
            if (appType == null)
            {
                appType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Where(a => string.Equals(a.GetName().Name, "System.Windows.Forms", StringComparison.Ordinal))
                    .Select(a => a.GetType("System.Windows.Forms.Application"))
                    .FirstOrDefault(t => t != null);
            }

            if (appType == null)
                return Array.Empty<object>();

            var openFormsProperty = appType.GetProperty("OpenForms", BindingFlags.Public | BindingFlags.Static);
            var openForms = openFormsProperty?.GetValue(null) as IEnumerable;
            if (openForms == null)
                return Array.Empty<object>();

            return openForms.Cast<object>().Where(f => f != null).ToArray();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static IReadOnlyList<CommandManifestEntry> LoadKnownCommands()
    {
        var manifestPath = FindCommandManifestPath();
        if (manifestPath == null)
        {
            LogEvent("[oww-command] Command manifest not found; fuzzy matching limited to built-in responses.");
            return CreateFallbackCommandManifest();
        }

        try
        {
            var commandsByPhrase = new Dictionary<string, CommandManifestEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in System.IO.File.ReadLines(manifestPath))
            {
                if (ParseCommandLine(line) is CommandManifestEntry entry)
                    commandsByPhrase[entry.MatchPhrase] = entry;
            }

            foreach (var pair in CommandResponses)
            {
                if (!commandsByPhrase.ContainsKey(pair.Key))
                    commandsByPhrase[pair.Key] = new CommandManifestEntry(pair.Key, pair.Key, pair.Key, null);
            }

            LogEvent($"[oww-command] Loaded {commandsByPhrase.Count} commands from {manifestPath}");
            EnsureRuntimeDiagnosticStarted($"commands-loaded:{commandsByPhrase.Count}");
            return commandsByPhrase.Values.ToArray();
        }
        catch (Exception ex)
        {
            LogEvent($"[oww-command] Failed to load command manifest '{manifestPath}': {ex}");
            return CreateFallbackCommandManifest();
        }
    }

    private static IReadOnlyList<CommandManifestEntry> CreateFallbackCommandManifest()
    {
        return CommandResponses.Keys
            .Select(responseKey => new CommandManifestEntry(responseKey, responseKey, responseKey, null))
            .ToArray();
    }

    private static CommandManifestEntry? ParseCommandLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            return null;

        var commandText = trimmed;
        string? scriptReference = null;

        var parenOpen = commandText.LastIndexOf('(');
        var parenClose = commandText.LastIndexOf(')');
        if (parenOpen > 0 && parenClose > parenOpen)
        {
            scriptReference = commandText.Substring(parenOpen + 1, parenClose - parenOpen - 1).Trim();
            commandText = commandText.Substring(0, parenOpen).Trim();

            if (!TryNormalizeCommandToken(scriptReference, out _, out var reason))
            {
                LogEvent($"[oww-command] Ignoring manifest entry with unsafe script reference '{scriptReference}': {reason}");
                return null;
            }
        }

        var normalizedPhrase = NormalizeCommandText(commandText);
        if (string.IsNullOrWhiteSpace(normalizedPhrase))
            return null;

        var commandToken = normalizedPhrase;
        if (!string.IsNullOrWhiteSpace(scriptReference))
        {
            if (!TryNormalizeCommandToken(scriptReference!, out commandToken, out var reason))
            {
                LogEvent($"[oww-command] Ignoring manifest entry with unsafe command token '{scriptReference}': {reason}");
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(commandToken))
            commandToken = normalizedPhrase;

        return new CommandManifestEntry(normalizedPhrase, commandText, commandToken, scriptReference);
    }

    private static string? ResolveCommandRootDirectory()
    {
        var manifestPath = FindCommandManifestPath();
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            var baseDirectories = new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                System.IO.Directory.GetCurrentDirectory()
            };

            foreach (var root in baseDirectories)
            {
                var current = new System.IO.DirectoryInfo(root!);
                while (current != null)
                {
                    var fullName = current.FullName;
                    if (System.IO.Directory.Exists(System.IO.Path.Combine(fullName, "custom-commands")))
                        return fullName;
                        
                    var legacyPath = System.IO.Path.Combine(fullName, "PAIcom_Player_Folder");
                    if (System.IO.Directory.Exists(System.IO.Path.Combine(legacyPath, "custom-commands")))
                        return legacyPath;

                    current = current.Parent;
                }
            }
            return null;
        }

        var customCommandsDirectory = System.IO.Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrWhiteSpace(customCommandsDirectory))
            return null;

        return System.IO.Path.GetDirectoryName(customCommandsDirectory);
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

        var text = ExtractJsonStringField(trimmed, "\"text\"");
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        var partial = ExtractJsonStringField(trimmed, "\"partial\"");
        if (!string.IsNullOrWhiteSpace(partial))
            return partial;

        return null;
    }

    private static string? ExtractJsonStringField(string json, string key)
    {
        var keyIndex = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
            return null;

        var colonIndex = json.IndexOf(':', keyIndex);
        if (colonIndex < 0)
            return null;

        var start = colonIndex + 1;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;

        if (start >= json.Length)
            return null;

        if (json[start] == '"')
            start++;

        var builder = new StringBuilder();
        var escaping = false;
        for (var i = start; i < json.Length; i++)
        {
            var current = json[i];
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

        var value = builder.ToString().Trim();
        return value.Length == 0 ? null : value;
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

    private static void EnsureRuntimeDiagnosticStarted(string trigger)
    {
        if (!IsRuntimeDiagnosticEnabled())
            return;

        lock (_runtimeDiagnosticLock)
        {
            if (_runtimeDiagnosticStarted)
                return;

            _runtimeDiagnosticStarted = true;
            _runtimeDiagnosticCompleted = false;
            _runtimeDiagnosticStartedUtc = DateTime.UtcNow;
            _runtimeDiagnosticLastRawFlushUtc = DateTime.MinValue;
            _runtimeDiagnosticLastSummaryWriteUtc = DateTime.MinValue;
            _runtimeDiagnosticPendingLines.Clear();
            _runtimeDiagnosticSeenEntries.Clear();
            _runtimeDiagnosticScannedTypes.Clear();
            _runtimeDiagnosticCategoryCounts.Clear();
            _runtimeDiagnosticLikelyScores.Clear();

            var root = ResolveCommandRootDirectory() ?? AppDomain.CurrentDomain.BaseDirectory;
            var dir = Path.Combine(root, "diagnostics");
            Directory.CreateDirectory(dir);

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            _runtimeDiagnosticRawPath = Path.Combine(dir, $"runtime-diagnostics-raw-{stamp}.log");
            _runtimeDiagnosticSummaryPath = Path.Combine(dir, $"runtime-diagnostics-summary-{stamp}.log");

            File.WriteAllText(_runtimeDiagnosticRawPath, $"# Runtime diagnostics raw log\n# start={DateTime.UtcNow:O}\n# trigger={trigger}\n");
            File.WriteAllText(_runtimeDiagnosticSummaryPath, $"# Runtime diagnostics summary\n# start={DateTime.UtcNow:O}\n# trigger={trigger}\n");
        }

        LogEvent($"[oww-runtime-diag] Diagnostic mode enabled. Raw log: {_runtimeDiagnosticRawPath}");
        LogEvent($"[oww-runtime-diag] Summary log: {_runtimeDiagnosticSummaryPath}");

        _ = System.Threading.Tasks.Task.Run(RunRuntimeDiagnosticSnapshotLoop);
    }

    private static bool IsRuntimeDiagnosticEnabled()
    {
        var value = Environment.GetEnvironmentVariable("PAICOM_RUNTIME_DIAGNOSTIC_MODE");
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetRuntimeDiagnosticDurationSeconds()
    {
        var value = Environment.GetEnvironmentVariable("PAICOM_RUNTIME_DIAGNOSTIC_DURATION_SECONDS");
        if (int.TryParse(value, out var seconds) && seconds > 0)
            return Math.Min(seconds, 1800);

        return 180;
    }

    private static void RunRuntimeDiagnosticSnapshotLoop()
    {
        try
        {
            var duration = TimeSpan.FromSeconds(GetRuntimeDiagnosticDurationSeconds());
            var stopAt = DateTime.UtcNow.Add(duration);
            var iteration = 0;

            while (DateTime.UtcNow < stopAt)
            {
                iteration++;
                CollectRuntimeDiagnosticSnapshot(iteration);
                FlushRuntimeDiagnosticRaw(force: false);
                WriteRuntimeDiagnosticSummary(force: false);
                System.Threading.Thread.Sleep(RuntimeDiagnosticSnapshotIntervalMs);
            }
        }
        catch (Exception ex)
        {
            LogEvent($"[oww-runtime-diag] Snapshot loop failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            FlushRuntimeDiagnosticRaw(force: true);
            WriteRuntimeDiagnosticSummary(force: true);
            LogRuntimeDiagnosticCompletionNotice();
        }
    }

    private static void CollectRuntimeDiagnosticSnapshot(int iteration)
    {
        var forms = GetOpenFormsSnapshot();
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        QueueRuntimeDiagnosticLine("snapshot", $"iteration:{iteration}", $"forms={forms.Length};assemblies={assemblies.Length}");

        foreach (var asm in assemblies)
        {
            var name = asm.GetName().Name ?? "<unknown>";
            QueueRuntimeDiagnosticLine("assembly", name, asm.FullName ?? string.Empty);
        }

        foreach (var form in forms)
        {
            QueueRuntimeDiagnosticObjectGraph(form);

            foreach (var control in EnumerateControlTree(form).Take(800))
            {
                var controlType = control.GetType();
                QueueRuntimeDiagnosticLine("control-type", controlType.FullName ?? controlType.Name, string.Empty);

                var controlKey = $"{controlType.FullName ?? controlType.Name}#{RuntimeHelpers.GetHashCode(control)}";
                var controlName = TryGetStringProperty(control, "Name", out var name) ? name : string.Empty;
                var controlText = TryGetStringProperty(control, "Text", out var text) ? text : string.Empty;
                var visible = TryGetBoolProperty(control, "Visible", out var isVisible) ? isVisible.ToString() : "?";
                var enabled = TryGetBoolProperty(control, "Enabled", out var isEnabled) ? isEnabled.ToString() : "?";

                QueueRuntimeDiagnosticLine(
                    "control",
                    controlKey,
                    $"name='{TrimDiagnosticText(controlName)}';text='{TrimDiagnosticText(controlText)}';visible={visible};enabled={enabled}");

                QueueRuntimeDiagnosticTypeMethods(controlType);
                QueueRuntimeDiagnosticFields(control);
            }
        }
    }

    private static void QueueRuntimeDiagnosticObjectGraph(object root)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        queue.Enqueue(root);
        var nodes = 0;

        while (queue.Count > 0 && nodes < 1000)
        {
            var current = queue.Dequeue();
            if (current == null || !visited.Add(current))
                continue;

            nodes++;
            var type = current.GetType();
            var key = $"{type.FullName ?? type.Name}#{RuntimeHelpers.GetHashCode(current)}";
            QueueRuntimeDiagnosticLine("object", key, string.Empty);
            QueueRuntimeDiagnosticLine("object-type", type.FullName ?? type.Name, string.Empty);

            QueueRuntimeDiagnosticTypeMethods(type);
            QueueRuntimeDiagnosticFields(current);

            foreach (var child in EnumerateChildObjects(current).Take(48))
            {
                if (child != null)
                    queue.Enqueue(child);
            }
        }
    }

    private static void QueueRuntimeDiagnosticTypeMethods(Type type)
    {
        var typeName = type.FullName ?? type.Name;
        if (!_runtimeDiagnosticScannedTypes.Add(typeName))
            return;

        MethodInfo[] methods;
        try
        {
            methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch
        {
            return;
        }

        foreach (var method in methods.Take(600))
        {
            var parameters = method.GetParameters();
            var signature = string.Join(",", parameters.Select(p => p.ParameterType.Name));
            var methodKey = $"{typeName}.{method.Name}";
            QueueRuntimeDiagnosticLine("method", methodKey, $"returns={method.ReturnType.Name};params=({signature});static={method.IsStatic}");
        }
    }

    private static void QueueRuntimeDiagnosticFields(object target)
    {
        FieldInfo[] fields;
        try
        {
            fields = target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch
        {
            return;
        }

        foreach (var field in fields.Take(120))
        {
            var fieldKey = $"{target.GetType().FullName}.{field.Name}";
            var detail = $"fieldType={field.FieldType.FullName ?? field.FieldType.Name}";

            try
            {
                var value = field.GetValue(target);
                if (value != null)
                {
                    detail += $";valueType={value.GetType().FullName ?? value.GetType().Name}";
                }
            }
            catch
            {
                // Ignore field read failures in diagnostics.
            }

            QueueRuntimeDiagnosticLine("field", fieldKey, detail);
        }
    }

    private static void QueueRuntimeDiagnosticLine(string category, string key, string detail)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var entry = $"{category}|{key}|{detail}";
        lock (_runtimeDiagnosticLock)
        {
            if (!_runtimeDiagnosticStarted || _runtimeDiagnosticCompleted)
                return;

            if (!_runtimeDiagnosticSeenEntries.Add(entry))
                return;

            _runtimeDiagnosticPendingLines.Add($"{DateTime.UtcNow:O} {entry}");
            if (!_runtimeDiagnosticCategoryCounts.ContainsKey(category))
                _runtimeDiagnosticCategoryCounts[category] = 1;
            else
                _runtimeDiagnosticCategoryCounts[category]++;

            var score = ScoreDiagnosticEntry(entry);
            if (score > 0)
                _runtimeDiagnosticLikelyScores[entry] = score;
        }
    }

    private static int ScoreDiagnosticEntry(string entry)
    {
        var lower = entry.ToLowerInvariant();
        var score = 0;
        foreach (var keyword in RuntimeDiagnosticLikelyKeywords)
        {
            if (lower.IndexOf(keyword, StringComparison.Ordinal) >= 0)
                score += 1;
        }

        return score;
    }

    private static void FlushRuntimeDiagnosticRaw(bool force)
    {
        List<string>? batch = null;
        string? rawPath;

        lock (_runtimeDiagnosticLock)
        {
            if (!_runtimeDiagnosticStarted || string.IsNullOrWhiteSpace(_runtimeDiagnosticRawPath))
                return;

            if (!force)
            {
                var elapsedMs = (DateTime.UtcNow - _runtimeDiagnosticLastRawFlushUtc).TotalMilliseconds;
                if (elapsedMs < RuntimeDiagnosticRawFlushMs && _runtimeDiagnosticPendingLines.Count < RuntimeDiagnosticMaxLinesPerFlush)
                    return;
            }

            if (_runtimeDiagnosticPendingLines.Count == 0)
                return;

            var take = force
                ? _runtimeDiagnosticPendingLines.Count
                : Math.Min(RuntimeDiagnosticMaxLinesPerFlush, _runtimeDiagnosticPendingLines.Count);

            batch = _runtimeDiagnosticPendingLines.Take(take).ToList();
            _runtimeDiagnosticPendingLines.RemoveRange(0, take);
            _runtimeDiagnosticLastRawFlushUtc = DateTime.UtcNow;
            rawPath = _runtimeDiagnosticRawPath;
        }

        if (batch == null || batch.Count == 0 || string.IsNullOrWhiteSpace(rawPath))
            return;

        try
        {
            File.AppendAllLines(rawPath, batch);
        }
        catch
        {
            // Avoid impacting runtime behavior if diagnostics file write fails.
        }

        if (force)
        {
            FlushRuntimeDiagnosticRaw(force: true);
        }
    }

    private static void WriteRuntimeDiagnosticSummary(bool force)
    {
        string? summaryPath;
        DateTime startedUtc;
        Dictionary<string, int> categoryCounts;
        Dictionary<string, int> likelyScores;
        int uniqueCount;

        lock (_runtimeDiagnosticLock)
        {
            if (!_runtimeDiagnosticStarted || string.IsNullOrWhiteSpace(_runtimeDiagnosticSummaryPath))
                return;

            if (!force &&
                (DateTime.UtcNow - _runtimeDiagnosticLastSummaryWriteUtc).TotalMilliseconds < RuntimeDiagnosticSummaryWriteMs)
            {
                return;
            }

            summaryPath = _runtimeDiagnosticSummaryPath;
            startedUtc = _runtimeDiagnosticStartedUtc;
            uniqueCount = _runtimeDiagnosticSeenEntries.Count;
            categoryCounts = new Dictionary<string, int>(_runtimeDiagnosticCategoryCounts, StringComparer.OrdinalIgnoreCase);
            likelyScores = new Dictionary<string, int>(_runtimeDiagnosticLikelyScores, StringComparer.Ordinal);
            _runtimeDiagnosticLastSummaryWriteUtc = DateTime.UtcNow;
        }

        if (string.IsNullOrWhiteSpace(summaryPath))
            return;

        var lines = new List<string>
        {
            $"# Runtime diagnostics summary",
            $"generated={DateTime.UtcNow:O}",
            $"started={startedUtc:O}",
            $"unique_entries={uniqueCount}",
            string.Empty,
            "[counts-by-category]"
        };

        foreach (var pair in categoryCounts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add($"{pair.Key}: {pair.Value}");

        lines.Add(string.Empty);
        lines.Add("[likely-objects-top-200]");
        foreach (var candidate in likelyScores
                     .OrderByDescending(p => p.Value)
                     .ThenBy(p => p.Key, StringComparer.Ordinal)
                     .Take(200))
        {
            lines.Add($"score={candidate.Value} | {candidate.Key}");
        }

        try
        {
            File.WriteAllLines(summaryPath, lines);
        }
        catch
        {
            // Avoid impacting runtime behavior if summary write fails.
        }
    }

    private static void LogRuntimeDiagnosticCompletionNotice()
    {
        string? rawPath;
        string? summaryPath;

        lock (_runtimeDiagnosticLock)
        {
            if (!_runtimeDiagnosticStarted || _runtimeDiagnosticCompleted)
                return;

            _runtimeDiagnosticCompleted = true;
            rawPath = _runtimeDiagnosticRawPath;
            summaryPath = _runtimeDiagnosticSummaryPath;
        }

        LogEvent($"[oww-runtime-diag] Diagnostics complete. Raw diagnostics located in: {rawPath}");
        LogEvent($"[oww-runtime-diag] Summarized diagnostics located in: {summaryPath}");
    }

    private static bool TryGetBoolProperty(object target, string propertyName, out bool value)
    {
        value = false;
        try
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property?.PropertyType != typeof(bool))
                return false;

            value = (bool)(property.GetValue(target) ?? false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string TrimDiagnosticText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 120 ? normalized : normalized.Substring(0, 120) + "...";
    }

    private static void TryExecuteAnimationScriptSync(string scriptReference)
    {
        LogEvent($"[animation-script] TryExecuteAnimationScriptSync called with: {scriptReference}");
        
        try
        {
            var manifestPath = FindCommandManifestPath();
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                LogEvent("[animation-script-error] Command manifest path not found");
                return;
            }

            var rootDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(manifestPath));
            if (string.IsNullOrWhiteSpace(rootDir))
            {
                LogEvent("[animation-script-error] Root directory not resolved");
                return;
            }

            var scriptPath = System.IO.Path.Combine(rootDir, "animations", scriptReference);
            if (!System.IO.File.Exists(scriptPath))
            {
                LogEvent($"[animation-script-error] Script file not found: {scriptPath}");
                return;
            }

            var animationDir = System.IO.Path.GetDirectoryName(scriptPath);
            LogEvent($"[animation-script] Loading script from: {scriptPath}");
            var lines = System.IO.File.ReadAllLines(scriptPath);
            
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                var parts = trimmed.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                var command = parts[0].ToUpperInvariant();
                
                switch (command)
                {
                    case "HIDE_ALL":
                        TryHideAllFrames();
                        LogEvent("[animation-script-action] HIDE_ALL");
                        break;
                    
                    case "SHOW":
                        if (parts.Length >= 2 && int.TryParse(parts[1], out var frameNum))
                        {
                            TryShowFrame(animationDir, frameNum);
                            LogEvent($"[animation-script-action] SHOW {frameNum}");
                        }
                        break;
                    
                    case "HIDE":
                        if (parts.Length >= 2 && int.TryParse(parts[1], out var hideNum))
                        {
                            TryHideFrame(hideNum);
                            LogEvent($"[animation-script-action] HIDE {hideNum}");
                        }
                        break;
                    
                    case "WAIT":
                        if (parts.Length >= 2 && int.TryParse(parts[1], out var delayMs))
                        {
                            LogEvent($"[animation-script-action] WAIT {delayMs}ms");
                            System.Threading.Thread.Sleep(delayMs);
                        }
                        break;
                    
                    case "PLAY_AUDIO":
                        if (parts.Length >= 2)
                        {
                            var audioFile = string.Join(" ", parts, 1, parts.Length - 1);
                            TryPlayAudio(animationDir, audioFile);
                            LogEvent($"[animation-script-action] PLAY_AUDIO {audioFile}");
                        }
                        break;
                    
                    case "OPEN_URL":
                        if (parts.Length >= 2)
                        {
                            var url = string.Join(" ", parts, 1, parts.Length - 1);
                            LogEvent($"[animation-script-action] OPEN_URL {url}");
                            try
                            {
                                var psi = new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = url,
                                    UseShellExecute = true
                                };
                                System.Diagnostics.Process.Start(psi);
                            }
                            catch (Exception ex)
                            {
                                LogEvent($"[animation-script-error] Failed to open URL: {ex.Message}");
                            }
                        }
                        break;
                    
                    default:
                        LogEvent($"[animation-script] Unknown command: {command}");
                        break;
                }
            }

            LogEvent($"[animation-script] Script execution completed: {scriptReference}");
        }
        catch (Exception ex)
        {
            LogEvent($"[animation-script-error] Exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryShowFrame(string animationDir, int frameNum)
    {
        try
        {
            var framePath = System.IO.Path.Combine(animationDir, $"{frameNum}.png");
            if (!System.IO.File.Exists(framePath))
            {
                LogEvent($"[animation-script-error] Frame image not found: {framePath}");
                return;
            }

            var imageType = TryResolveRuntimeType(
                "System.Drawing.Image",
                "System.Drawing.Image, System.Drawing.Common",
                "System.Drawing.Image, System.Drawing");
            if (imageType == null)
            {
                LogEvent("[animation-script-error] Image type not found");
                return;
            }

            if (!TryLoadRuntimeImage(imageType, framePath, out var image, out var loadDetail))
            {
                LogEvent($"[animation-script-error] {loadDetail}");
                return;
            }

            if (image == null)
            {
                LogEvent($"[animation-script-error] Failed to load frame image: {framePath}");
                return;
            }

            TryDisplayImageInControl(image, frameNum, framePath);
        }
        catch (Exception ex)
        {
            LogEvent($"[animation-script-error] Error showing frame {frameNum}: {ex.Message}");
        }
    }

    private static void TryDisplayImageInControl(object image, int frameNum, string framePath)
    {
        try
        {
            // Get the main form from the application
            var mainForm = TryGetGameForm();
            if (mainForm == null)
            {
                LogEvent("[animation-script-error] Could not find main game form");
                return;
            }

            if (!TryInvokeOnUiThread(mainForm, () =>
            {
                if (TryFindPictureBoxControl(mainForm, out var pictureBox))
                {
                    var ctrlType = pictureBox!.GetType();
                    LogEvent($"[animation-script-diag] Using form: {DescribeControlState(mainForm)}");
                    LogEvent($"[animation-script-diag] Selected PictureBox: {DescribeControlState(pictureBox)}");
                    LogEvent($"[animation-script-diag] PictureBox chain: {DescribeControlChain(pictureBox)}");

                    var imageProperty = ctrlType.GetProperty("Image", BindingFlags.Instance | BindingFlags.Public);
                    if (imageProperty != null)
                    {
                        var currentImage = imageProperty.GetValue(pictureBox);
                        if (!ReferenceEquals(currentImage, image) && currentImage != null)
                        {
                            var disposeMethod = currentImage.GetType().GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                            if (disposeMethod != null)
                            {
                                try
                                {
                                    disposeMethod.Invoke(currentImage, null);
                                }
                                catch { }
                            }
                        }

                        imageProperty.SetValue(pictureBox, image);
                    }

                    var imageLocationProperty = ctrlType.GetProperty("ImageLocation", BindingFlags.Instance | BindingFlags.Public);
                    if (imageLocationProperty != null)
                    {
                        try
                        {
                            imageLocationProperty.SetValue(pictureBox, string.Empty);
                        }
                        catch { }
                    }

                    var backgroundImageProperty = ctrlType.GetProperty("BackgroundImage", BindingFlags.Instance | BindingFlags.Public);
                    if (backgroundImageProperty != null)
                    {
                        try
                        {
                            backgroundImageProperty.SetValue(pictureBox, null);
                        }
                        catch { }
                    }

                    var sizeModeProperty = ctrlType.GetProperty("SizeMode", BindingFlags.Instance | BindingFlags.Public);
                    if (sizeModeProperty != null)
                    {
                        try
                        {
                            var zoomValue = Enum.Parse(sizeModeProperty.PropertyType, "Zoom", ignoreCase: true);
                            sizeModeProperty.SetValue(pictureBox, zoomValue);
                        }
                        catch { }
                    }

                    var bringToFrontMethod = ctrlType.GetMethod("BringToFront", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    bringToFrontMethod?.Invoke(pictureBox, Array.Empty<object>());

                    var refreshMethod = ctrlType.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    refreshMethod?.Invoke(pictureBox, Array.Empty<object>());

                    var invalidateMethod = ctrlType.GetMethod("Invalidate", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    invalidateMethod?.Invoke(pictureBox, Array.Empty<object>());

                    var updateMethod = ctrlType.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    updateMethod?.Invoke(pictureBox, Array.Empty<object>());

                    var visibleProperty = ctrlType.GetProperty("Visible", BindingFlags.Instance | BindingFlags.Public);
                    if (visibleProperty != null)
                    {
                        visibleProperty.SetValue(pictureBox, true);
                    }

                    var loadMethod = ctrlType.GetMethod("Load", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                    if (loadMethod != null)
                    {
                        try
                        {
                            loadMethod.Invoke(pictureBox, new object[] { framePath });
                        }
                        catch { }
                    }

                    TryPromoteControlInParent(pictureBox);
                    TryRefreshControlLayout(pictureBox);

                    var nameProperty = ctrlType.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
                    var ctrlName = nameProperty?.GetValue(pictureBox)?.ToString() ?? "Unknown";
                    LogEvent($"[animation-script-action] Displayed frame {frameNum} in {ctrlName}");
                }
                else
                {
                    LogEvent("[animation-script-error] No PictureBox controls found to display frame");
                }
            }))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            LogEvent($"[animation-script-error] Error displaying image: {ex.Message}");
        }
    }

    private static void TryHideAllFrames()
    {
        try
        {
            var mainForm = TryGetGameForm();
            if (mainForm == null)
            {
                LogEvent("[animation-script-error] Could not find main game form for HIDE_ALL");
                return;
            }

            TryInvokeOnUiThread(mainForm, () =>
            {
                ApplyToPictureBoxControls(mainForm, ctrl =>
                {
                    var ctrlType = ctrl.GetType();

                    var visibleProperty = ctrlType.GetProperty("Visible", BindingFlags.Instance | BindingFlags.Public);
                    if (visibleProperty != null)
                    {
                        visibleProperty.SetValue(ctrl, false);
                    }

                    var imageProperty = ctrlType.GetProperty("Image", BindingFlags.Instance | BindingFlags.Public);
                    if (imageProperty != null)
                    {
                        var currentImage = imageProperty.GetValue(ctrl);
                        if (currentImage != null)
                        {
                            var disposeMethod = currentImage.GetType().GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                            if (disposeMethod != null)
                            {
                                try
                                {
                                    disposeMethod.Invoke(currentImage, null);
                                }
                                catch { }
                            }

                            imageProperty.SetValue(ctrl, null);
                        }
                    }

                    var imageLocationProperty = ctrlType.GetProperty("ImageLocation", BindingFlags.Instance | BindingFlags.Public);
                    if (imageLocationProperty != null)
                    {
                        try
                        {
                            imageLocationProperty.SetValue(ctrl, string.Empty);
                        }
                        catch { }
                    }

                    var backgroundImageProperty = ctrlType.GetProperty("BackgroundImage", BindingFlags.Instance | BindingFlags.Public);
                    if (backgroundImageProperty != null)
                    {
                        try
                        {
                            backgroundImageProperty.SetValue(ctrl, null);
                        }
                        catch { }
                    }

                    var refreshMethod = ctrlType.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    refreshMethod?.Invoke(ctrl, Array.Empty<object>());

                    TryRefreshControlLayout(ctrl);
                });
            });
        }
        catch (Exception ex)
        {
            LogEvent($"[animation-script-error] Error hiding all frames: {ex.Message}");
        }
    }

    private static void TryHideFrame(int frameNum)
    {
        try
        {
            var mainForm = TryGetGameForm();
            if (mainForm == null)
            {
                LogEvent("[animation-script-error] Could not find main game form for HIDE");
                return;
            }

            TryInvokeOnUiThread(mainForm, () =>
            {
                if (TryFindVisiblePictureBoxControl(mainForm, out var pictureBox))
                {
                    var ctrlType = pictureBox!.GetType();

                    var visibleProperty = ctrlType.GetProperty("Visible", BindingFlags.Instance | BindingFlags.Public);
                    if (visibleProperty != null)
                    {
                        visibleProperty.SetValue(pictureBox, false);

                        var imageProperty = ctrlType.GetProperty("Image", BindingFlags.Instance | BindingFlags.Public);
                        if (imageProperty != null)
                        {
                            var currentImage = imageProperty.GetValue(pictureBox);
                            if (currentImage != null)
                            {
                                var disposeMethod = currentImage.GetType().GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                                if (disposeMethod != null)
                                {
                                    try
                                    {
                                        disposeMethod.Invoke(currentImage, null);
                                    }
                                    catch { }
                                }
                                imageProperty.SetValue(pictureBox, null);
                            }
                        }

                        var imageLocationProperty = ctrlType.GetProperty("ImageLocation", BindingFlags.Instance | BindingFlags.Public);
                        if (imageLocationProperty != null)
                        {
                            try
                            {
                                imageLocationProperty.SetValue(pictureBox, string.Empty);
                            }
                            catch { }
                        }

                        var backgroundImageProperty = ctrlType.GetProperty("BackgroundImage", BindingFlags.Instance | BindingFlags.Public);
                        if (backgroundImageProperty != null)
                        {
                            try
                            {
                                backgroundImageProperty.SetValue(pictureBox, null);
                            }
                            catch { }
                        }

                        var refreshMethod = ctrlType.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                        refreshMethod?.Invoke(pictureBox, Array.Empty<object>());

                        TryRefreshControlLayout(pictureBox);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            LogEvent($"[animation-script-error] Error hiding frame {frameNum}: {ex.Message}");
        }
    }

    private static void TryPlayAudio(string animationDir, string audioFile)
    {
        try
        {
            var audioPath = ResolveAudioPath(animationDir, audioFile);
            if (audioPath == null)
            {
                LogEvent($"[animation-script-error] Audio file not found: {audioFile}");
                return;
            }

            // Get SoundPlayer via reflection
            var soundPlayerType = TryResolveRuntimeType(
                "System.Media.SoundPlayer",
                "System.Media.SoundPlayer, System.Windows.Extensions",
                "System.Media.SoundPlayer, System");
            if (soundPlayerType == null)
            {
                LogEvent("[animation-script-error] SoundPlayer type not found");
                return;
            }

            // Create SoundPlayer instance with path
            var player = System.Activator.CreateInstance(soundPlayerType, audioPath);
            if (player == null)
            {
                LogEvent("[animation-script-error] Could not create SoundPlayer");
                return;
            }

            // Root the player for the playback's duration: PlaySync blocks
            // until done, so the instance and its buffer stay reachable and
            // wine/mono GC cannot collect mid-playback (unrooted async Play
            // died silently under collection pressure; paired A/B validated
            // the fix, contention review cleared co-residency). Off-thread so
            // the animation sequence stays unblocked.
            var playMethod = soundPlayerType.GetMethod("Play");
            if (playMethod != null)
            {
                try
                {
                    var playSyncMethod = soundPlayerType.GetMethod("PlaySync");
                    var blocking = playSyncMethod ?? playMethod;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        // Never swallow: a silent worker is an undiagnosable
                        // silence on the bus (residual-1 class). Log everything.
                        try { blocking.Invoke(player, null); }
                        catch (Exception innerEx)
                        {
                            LogEvent($"[animation-script-error] Background playback failed: {innerEx.GetType().Name}: {innerEx.Message}");
                        }
                    });
                    LogEvent($"[animation-script-action] Audio playing: {audioFile}");
                }
                catch (Exception ex)
                {
                    LogEvent($"[animation-script-error] Failed to play audio: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogEvent($"[animation-script-error] Error with audio playback: {ex.Message}");
        }
    }

    private static object? TryGetGameForm()
    {
        try
        {
            // Get Application.OpenForms via reflection
            var applicationType = TryResolveRuntimeType(
                "System.Windows.Forms.Application",
                "System.Windows.Forms.Application, System.Windows.Forms");
            if (applicationType == null)
                return null;

            var openFormsProperty = applicationType.GetProperty("OpenForms");
            if (openFormsProperty == null)
                return null;

            var openForms = openFormsProperty.GetValue(null);
            if (openForms == null)
            {
                var activeFormProperty = applicationType.GetProperty("ActiveForm");
                return activeFormProperty?.GetValue(null);
            }

            // Get count property
            var countProperty = openForms.GetType().GetProperty("Count");
            if (countProperty == null)
            {
                var activeFormProperty = applicationType.GetProperty("ActiveForm");
                return activeFormProperty?.GetValue(null);
            }

            var count = (int?)countProperty.GetValue(openForms) ?? 0;
            if (count > 0)
            {
                object? firstForm = null;
                object? bestForm = null;
                var bestScore = int.MinValue;

                foreach (var form in openForms as System.Collections.IEnumerable ?? Array.Empty<object>())
                {
                    if (form == null)
                        continue;

                    if (firstForm == null)
                        firstForm = form;

                    if (TryFindPictureBoxControl(form, out var pictureBox))
                    {
                        var score = ScorePictureBoxControl(pictureBox!);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestForm = form;
                        }
                    }
                }

                if (bestForm != null)
                    return bestForm;

                // Get indexer
                var indexer = openForms.GetType().GetProperty("Item");
                if (indexer != null)
                {
                    var form = indexer.GetValue(openForms, new object[] { 0 });
                    if (form != null)
                        return form;
                }

                if (firstForm != null)
                    return firstForm;
            }

            var activeForm = applicationType.GetProperty("ActiveForm")?.GetValue(null);
            return activeForm;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryInvokeOnUiThread(object target, Action action)
    {
        try
        {
            var targetType = target.GetType();
            var invokeRequiredProperty = targetType.GetProperty("InvokeRequired");
            var invokeMethod = targetType.GetMethod("Invoke", new[] { typeof(Delegate) });

            if (invokeRequiredProperty != null && invokeMethod != null)
            {
                var invokeRequired = (bool?)(invokeRequiredProperty.GetValue(target)) ?? false;
                if (invokeRequired)
                {
                    invokeMethod.Invoke(target, new object[] { action });
                    return true;
                }
            }

            action();
            return true;
        }
        catch (Exception ex)
        {
            LogEvent($"[animation-script-error] Failed to invoke UI action: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryFindPictureBoxControl(object parent, out object? pictureBox)
    {
        return TryFindBestPictureBoxControl(parent, requireVisible: false, out pictureBox);
    }

    private static bool TryFindVisiblePictureBoxControl(object parent, out object? pictureBox)
    {
        return TryFindBestPictureBoxControl(parent, requireVisible: true, out pictureBox);
    }

    private static bool TryFindBestPictureBoxControl(object parent, bool requireVisible, out object? pictureBox)
    {
        pictureBox = null;

        var candidates = new List<object>();
        CollectPictureBoxControls(parent, candidates);
        if (candidates.Count == 0)
            return false;

        var bestScore = int.MinValue;
        foreach (var candidate in candidates)
        {
            var score = ScorePictureBoxControl(candidate, requireVisible);
            if (score > bestScore)
            {
                bestScore = score;
                pictureBox = candidate;
            }
        }

        return pictureBox != null;
    }

    private static void CollectPictureBoxControls(object parent, List<object> controls)
    {
        if (parent.GetType().Name == "PictureBox")
        {
            controls.Add(parent);
            return;
        }

        var controlsProperty = parent.GetType().GetProperty("Controls");
        if (controlsProperty == null)
            return;

        var childControls = controlsProperty.GetValue(parent);
        if (childControls is not System.Collections.IEnumerable enumerable)
            return;

        foreach (var ctrl in enumerable)
        {
            if (ctrl == null)
                continue;

            CollectPictureBoxControls(ctrl, controls);
        }
    }

    private static int ScorePictureBoxControl(object control, bool requireVisible = false)
    {
        var score = 0;

        if (TryGetBoolProperty(control, "Visible", out var visible) && visible)
            score += 10;
        else if (requireVisible)
            return int.MinValue;

        if (TryGetBoolProperty(control, "Enabled", out var enabled) && enabled)
            score += 2;

        if (TryGetPropertyValueText(control, "Bounds", out var boundsText) &&
            boundsText.IndexOf("Width=0", StringComparison.OrdinalIgnoreCase) < 0 &&
            boundsText.IndexOf("Height=0", StringComparison.OrdinalIgnoreCase) < 0)
        {
            score += 5;
        }

        if (TryGetPropertyValueText(control, "Location", out var locationText) &&
            locationText.IndexOf("X=0", StringComparison.OrdinalIgnoreCase) < 0 &&
            locationText.IndexOf("Y=0", StringComparison.OrdinalIgnoreCase) < 0)
        {
            score += 1;
        }

        if (TryGetStringProperty(control, "Name", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            if (name.IndexOf("picture", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 2;
            if (name.IndexOf("frame", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 2;
        }

        var parentProperty = control.GetType().GetProperty("Parent", BindingFlags.Instance | BindingFlags.Public);
        var parent = parentProperty?.GetValue(control);
        if (parent != null)
        {
            if (TryGetBoolProperty(parent, "Visible", out var parentVisible) && parentVisible)
                score += 4;

            if (TryGetStringProperty(parent, "Name", out var parentName) && !string.IsNullOrWhiteSpace(parentName))
            {
                if (parentName.IndexOf("panel", StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 1;
                if (parentName.IndexOf("picture", StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 1;
            }
        }

        return score;
    }

    private static bool TryGetPropertyValueText(object target, string propertyName, out string value)
    {
        value = string.Empty;
        try
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
                return false;

            var propertyValue = property.GetValue(target);
            if (propertyValue == null)
                return false;

            value = propertyValue.ToString() ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeControlState(object control)
    {
        var controlType = control.GetType();
        var typeName = controlType.Name;
        var controlName = TryGetStringProperty(control, "Name", out var name) ? name : string.Empty;
        var controlText = TryGetStringProperty(control, "Text", out var text) ? text : string.Empty;
        var visible = TryGetBoolProperty(control, "Visible", out var isVisible) ? isVisible.ToString() : "?";
        var enabled = TryGetBoolProperty(control, "Enabled", out var isEnabled) ? isEnabled.ToString() : "?";
        var bounds = TryGetPropertyValueText(control, "Bounds", out var boundsText) ? boundsText : "?";
        var location = TryGetPropertyValueText(control, "Location", out var locationText) ? locationText : "?";
        var size = TryGetPropertyValueText(control, "Size", out var sizeText) ? sizeText : "?";
        var parent = controlType.GetProperty("Parent", BindingFlags.Instance | BindingFlags.Public)?.GetValue(control);
        var parentName = parent != null && TryGetStringProperty(parent, "Name", out var parentControlName) ? parentControlName : string.Empty;
        var parentType = parent?.GetType().Name ?? string.Empty;

        return $"{typeName} name='{TrimDiagnosticText(controlName)}';text='{TrimDiagnosticText(controlText)}';visible={visible};enabled={enabled};bounds={bounds};location={location};size={size};parent={parentType}:{TrimDiagnosticText(parentName)}";
    }

    private static string DescribeControlChain(object control, int maxDepth = 6)
    {
        var chain = new List<string>();
        var current = control;
        var depth = 0;

        while (current != null && depth < maxDepth)
        {
            chain.Add(DescribeControlState(current));

            var parentProperty = current.GetType().GetProperty("Parent", BindingFlags.Instance | BindingFlags.Public);
            current = parentProperty?.GetValue(current);
            depth++;
        }

        return string.Join(" => ", chain);
    }

    private static void ApplyToPictureBoxControls(object parent, Action<object> action)
    {
        if (parent.GetType().Name == "PictureBox")
        {
            action(parent);
            return;
        }

        var controlsProperty = parent.GetType().GetProperty("Controls");
        if (controlsProperty == null)
            return;

        var controls = controlsProperty.GetValue(parent);
        if (controls is not System.Collections.IEnumerable enumerable)
            return;

        foreach (var ctrl in enumerable)
        {
            if (ctrl == null)
                continue;

            ApplyToPictureBoxControls(ctrl, action);
        }
    }

    private static string? ResolveAudioPath(string animationDir, string audioFile)
    {
        var normalizedAudioFile = audioFile.Replace('/', System.IO.Path.DirectorySeparatorChar);
        var fileName = System.IO.Path.GetFileName(normalizedAudioFile);
        var fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(normalizedAudioFile);
        var hasExtension = System.IO.Path.HasExtension(normalizedAudioFile);
        var relativeDirectory = System.IO.Path.GetDirectoryName(normalizedAudioFile);

        var searchRoots = new List<string> { animationDir };
        var parentDirectory = System.IO.Directory.GetParent(animationDir)?.FullName;
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            searchRoots.Add(parentDirectory);
            searchRoots.Add(System.IO.Path.Combine(parentDirectory, "audio"));
            searchRoots.Add(System.IO.Path.Combine(parentDirectory, "animations"));
            searchRoots.Add(System.IO.Path.Combine(parentDirectory, "animations", "audio"));
        }

        foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exactPath = System.IO.Path.Combine(root, normalizedAudioFile);
            if (System.IO.File.Exists(exactPath))
                return exactPath;

            if (!hasExtension)
            {
                var relativeCandidate = string.IsNullOrWhiteSpace(relativeDirectory)
                    ? fileNameWithoutExtension
                    : System.IO.Path.Combine(relativeDirectory, fileNameWithoutExtension);

                var relativeWavPath = System.IO.Path.Combine(root, relativeCandidate + ".wav");
                if (System.IO.File.Exists(relativeWavPath))
                    return relativeWavPath;

                var relativeMp3Path = System.IO.Path.Combine(root, relativeCandidate + ".mp3");
                if (System.IO.File.Exists(relativeMp3Path))
                    return relativeMp3Path;

                var baseWavPath = System.IO.Path.Combine(root, fileNameWithoutExtension + ".wav");
                if (System.IO.File.Exists(baseWavPath))
                    return baseWavPath;

                var baseMp3Path = System.IO.Path.Combine(root, fileNameWithoutExtension + ".mp3");
                if (System.IO.File.Exists(baseMp3Path))
                    return baseMp3Path;
            }
        }

        return null;
    }

    private static Type? TryResolveRuntimeType(params string[] typeNames)
    {
        foreach (var typeName in typeNames)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                continue;

            var resolvedType = Type.GetType(typeName, throwOnError: false);
            if (resolvedType != null)
                return resolvedType;

            var fullName = typeName.Split(',')[0].Trim();
            if (string.IsNullOrWhiteSpace(fullName))
                continue;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    resolvedType = assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
                    if (resolvedType != null)
                        return resolvedType;
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private static bool TryLoadRuntimeImage(Type imageType, string framePath, out object? image, out string detail)
    {
        image = null;

        try
        {
            var fromFileMethod = imageType.GetMethod(
                "FromFile",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            if (fromFileMethod != null)
            {
                image = fromFileMethod.Invoke(null, new object[] { framePath });
                detail = $"Loaded frame via Image.FromFile(string): {framePath}";
                return true;
            }

            var fromFileColorMethod = imageType.GetMethod(
                "FromFile",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(bool) },
                null);

            if (fromFileColorMethod != null)
            {
                image = fromFileColorMethod.Invoke(null, new object[] { framePath, false });
                detail = $"Loaded frame via Image.FromFile(string, bool): {framePath}";
                return true;
            }

            var fromStreamMethod = imageType.GetMethod(
                "FromStream",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Stream) },
                null);

            if (fromStreamMethod != null)
            {
                using var stream = System.IO.File.OpenRead(framePath);
                image = fromStreamMethod.Invoke(null, new object[] { stream });
                detail = $"Loaded frame via Image.FromStream(Stream): {framePath}";
                return true;
            }

            detail = "Image loading methods not found: FromFile(string), FromFile(string, bool), or FromStream(Stream).";
            return false;
        }
        catch (Exception ex)
        {
            detail = $"Failed to load frame image: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static void TryPromoteControlInParent(object control)
    {
        try
        {
            var controlType = control.GetType();
            var parentProperty = controlType.GetProperty("Parent", BindingFlags.Instance | BindingFlags.Public);
            var parent = parentProperty?.GetValue(control);
            if (parent == null)
                return;

            var parentType = parent.GetType();
            var controlsProperty = parentType.GetProperty("Controls", BindingFlags.Instance | BindingFlags.Public);
            var controls = controlsProperty?.GetValue(parent);
            if (controls == null)
                return;

            var setChildIndexMethod = controls.GetType().GetMethod(
                "SetChildIndex",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { controlType, typeof(int) },
                null);
            if (setChildIndexMethod != null)
            {
                try
                {
                    setChildIndexMethod.Invoke(controls, new object[] { control, 0 });
                }
                catch { }
            }

            var bringToFrontMethod = controls.GetType().GetMethod("BringToFront", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            bringToFrontMethod?.Invoke(controls, Array.Empty<object>());
        }
        catch
        {
        }
    }

    private static void TryRefreshControlLayout(object control)
    {
        try
        {
            var controlType = control.GetType();

            var performLayoutMethod = controlType.GetMethod("PerformLayout", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            performLayoutMethod?.Invoke(control, Array.Empty<object>());

            var suspendLayoutMethod = controlType.GetMethod("SuspendLayout", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            var resumeLayoutMethod = controlType.GetMethod("ResumeLayout", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null);
            if (suspendLayoutMethod != null && resumeLayoutMethod != null)
            {
                try
                {
                    suspendLayoutMethod.Invoke(control, Array.Empty<object>());
                    resumeLayoutMethod.Invoke(control, new object[] { true });
                }
                catch { }
            }

            var parentProperty = controlType.GetProperty("Parent", BindingFlags.Instance | BindingFlags.Public);
            var parent = parentProperty?.GetValue(control);
            if (parent != null)
            {
                var parentType = parent.GetType();

                var parentPerformLayout = parentType.GetMethod("PerformLayout", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                parentPerformLayout?.Invoke(parent, Array.Empty<object>());

                var parentRefresh = parentType.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                parentRefresh?.Invoke(parent, Array.Empty<object>());

                var parentInvalidate = parentType.GetMethod("Invalidate", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null)
                    ?? parentType.GetMethod("Invalidate", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (parentInvalidate != null)
                {
                    try
                    {
                        if (parentInvalidate.GetParameters().Length == 1)
                            parentInvalidate.Invoke(parent, new object[] { true });
                        else
                            parentInvalidate.Invoke(parent, Array.Empty<object>());
                    }
                    catch { }
                }

                var parentUpdate = parentType.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                parentUpdate?.Invoke(parent, Array.Empty<object>());
            }

            var applicationType = TryResolveRuntimeType(
                "System.Windows.Forms.Application",
                "System.Windows.Forms.Application, System.Windows.Forms");
            var doEventsMethod = applicationType?.GetMethod("DoEvents", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            doEventsMethod?.Invoke(null, Array.Empty<object>());
        }
        catch
        {
        }
    }

    /// <summary>
    /// Graceful shutdown (called on app exit or cleanup).
    /// Stops background inference, flushes queues.
    /// </summary>
    public static void Shutdown()
    {
        StopTestCommandQueueLoop();

        FlushRuntimeDiagnosticRaw(force: true);
        WriteRuntimeDiagnosticSummary(force: true);
        LogRuntimeDiagnosticCompletionNotice();

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

        lock (_dispatcherLock)
        {
            _cachedGameHandlerMethod = null;
            _cachedGameHandlerTarget = null;
        }

        lock (_commandManifestLock)
        {
            KnownCommands = new Lazy<IReadOnlyList<CommandManifestEntry>>(LoadKnownCommands, true);
        }
    }

    /// <summary>Internal: get current lock state (for testing).</summary>
    internal static bool IsLocked => _lockManager?.IsLocked ?? false;

    /// <summary>Internal: get initialization error (for diagnostics).</summary>
    internal static Exception? InitializationError => _initError;
}
