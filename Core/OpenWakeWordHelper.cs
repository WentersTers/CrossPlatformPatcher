using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CrossPlatformPatcher.Core.Audio;
using CrossPlatformPatcher.Core.Animation;

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
    private static Lazy<IAnimationDispatcher>? _lazyCrossPlatformAnimationDispatcher;
    private static Lazy<IAudioPlayer>? _lazyCrossPlatformAudioPlayer;
    private static Lazy<AnimationScriptExecutor>? _lazyAnimationScriptExecutor;
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
    private const int DefaultFallbackScriptTimeoutMs = 8000;

    // Sequential method testing (for diagnostics and compatibility testing)
    private static bool _sequentialMethodTestMode;
    private static string? _testCategoryFilter;
    private static int _testMethodCount;
    private static int _testMethodSuccessCount;
    private static int _testMethodFailureCount;
    private static readonly object _methodTestLock = new();

    // Live method testing (keeps game running, tests against live instance)
    private static bool _liveMethodTestMode;
    private static string? _liveTestLogPath;
    private static readonly object _liveTestLock = new();

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
    /// On Unix systems, ProcessFallback runs before Reflection to prefer native commands
    /// over trying to dispatch into the Wine-running game.
    /// </summary>
    private static ICommandDispatcher[] BuildCommandDispatcherPipeline()
    {
        var commonDispatchers = new ICommandDispatcher[]
        {
            new SpeechEmulationCommandDispatcher(),
            new UiSimulationCommandDispatcher(),
            new CrossPlatformAnimationCommandDispatcher(),
            new CrossPlatformAudioCommandDispatcher(),
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: try game reflection first, then fall back to processes
            return commonDispatchers.Concat(new ICommandDispatcher[]
            {
                new ReflectionCommandDispatcher(),
                new AnimationReflectionDispatcher(),
                new ProcessFallbackCommandDispatcher()
            }).ToArray();
        }
        else
        {
            // Unix (macOS/Linux): try native process commands BEFORE game reflection
            // This ensures commands like "show steam friends" or "open task manager"
            // execute natively instead of trying to call into the Wine-running game
            return commonDispatchers.Concat(new ICommandDispatcher[]
            {
                new ProcessFallbackCommandDispatcher(),
                new ReflectionCommandDispatcher(),
                new AnimationReflectionDispatcher(),
            }).ToArray();
        }
    }
    private static MethodInfo? _cachedGameHandlerMethod;
    private static object? _cachedGameHandlerTarget;
    private static MethodInfo? _cachedAnimationHandlerMethod;
    private static object? _cachedAnimationHandlerTarget;
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
    private const int SequentialCandidateDefaultLimit = 25;

    private sealed class ScoredMethodCandidate
    {
        public ScoredMethodCandidate(object? target, MethodInfo method, int score)
        {
            Target = target;
            Method = method;
            Score = score;
        }

        public object? Target { get; }
        public MethodInfo Method { get; }
        public int Score { get; }
    }

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
            if (IsSequentialCandidateTestingEnabled())
                return TryDispatchAcrossLikelyGameHandlers(action, out detail);

            if (!TryGetGameCommandHandler(out var target, out var method, out detail))
                return false;

            LogEvent($"[oww-dispatch-debug] Found handler: {method?.Name}, DispatchPhrase: '{action.DispatchPhrase}', MatchPhrase: '{action.MatchPhrase}'");
            
            try
            {
                // Try dispatching with the full dispatch phrase (including wake word)
                // Only try UI thread if we have a target object
                if (target != null && TryInvokeOnUiThread(target, method!, action.DispatchPhrase, out detail))
                {
                    LogEvent($"[oww-dispatch-result] UI thread invoke succeeded");
                    return true;
                }

                // Direct invocation (works for both instance and static methods)
                if (method!.IsStatic)
                {
                    method.Invoke(null, new object[] { action.DispatchPhrase });
                    detail = $"Invoked static {method.DeclaringType?.FullName}.{method.Name}(\"{action.DispatchPhrase}\") with dispatch phrase.";
                }
                else if (target != null)
                {
                    method.Invoke(target, new object[] { action.DispatchPhrase });
                    detail = $"Invoked {method.DeclaringType?.FullName}.{method.Name}(\"{action.DispatchPhrase}\") with dispatch phrase.";
                }
                else
                {
                    detail = "Unable to invoke handler: no target object for instance method and method is not static.";
                    return false;
                }
                
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
            // First, try to handle common commands natively on Unix systems
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (TryDispatchNativeCommand(action, out detail))
                    return true;
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
            var token = action.CommandToken.ToLowerInvariant();
            
            try
            {
                // Browser commands
                if (token.Contains("browser") || token.Contains("web") || token == "open the browser")
                {
                    return ExecuteNativeCommand("open-default-browser", out detail);
                }
                
                // Task Manager / Activity Monitor
                if (token.Contains("task") && (token.Contains("manager") || token.Contains("monitor")))
                {
                    return ExecuteNativeCommand("open-task-manager", out detail);
                }
                
                // Steam commands
                if (token.Contains("steam"))
                {
                    // Check for steam chat specifically
                    if (token.Contains("chat"))
                    {
                        return ExecuteNativeCommand("steam-chat", out detail);
                    }
                    
                    // Check for status-related commands (invisible, offline, hide status)
                    if (token.Contains("invisible") ||
                        token.Contains("offline") ||
                        (token.Contains("hide") && token.Contains("status")) ||
                        (token.Contains("appear") && token.Contains("offline")))
                    {
                        return ExecuteNativeCommand("steam-set-invisible", out detail);
                    }
                    if (token.Contains("online") ||
                        token.Contains("active") ||
                        (token.Contains("show") && token.Contains("status")))
                    {
                        return ExecuteNativeCommand("steam-set-online", out detail);
                    }
                    if (token.Contains("friend"))
                    {
                        return ExecuteNativeCommand("steam-friends", out detail);
                    }
                    if (token.Contains("library"))
                    {
                        return ExecuteNativeCommand("steam-library", out detail);
                    }
                    if (token.Contains("overlay"))
                    {
                        return ExecuteNativeCommand("steam-overlay", out detail);
                    }
                    // Generic steam command - open Steam
                    return ExecuteNativeCommand("steam-launch", out detail);
                }
                
                detail = "Not a native command token.";
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
                
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
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
                            Arguments = "steam://friends",
                            UseShellExecute = true
                        },
                        "steam-chat" => new ProcessStartInfo
                        {
                            FileName = "osascript",
                            Arguments = "-e 'tell application \"Steam\" to activate' -e 'delay 0.5' -e 'tell application \"System Events\" to tell process \"Steam\"' -e 'set frontmost to true' -e 'end tell' -e 'delay 0.3' -e 'tell application \"System Events\"' -e 'keystroke \"`\" using {control down, shift down}' -e 'delay 0.3' -e 'keystroke \"open steam://open/friends\"' -e 'key code 36' -e 'end tell' 2>/dev/null || open 'steam://open/friends'",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        "steam-library" => new ProcessStartInfo
                        {
                            FileName = "open",
                            Arguments = "steam://nav/main",
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
                            FileName = "osascript",
                            Arguments = "-e 'tell application \"Steam\" to activate' -e 'delay 0.5' -e 'tell application \"System Events\" to tell process \"Steam\"' -e 'set frontmost to true' -e 'end tell' -e 'delay 0.3' -e 'tell application \"System Events\"' -e 'keystroke \"`\" using {control down, shift down}' -e 'delay 0.3' -e 'keystroke \"friends status invisible\"' -e 'key code 36' -e 'end tell' 2>/dev/null || open 'steam://friends'",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        "steam-set-online" => new ProcessStartInfo
                        {
                            FileName = "osascript",
                            Arguments = "-e 'tell application \"Steam\" to activate' -e 'delay 0.5' -e 'tell application \"System Events\" to tell process \"Steam\"' -e 'set frontmost to true' -e 'end tell' -e 'delay 0.3' -e 'tell application \"System Events\"' -e 'keystroke \"`\" using {control down, shift down}' -e 'delay 0.3' -e 'keystroke \"friends status online\"' -e 'key code 36' -e 'end tell' 2>/dev/null || open 'steam://friends'",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        _ => null
                    };
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
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
                            Arguments = "steam://friends",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        "steam-chat" => new ProcessStartInfo
                        {
                            FileName = "bash",
                            Arguments = "-c \"(command -v steam >/dev/null 2>&1 && steam 'steam://open/friends' || xdg-open 'steam://open/friends') 2>/dev/null\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        },
                        "steam-library" => new ProcessStartInfo
                        {
                            FileName = "xdg-open",
                            Arguments = "steam://nav/main",
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

        /// <summary>
        /// Returns script file extensions in platform-specific priority order.
        /// Prefers native scripts for the current OS to avoid Wine/compatibility overhead.
        /// </summary>
        private static string[] GetPlatformSpecificScriptExtensions()
        {
            var isWine = IsRunningUnderWine();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !isWine)
            {
                // Windows: prefer batch files, then PS1, then fall back to .exe/.sh
                return new[] { ".bat", ".cmd", ".ps1", ".exe", ".sh", ".command" };
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || isWine)
            {
                // macOS: prefer .command (Finder double-click wrapper) and .sh (shell script)
                // Fall back to .exe/.bat only after Unix-native options exhausted
                return new[] { ".command", ".sh", ".exe", ".bat", ".cmd", ".ps1" };
            }

            // Linux: prefer .sh shell scripts, fall back to .exe/.bat with Wine
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

            var platformExtensions = GetPlatformSpecificScriptExtensions();

            foreach (var root in searchRoots)
            {
                var fullRoot = System.IO.Path.GetFullPath(root);
                var normalizedRoot = fullRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                var rootPrefix = normalizedRoot + System.IO.Path.DirectorySeparatorChar;
                foreach (var extension in platformExtensions)
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
                LogEvent($"[batch-converter-diag] TryStartScript called: path={scriptPath}, extension={extension}, isWindows={RuntimeInformation.IsOSPlatform(OSPlatform.Windows)}");
                
                ProcessStartInfo startInfo;

                if (string.Equals(extension, ".sh", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".command", StringComparison.OrdinalIgnoreCase))
                {
                    LogEvent($"[batch-converter-diag] Executing as shell script: {extension}");
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
                    // Check if we're actually on Windows or running under Wine on Unix
                    bool actuallyWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                    bool isWine = IsRunningUnderWine();
                    
                    LogEvent($"[batch-converter-diag] Batch file detected: actuallyWindows={actuallyWindows}, isWine={isWine}");
                    
                    if (actuallyWindows && !isWine)
                    {
                        LogEvent($"[batch-converter-diag] Executing batch directly on Windows");
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
                        // On Unix or Wine on Unix, translate the batch file to shell syntax and execute
                        LogEvent($"[batch-converter-diag] Translating batch file for Unix (Wine={isWine})");
                        if (!TryTranslateAndExecuteBatch(scriptPath, timeoutMs, out detail))
                        {
                            LogEvent($"[batch-converter-diag] Batch translation failed: {detail}");
                            return false;
                        }

                        LogEvent($"[batch-converter-diag] Batch translation succeeded");
                        return true;
                    }
                }
                else
                {
                    LogEvent($"[batch-converter-diag] Executing as generic file: {extension}");
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
                LogEvent($"[batch-converter-error] {detail}");
                return false;
            }
        }

        /// <summary>
        /// Detects if code is running under Wine/Whisky by checking for Wine environment variables
        /// or by attempting to invoke Unix commands.
        /// </summary>
        private static bool IsRunningUnderWine()
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
            {
                return true;
            }

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
            return currentDir.StartsWith("Z:\\", StringComparison.OrdinalIgnoreCase) ||
                   currentDir.StartsWith("Z:/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Translates a batch file to shell syntax and executes it on Unix.
        /// Supports development mode (PAICOM_BATCH_TO_SHELL_MODE=generate-and-test) to generate/validate .sh files.
        /// </summary>
        private static bool TryTranslateAndExecuteBatch(string batchFilePath, int timeoutMs, out string detail)
        {
            detail = "";

            try
            {
                // Read batch file content
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

                // Check if we're in generate-and-test mode
                var converterMode = Environment.GetEnvironmentVariable("PAICOM_BATCH_TO_SHELL_MODE") ?? "internal";
                var isGenerateAndTest = converterMode.Equals("generate-and-test", StringComparison.OrdinalIgnoreCase);

                // In generate-and-test mode, write the .sh file and validate it
                if (isGenerateAndTest)
                {
                    if (BatchFileTranslator.TryGenerateShellEquivalent(batchFilePath, translatedContent, out var generatedPath, LogEvent))
                    {
                        // Validate syntax
                        var (isValid, error) = BatchFileTranslator.ValidateShellSyntax(translatedContent);
                        if (!isValid)
                        {
                            LogEvent($"[batch-converter-warning] Generated shell script has syntax issues: {error}");
                        }
                        else
                        {
                            LogEvent($"[batch-converter] Shell equivalent validated successfully: {generatedPath}");
                        }
                    }
                }

                // Convert Wine path to Unix path if needed
                var unixWorkingDir = ConvertWinePathToUnix(System.IO.Path.GetDirectoryName(batchFilePath) ?? ".");
                LogEvent($"[batch-converter] Working directory: Wine='{System.IO.Path.GetDirectoryName(batchFilePath)}' -> Unix='{unixWorkingDir}'");
                
                // Execute the translated content via shell
                LogEvent($"[batch-converter] Executing translated command: {translatedContent}");
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
                    detail = $"Failed to start shell process for translated batch file: '{batchFilePath}'";
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
                    : $"Translated batch file executed with exit code {process.ExitCode}: '{batchFilePath}'";

                LogEvent($"[batch-converter] Executed translated batch: {batchFilePath} (exit code: {process.ExitCode})");
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                detail = $"Failed to translate and execute batch file '{batchFilePath}': {ex.GetType().Name}: {ex.Message}";
                LogEvent($"[batch-converter-error] {detail}");
                return false;
            }
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
                        process.Kill(entireProcessTree: true);
                    }
                    catch
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
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Converts a Wine path (e.g., Z:\Users\...) to a Unix path.
        /// Wine typically maps Z:\ to the actual filesystem root.
        /// </summary>
        private static string ConvertWinePathToUnix(string winePath)
        {
            if (string.IsNullOrEmpty(winePath))
                return ".";
            
            // Replace backslashes with forward slashes
            var unixPath = winePath.Replace('\\', '/');
            
            // If it starts with Z:/, remove it (Z: is Wine's root mapping)
            if (unixPath.StartsWith("Z:/", StringComparison.OrdinalIgnoreCase))
            {
                unixPath = unixPath.Substring(2); // Remove "Z:"
            }
            
            // If path doesn't exist or is just a drive letter, use current directory
            if (!System.IO.Directory.Exists(unixPath) && !System.IO.Directory.Exists(winePath))
            {
                LogEvent($"[batch-converter-diag] Path doesn't exist: {unixPath}, using '.'");
                return ".";
            }
            
            return unixPath;
        }

        private static int GetFallbackScriptTimeoutMs()
        {
            var configured = Environment.GetEnvironmentVariable("PAICOM_FALLBACK_SCRIPT_TIMEOUT_MS");
            if (!string.IsNullOrWhiteSpace(configured) && int.TryParse(configured, out var parsed))
                return parsed < 1000 ? 1000 : (parsed > 120000 ? 120000 : parsed);

            return DefaultFallbackScriptTimeoutMs;
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

        private static string QuoteArgument(string argument)
        {
            if (argument.IndexOf(' ') < 0 && argument.IndexOf('\t') < 0)
                return argument;

            return "\"" + argument.Replace("\"", "\\\"") + "\"";
        }
    }

    private sealed class AnimationReflectionDispatcher : ICommandDispatcher
    {
        public string Name => "game-animation-reflection";

        public bool TryDispatch(CommandAction action, out string detail)
        {
            if (IsSequentialCandidateTestingEnabled())
                return TryDispatchAcrossLikelyAnimationHandlers(action, out detail);

            if (!TryGetAnimationHandler(out var target, out var method, out detail))
                return false;

            var candidateArguments = BuildAnimationDispatchArguments(action);
            Exception? lastError = null;

            foreach (var argument in candidateArguments)
            {
                try
                {
                    if (target != null && TryInvokeOnUiThread(target, method!, argument, out detail))
                    {
                        detail = $"Animation handler queued via UI dispatcher using argument '{argument}'.";
                        return true;
                    }

                    if (method!.IsStatic)
                    {
                        method.Invoke(null, new object[] { argument });
                        detail = $"Invoked static animation handler {method.DeclaringType?.FullName}.{method.Name}(\"{argument}\").";
                        return true;
                    }

                    if (target != null)
                    {
                        method.Invoke(target, new object[] { argument });
                        detail = $"Invoked animation handler {method.DeclaringType?.FullName}.{method.Name}(\"{argument}\").";
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    LogEvent($"[oww-animation-dispatch] Candidate argument '{argument}' failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            detail = lastError == null
                ? "Animation handler found, but no invocation path succeeded."
                : $"Animation dispatch failed: {lastError.GetType().Name}: {lastError.Message}";
            return false;
        }
    }

    private sealed class CrossPlatformAnimationCommandDispatcher : ICommandDispatcher
    {
        public string Name => "cross-platform-animation";

        public bool TryDispatch(CommandAction action, out string detail)
        {
            try
            {
                // Get or initialize the dispatcher
                var dispatcher = GetCrossPlatformAnimationDispatcher();
                if (dispatcher == null)
                {
                    detail = "Cross-platform animation dispatcher is not available on this platform.";
                    return false;
                }

                // Extract delay from action if available
                int delayMs = ExtractDelayFromAction(action) ?? 50;
                
                LogEvent($"[oww-cross-platform-animation] Dispatching animation with {delayMs}ms delay");
                
                // Schedule animation frame
                var animationTask = dispatcher.AnimateFrame(delayMs);
                
                // Queue UI callbacks if needed
                if (!string.IsNullOrWhiteSpace(action.DispatchPhrase))
                {
                    dispatcher.TryQueueUiAction(() =>
                    {
                        LogEvent($"[oww-cross-platform-animation] Animation callback: {action.DispatchPhrase}");
                    }, out var queueError);
                    
                    if (!string.IsNullOrEmpty(queueError))
                        LogEvent($"[oww-cross-platform-animation] Queue error: {queueError}");
                }
                
                detail = $"Cross-platform animation dispatched with {delayMs}ms delay, state={dispatcher.GetState()}.";
                return true;
            }
            catch (Exception ex)
            {
                detail = $"Cross-platform animation dispatch failed: {ex.GetType().Name}: {ex.Message}";
                LogEvent($"[oww-cross-platform-animation-error] {detail}");
                return false;
            }
        }
    }

    private sealed class CrossPlatformAudioCommandDispatcher : ICommandDispatcher
    {
        public string Name => "cross-platform-audio";

        public bool TryDispatch(CommandAction action, out string detail)
        {
            try
            {
                // Get or initialize the audio player
                var audioPlayer = GetCrossPlatformAudioPlayer();
                if (audioPlayer == null)
                {
                    detail = "Cross-platform audio player is not available on this platform.";
                    return false;
                }

                // Try to load audio from the action dispatch phrase
                var resourcePath = action.DispatchPhrase ?? action.CommandToken ?? string.Empty;
                if (string.IsNullOrWhiteSpace(resourcePath))
                {
                    detail = "No audio resource path found in action.";
                    return false;
                }

                LogEvent($"[oww-cross-platform-audio] Loading audio from: {resourcePath}");
                
                if (!audioPlayer.TryLoadAudio(resourcePath, out var track, out var loadError))
                {
                    detail = $"Failed to load audio: {loadError}";
                    LogEvent($"[oww-cross-platform-audio-error] {detail}");
                    return false;
                }

                LogEvent($"[oww-cross-platform-audio] Loaded audio track, playing...");
                
                if (track == null)
                {
                    detail = "Failed to load audio track: track is null";
                    LogEvent($"[oww-cross-platform-audio-error] {detail}");
                    return false;
                }

                if (!audioPlayer.TryPlayAudio(track, out var playError))
                {
                    track?.Dispose();
                    detail = $"Failed to play audio: {playError}";
                    LogEvent($"[oww-cross-platform-audio-error] {detail}");
                    return false;
                }

                detail = $"Audio played successfully from {resourcePath}, state={audioPlayer.GetState()}.";
                return true;
            }
            catch (Exception ex)
            {
                detail = $"Cross-platform audio dispatch failed: {ex.GetType().Name}: {ex.Message}";
                LogEvent($"[oww-cross-platform-audio-error] {detail}");
                return false;
            }
        }
    }

    private static IAnimationDispatcher? GetCrossPlatformAnimationDispatcher()
    {
        _lazyCrossPlatformAnimationDispatcher ??= new Lazy<IAnimationDispatcher>(
            () => new CrossPlatformAnimationDispatcher(logger: LogEvent));
        return _lazyCrossPlatformAnimationDispatcher.Value;
    }

    private static IAudioPlayer? GetCrossPlatformAudioPlayer()
    {
        _lazyCrossPlatformAudioPlayer ??= new Lazy<IAudioPlayer>(
            () => new CrossPlatformAudioPlayer(logger: LogEvent));
        return _lazyCrossPlatformAudioPlayer.Value;
    }

    private static AnimationScriptExecutor? GetAnimationScriptExecutor()
    {
        _lazyAnimationScriptExecutor ??= new Lazy<AnimationScriptExecutor>(() =>
        {
            // Resolve animation and audio paths
            var baseDir = ResolveCommandRootDirectory();
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                LogEvent("[animation-script] Could not resolve base directory for animations");
                return null;
            }

            var animationsPath = System.IO.Path.Combine(baseDir, "animations");
            var audioPath = baseDir;  // Audio files in root

            LogEvent($"[animation-script-init] Animations path: {animationsPath}");
            LogEvent($"[animation-script-init] Audio path: {audioPath}");

            var executor = new AnimationScriptExecutor(
                animationsPath,
                audioPath,
                GetCrossPlatformAnimationDispatcher(),
                GetCrossPlatformAudioPlayer(),
                LogEvent);
            
            LogEvent("[animation-script-init] Animation script executor initialized");
            return executor;
        });
        
        var result = _lazyAnimationScriptExecutor.Value;
        if (result == null)
        {
            LogEvent("[animation-script] Animation script executor is null!");
        }
        return result;
    }

    private static int? ExtractDelayFromAction(CommandAction action)
    {
        if (action == null || string.IsNullOrWhiteSpace(action.CommandToken))
            return null;

        // Try to extract a number from the command token
        var token = action.CommandToken.Trim();
        if (int.TryParse(token, out var value) && value > 0)
            return value;
        
        return null;
    }

    private static bool IsSequentialCandidateTestingEnabled()
    {
        var value = Environment.GetEnvironmentVariable("PAICOM_TRY_ALL_CANDIDATE_METHODS");
        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Runtime-diagnostic sessions are explicitly test/investigation runs.
        // Default to sequential candidate testing there so likely handlers are
        // exercised without requiring extra launcher plumbing.
        return IsRuntimeDiagnosticEnabled();
    }

    private static int GetSequentialCandidateLimit()
    {
        var value = Environment.GetEnvironmentVariable("PAICOM_CANDIDATE_METHOD_LIMIT");
        if (int.TryParse(value, out var parsed) && parsed > 0)
            return Math.Min(parsed, 100);

        return SequentialCandidateDefaultLimit;
    }

    private static bool TryDispatchAcrossLikelyGameHandlers(CommandAction action, out string detail)
    {
        var limit = GetSequentialCandidateLimit();
        var candidates = GetRankedGameHandlerCandidates(limit);
        if (candidates.Length == 0)
        {
            detail = "No ranked game-handler candidates were discovered for sequential testing.";
            return false;
        }

        var successCount = 0;
        var firstSuccessDetail = string.Empty;

        foreach (var candidate in candidates)
        {
            if (TryInvokeStringHandler(candidate.Target, candidate.Method, action.DispatchPhrase, out var attemptDetail))
            {
                successCount++;
                if (string.IsNullOrEmpty(firstSuccessDetail))
                    firstSuccessDetail = attemptDetail;
                LogEvent($"[oww-sequential-handler] Success: {candidate.Method.DeclaringType?.FullName}.{candidate.Method.Name} (score={candidate.Score}) -> {attemptDetail}");
            }
            else
            {
                LogEvent($"[oww-sequential-handler] Failed: {candidate.Method.DeclaringType?.FullName}.{candidate.Method.Name} (score={candidate.Score}) -> {attemptDetail}");
            }
        }

        if (successCount > 0)
        {
            detail = $"Sequential game-handler test completed: {successCount}/{candidates.Length} candidates invoked successfully. First success: {firstSuccessDetail}";
            return true;
        }

        detail = $"Sequential game-handler test completed: 0/{candidates.Length} candidates succeeded.";
        return false;
    }

    private static bool TryDispatchAcrossLikelyAnimationHandlers(CommandAction action, out string detail)
    {
        var limit = GetSequentialCandidateLimit();
        var candidates = GetRankedAnimationHandlerCandidates(limit);
        if (candidates.Length == 0)
        {
            detail = "No ranked animation-handler candidates were discovered for sequential testing.";
            return false;
        }

        var successCount = 0;
        var firstSuccessDetail = string.Empty;
        var candidateArguments = BuildAnimationDispatchArguments(action);

        foreach (var candidate in candidates)
        {
            var invoked = false;
            foreach (var argument in candidateArguments)
            {
                if (TryInvokeStringHandler(candidate.Target, candidate.Method, argument, out var attemptDetail))
                {
                    successCount++;
                    invoked = true;
                    if (string.IsNullOrEmpty(firstSuccessDetail))
                        firstSuccessDetail = attemptDetail;
                    LogEvent($"[oww-sequential-animation] Success: {candidate.Method.DeclaringType?.FullName}.{candidate.Method.Name} (score={candidate.Score}) arg='{argument}' -> {attemptDetail}");
                    break;
                }
            }

            if (!invoked)
            {
                LogEvent($"[oww-sequential-animation] Failed: {candidate.Method.DeclaringType?.FullName}.{candidate.Method.Name} (score={candidate.Score})");
            }
        }

        if (successCount > 0)
        {
            detail = $"Sequential animation-handler test completed: {successCount}/{candidates.Length} candidates invoked successfully. First success: {firstSuccessDetail}";
            return true;
        }

        detail = $"Sequential animation-handler test completed: 0/{candidates.Length} candidates succeeded.";
        return false;
    }

    private static ScoredMethodCandidate[] GetRankedGameHandlerCandidates(int limit)
    {
        var candidates = CollectRankedStringMethodCandidates(ScoreGameHandlerCandidate);
        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Method.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(c => c.Method.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private static ScoredMethodCandidate[] GetRankedAnimationHandlerCandidates(int limit)
    {
        var candidates = CollectRankedStringMethodCandidates(ScoreAnimationHandlerCandidate);
        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Method.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(c => c.Method.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private static List<ScoredMethodCandidate> CollectRankedStringMethodCandidates(Func<MethodInfo, int> scorer)
    {
        var results = new List<ScoredMethodCandidate>();

        foreach (var form in GetOpenFormsSnapshot())
        {
            var methods = form.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m =>
                {
                    if (m.IsSpecialName)
                        return false;

                    var parameters = m.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
                });

            foreach (var method in methods)
                results.Add(new ScoredMethodCandidate(form, method, scorer(method)));
        }

        foreach (var asm in GetCandidateAssembliesForDiscovery())
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type == null || type.Name.StartsWith("<", StringComparison.Ordinal))
                    continue;

                var hasCompilerGenerated = type.GetCustomAttributes(typeof(CompilerGeneratedAttribute), false).Length > 0;
                if (hasCompilerGenerated)
                    continue;

                var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m =>
                    {
                        if (m.IsSpecialName)
                            return false;

                        var parameters = m.GetParameters();
                        return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
                    });

                foreach (var method in methods)
                {
                    object? target = null;
                    if (!method.IsStatic && !TryFindTargetInstance(method.DeclaringType, out target))
                        continue;

                    results.Add(new ScoredMethodCandidate(target, method, scorer(method)));
                }
            }
        }

        return results
            .GroupBy(c =>
            {
                var targetType = c.Target?.GetType().FullName ?? "<static>";
                return $"{c.Method.DeclaringType?.FullName}.{c.Method.Name}|{targetType}|{c.Method.IsStatic}";
            }, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .ToList();
    }

    private static bool TryInvokeStringHandler(object? target, MethodInfo method, string argument, out string detail)
    {
        try
        {
            if (target != null && TryInvokeOnUiThread(target, method, argument, out detail))
                return true;

            if (method.IsStatic)
            {
                method.Invoke(null, new object[] { argument });
                detail = $"Invoked static {method.DeclaringType?.FullName}.{method.Name}(\"{argument}\").";
                
                // Log method test result if testing is enabled
                if (_sequentialMethodTestMode && ShouldTestMethod(method))
                {
                    RecordMethodTestAttempt(method, true, detail);
                }
                
                return true;
            }

            if (target != null)
            {
                method.Invoke(target, new object[] { argument });
                detail = $"Invoked {method.DeclaringType?.FullName}.{method.Name}(\"{argument}\").";
                
                // Log method test result if testing is enabled
                if (_sequentialMethodTestMode && ShouldTestMethod(method))
                {
                    RecordMethodTestAttempt(method, true, detail);
                }
                
                return true;
            }

            detail = $"Skipping {method.DeclaringType?.FullName}.{method.Name}: no compatible target instance.";
            return false;
        }
        catch (Exception ex)
        {
            detail = $"Invoke failed for {method.DeclaringType?.FullName}.{method.Name}: {ex.GetType().Name}: {ex.Message}";
            
            // Log method test failure if testing is enabled
            if (_sequentialMethodTestMode && ShouldTestMethod(method))
            {
                RecordMethodTestAttempt(method, false, $"{ex.GetType().Name}: {ex.Message}");
            }
            
            return false;
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

        // Initialize live testing mode if enabled
        lock (_liveTestLock)
        {
            var liveTestVar = Environment.GetEnvironmentVariable("PAICOM_LIVE_METHOD_TEST");
            _liveMethodTestMode = string.Equals(liveTestVar, "1", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(liveTestVar, "true", StringComparison.OrdinalIgnoreCase);
            
            if (_liveMethodTestMode)
            {
                _liveTestLogPath = Environment.GetEnvironmentVariable("PAICOM_LIVE_TEST_LOG");
                LogEvent($"[livetest] Live method testing ENABLED");
                if (!string.IsNullOrEmpty(_liveTestLogPath))
                    LogEvent($"[livetest] Test log: {_liveTestLogPath}");
            }
        }

        // Initialize file-based command input if enabled
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
                                ExecuteAnimationScriptAsync(action.ScriptReference).ContinueWith(task =>
                                {
                                    if (task.IsFaulted)
                                    {
                                        LogEvent($"[fileinput] Animation script task faulted: {task.Exception?.InnerException?.Message}");
                                    }
                                });
                            }
                            catch (Exception ex)
                            {
                                LogEvent($"[fileinput] Exception queueing animation script: {ex.GetType().Name}: {ex.Message}");
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
    /// Get the category of a method for filtering in sequential testing.
    /// </summary>
    private static string GetMethodCategory(MethodInfo method)
    {
        var methodName = method.Name.ToLowerInvariant();
        
        if (methodName.Contains("paint") || methodName.Contains("render") || methodName.Contains("draw"))
            return "render-methods";
        if (methodName.Contains("audio") || methodName.Contains("sound"))
            return "audio-methods";
        if (methodName.Contains("animation"))
            return "animation-methods";
        if (methodName.Contains("click") || methodName.Contains("button"))
            return "button-methods";
        if (methodName.Contains("show") || methodName.Contains("hide") || methodName.Contains("visible"))
            return "visibility-methods";
        if (methodName.Contains("form") || methodName.Contains("window"))
            return "form-methods";
        if (methodName.Contains("text") || methodName.Contains("input") || methodName.Contains("textbox"))
            return "input-methods";
        if (methodName.Contains("event") || methodName.Contains("handler") && methodName.EndsWith("_"))
            return "event-handlers";
        
        return "other-methods";
    }

    /// <summary>
    /// Check if a method should be tested based on current category filter.
    /// </summary>
    private static bool ShouldTestMethod(MethodInfo method)
    {
        if (!_sequentialMethodTestMode || string.IsNullOrEmpty(_testCategoryFilter))
            return true;
        
        var methodCategory = GetMethodCategory(method);
        return methodCategory == _testCategoryFilter;
    }

    /// <summary>
    /// Record a method test attempt for diagnostics.
    /// </summary>
    private static void RecordMethodTestAttempt(MethodInfo method, bool success, string detail = "")
    {
        lock (_methodTestLock)
        {
            if (!_sequentialMethodTestMode)
                return;
            
            _testMethodCount++;
            if (success)
                _testMethodSuccessCount++;
            else
                _testMethodFailureCount++;
            
            var status = success ? "✓" : "✗";
            var category = GetMethodCategory(method);
            var sig = method.Name;
            if (sig.Length > 40)
                sig = sig.Substring(0, 37) + "...";
            
            LogEvent($"[methodtest] {status} [{category}] {method.DeclaringType?.Name}.{sig} {(success ? "SUCCESS" : "FAILED")}");
            
            if (!string.IsNullOrEmpty(detail))
                LogEvent($"[methodtest] Detail: {detail}");
        }

        // Also log for live testing
        RecordLiveTestAttempt(method, success, detail);
    }

    private static void RecordLiveTestAttempt(MethodInfo method, bool success, string detail = "")
    {
        lock (_liveTestLock)
        {
            if (!_liveMethodTestMode || string.IsNullOrEmpty(_liveTestLogPath))
                return;
            
            var status = success ? "✓" : "✗";
            var timestamp = DateTime.UtcNow.ToString("O");
            var typeName = method.DeclaringType?.Name ?? "Unknown";
            var methodName = method.Name;
            
            var logLine = $"[{timestamp}] {status} {typeName}.{methodName}";
            if (!string.IsNullOrEmpty(detail))
                logLine += $" - {detail}";
            
            logLine += "\n";
            
            try
            {
                // Append to live test log file (non-blocking, fire-and-forget)
                System.IO.File.AppendAllText(_liveTestLogPath, logLine);
            }
            catch
            {
                // Silently ignore file write errors to avoid breaking real execution
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

    private static void EnableUnhandledExceptionLogging()
    {
        var raw = Environment.GetEnvironmentVariable("PAICOM_LOG_UNHANDLED_EXCEPTIONS");
        if (string.IsNullOrWhiteSpace(raw))
            return;

        var normalized = raw.Trim().ToLowerInvariant();
        var enabled = normalized == "1" || normalized == "true" || normalized == "yes" || normalized == "on";
        if (!enabled)
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
            // Enumerate loaded native modules to help identify which native DLL may have caused the crash
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

        try
        {
            // If we hit an AccessViolation, attempt to write a full memory minidump for native analysis
            void TryWriteDump()
            {
                try
                {
                    var proc = System.Diagnostics.Process.GetCurrentProcess();
                    var diagDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics");
                    Directory.CreateDirectory(diagDir);
                    var dumpPath = Path.Combine(diagDir, $"native-crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.dmp");
                    using (var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var hProcess = proc.Handle;
                        var pid = (uint)proc.Id;
                        const uint MiniDumpWithFullMemory = 0x00000002;
                        var ok = MiniDumpWriteDump(hProcess, pid, fs.SafeFileHandle.DangerousGetHandle(), MiniDumpWithFullMemory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        LogEvent($"[oww-exception] dump_write_result={ok};dump_path={dumpPath}");
                    }
                }
                catch (Exception ex)
                {
                    try { LogEvent($"[oww-exception] dump_write_failed={ex.Message}"); } catch { }
                }
            }

            TryWriteDump();
        }
        catch
        {
            // Best-effort only
        }

        [System.Runtime.InteropServices.DllImport("Dbghelp.dll", SetLastError = true)]
        static extern bool MiniDumpWriteDump(IntPtr hProcess, uint ProcessId, IntPtr hFile, uint DumpType, IntPtr ExceptionParam, IntPtr UserStreamParam, IntPtr CallbackParam);
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
            LogEvent($"[oww-command] Command action: MatchPhrase='{action.MatchPhrase}', Token='{action.CommandToken}', ScriptRef='{action.ScriptReference ?? "<null>"}'");
            
            // Execute animation script if one is referenced
            if (!string.IsNullOrWhiteSpace(action.ScriptReference))
            {
                LogEvent($"[oww-command] Queuing animation script execution: {action.ScriptReference}");
                // Fire and forget on background thread
                System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    try
                    {
                        ExecuteAnimationScriptAsync(action.ScriptReference).ContinueWith(task =>
                        {
                            if (task.IsFaulted)
                            {
                                LogEvent($"[oww-command] Animation script task faulted: {task.Exception?.InnerException?.Message}");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        LogEvent($"[oww-command] Exception queueing animation script: {ex.GetType().Name}: {ex.Message}");
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
        }

        return action.AssistantLine;
    }

    /// <summary>
    /// Test harness: Directly dispatch a command action without voice recognition.
    /// For testing command handler discovery and invocation.
    /// </summary>
    internal static bool TestDispatchCommand(CommandAction action, out string detail)
    {
        LogEvent($"[oww-test] Dispatching test command: MatchPhrase='{action.MatchPhrase}', DispatchPhrase='{action.DispatchPhrase}'");
        return DispatchCommandAction(action, out detail);
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

    private static async System.Threading.Tasks.Task ExecuteAnimationScriptAsync(string scriptReference)
    {
        LogEvent($"[animation-script] ExecuteAnimationScriptAsync called with: {scriptReference}");
        
        var executor = GetAnimationScriptExecutor();
        if (executor == null)
        {
            LogEvent("[animation-script-error] Animation script executor is null, cannot execute");
            return;
        }

        try
        {
            LogEvent($"[animation-script] Starting async execution of {scriptReference}");
            var (success, detail) = await executor.ExecuteScriptAsync(scriptReference);
            LogEvent($"[animation-script] ExecuteScriptAsync returned: success={success}, detail={detail}");
            
            if (!success)
            {
                LogEvent($"[animation-script-error] Failed to execute {scriptReference}: {detail}");
            }
        }
        catch (Exception ex)
        {
            LogEvent($"[animation-script-error] Exception in ExecuteAnimationScriptAsync: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool DispatchCommandAction(CommandAction action, out string detail)
    {
        var results = new List<string>();
        var anySucceeded = false;

        LogEvent($"[oww-dispatch] Starting dispatch for command token: '{action.CommandToken}'");
        LogEvent($"[oww-dispatch] Dispatchers will be tried in order: {string.Join(", ", CommandDispatchers.Select(d => d.Name))}");

        // Try all dispatchers (don't stop on first success) so animations and batch files both execute
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

        // Try Windows Forms first
        if (TryGetGameCommandHandlerFromWinForms(out target, out method, out detail))
        {
            lock (_dispatcherLock)
            {
                _cachedGameHandlerTarget = target;
                _cachedGameHandlerMethod = method;
            }
            return true;
        }

        LogEvent($"[oww-handler-discovery] Windows Forms path failed, trying assembly scan");

        // Fallback: search all loaded assemblies for command handler methods
        if (TryGetGameCommandHandlerFromAssemblies(out target, out method, out detail))
        {
            lock (_dispatcherLock)
            {
                _cachedGameHandlerTarget = target;
                _cachedGameHandlerMethod = method;
            }
            return true;
        }

        target = null;
        method = null;
        detail = "No game command handler found in Windows Forms or assemblies.";
        return false;
    }

    private static bool TryGetGameCommandHandlerFromWinForms(out object? target, out MethodInfo? method, out string detail)
    {
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

        foreach (var form in openForms)
        {
            if (form == null)
                continue;

            var candidates = form.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m =>
                {
                    if (m.IsSpecialName)
                        return false;

                    var parameters = m.GetParameters();
                    if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string))
                        return false;

                    // Apply category filter if sequential method testing is active
                    if (_sequentialMethodTestMode && !string.IsNullOrEmpty(_testCategoryFilter))
                    {
                        var methodCategory = GetMethodCategory(m);
                        return methodCategory == _testCategoryFilter;
                    }

                    return true;
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
            var filterInfo = _sequentialMethodTestMode && !string.IsNullOrEmpty(_testCategoryFilter)
                ? $" [category filter active: {_testCategoryFilter}]"
                : "";
            detail = $"No high-confidence Windows Forms handler found (best score: {bestScore}){filterInfo}.";
            return false;
        }

        target = bestTarget;
        method = bestMethod;
        detail = $"Selected Windows Forms handler {bestMethod.DeclaringType?.FullName}.{bestMethod.Name} (score={bestScore}).";
        return true;
    }

    private static bool TryGetGameCommandHandlerFromAssemblies(out object? target, out MethodInfo? method, out string detail)
    {
        target = null;
        method = null;
        var bestScore = int.MinValue;
        MethodInfo? bestMethod = null;

        try
        {
            var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            LogEvent($"[oww-handler-discovery] Scanning {allAssemblies.Length} loaded assemblies");

            // Also try the entry assembly (the main game exe)
            var entryAsm = System.Reflection.Assembly.GetEntryAssembly();
            if (entryAsm != null)
            {
                LogEvent($"[oww-handler-discovery] Will also scan entry assembly: {entryAsm.GetName().Name}");
                // Add entry assembly to the list if not already there
                var asmSet = new List<Assembly>(allAssemblies);
                if (!asmSet.Any(a => a.FullName == entryAsm.FullName))
                    asmSet.Add(entryAsm);
                allAssemblies = asmSet.ToArray();
            }

            foreach (var asm in allAssemblies)
            {
                var asmName = asm.GetName().Name ?? "";
                
                // Skip framework assemblies (but not the game assembly)
                if (asmName.StartsWith("System.") || asmName == "mscorlib" || asmName.StartsWith("MS.Internal") || 
                    asmName.StartsWith("netstandard") || asmName.StartsWith("WindowsBase") || asmName == "PresentationCore")
                {
                    continue;
                }

                // Skip our own patcher
                if (asmName.StartsWith("CrossPlatformPatcher"))
                {
                    continue;
                }

                LogEvent($"[oww-handler-discovery] Scanning assembly: {asmName}");

                try
                {
                    Type[] types = null;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        LogEvent($"[oww-handler-discovery]   (Partial load) Warning: {ex.Types?.Length ?? 0} types, {ex.LoaderExceptions?.Length ?? 0} loader errors");
                        types = ex.Types.Where(t => t != null).ToArray();
                    }

                    LogEvent($"[oww-handler-discovery]   Found {types?.Length ?? 0} types in {asmName}");
                    
                    if (types != null)
                    {
                        foreach (var type in types)
                        {
                            if (type == null)
                                continue;

                            // Skip internal/compiler-generated types
                            if (type.Name.StartsWith("<"))
                                continue;
                            
                            // Check if type has CompilerGeneratedAttribute
                            var hasCompilerGenerated = type.GetCustomAttributes(typeof(CompilerGeneratedAttribute), false).Length > 0;
                            if (hasCompilerGenerated)
                                continue;

                            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            foreach (var candidate in methods)
                            {
                                if (candidate.IsSpecialName)
                                    continue;

                                var parameters = candidate.GetParameters();
                                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string))
                                    continue;

                                // Apply category filter if sequential method testing is active
                                if (_sequentialMethodTestMode && !string.IsNullOrEmpty(_testCategoryFilter))
                                {
                                    var methodCategory = GetMethodCategory(candidate);
                                    if (methodCategory != _testCategoryFilter)
                                        continue;  // Skip methods that don't match the target category
                                }

                                var score = ScoreGameHandlerCandidate(candidate);
                                if (score > bestScore)
                                {
                                    bestScore = score;
                                    bestMethod = candidate;
                                    var categoryInfo = _sequentialMethodTestMode && !string.IsNullOrEmpty(_testCategoryFilter)
                                        ? $" [{GetMethodCategory(candidate)}]"
                                        : "";
                                    LogEvent($"[oww-handler-discovery] Better candidate found: {type.FullName}.{candidate.Name}{categoryInfo} (score={score})");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogEvent($"[oww-handler-discovery] Error scanning {asmName}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            detail = $"Error during assembly scan: {ex.Message}";
            LogEvent($"[oww-handler-discovery] {detail}");
            return false;
        }

        if (bestMethod == null || bestScore < 4)
        {
            var filterInfo = _sequentialMethodTestMode && !string.IsNullOrEmpty(_testCategoryFilter)
                ? $" [category filter active: {_testCategoryFilter}]"
                : "";
            detail = $"No high-confidence handler found in assemblies (best score: {bestScore}){filterInfo}.";
            return false;
        }

        target = null;
        if (!bestMethod.IsStatic)
        {
            if (!TryFindTargetInstance(bestMethod.DeclaringType, out target))
            {
                detail = $"Selected assembly handler {bestMethod.DeclaringType?.FullName}.{bestMethod.Name} (score={bestScore}) but no compatible target instance was found.";
                method = null;
                return false;
            }
        }

        method = bestMethod;
        detail = $"Selected assembly handler {bestMethod.DeclaringType?.FullName}.{bestMethod.Name} (score={bestScore}).";
        return true;
    }

    private static bool TryGetAnimationHandler(out object? target, out MethodInfo? method, out string detail)
    {
        lock (_dispatcherLock)
        {
            if (_cachedAnimationHandlerMethod != null)
            {
                target = _cachedAnimationHandlerTarget;
                method = _cachedAnimationHandlerMethod;
                detail = $"Using cached animation handler {_cachedAnimationHandlerMethod.DeclaringType?.FullName}.{_cachedAnimationHandlerMethod.Name}.";
                return true;
            }
        }

        if (TryGetAnimationHandlerFromWinForms(out target, out method, out detail) ||
            TryGetAnimationHandlerFromAssemblies(out target, out method, out detail))
        {
            lock (_dispatcherLock)
            {
                _cachedAnimationHandlerTarget = target;
                _cachedAnimationHandlerMethod = method;
            }

            return true;
        }

        target = null;
        method = null;
        return false;
    }

    private static bool TryGetAnimationHandlerFromWinForms(out object? target, out MethodInfo? method, out string detail)
    {
        target = null;
        method = null;

        var rawCandidates = GetOpenFormsSnapshot()
            .SelectMany(form => form.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m =>
                {
                    if (m.IsSpecialName)
                        return false;

                    var parameters = m.GetParameters();
                    if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string))
                        return false;

                    // Apply category filter if sequential method testing is active
                    if (_sequentialMethodTestMode && !string.IsNullOrEmpty(_testCategoryFilter))
                    {
                        var methodCategory = GetMethodCategory(m);
                        return methodCategory == _testCategoryFilter;
                    }

                    return true;
                })
                .Select(m => new ScoredMethodCandidate(form, m, score: 0)));

        var best = FindBestScoredMethodCandidate(
            rawCandidates,
            candidate => ScoreAnimationHandlerCandidate(candidate.Method),
            "oww-animation-discovery",
            "winforms");

        if (best == null || best.Score < 8)
        {
            var bestScore = best?.Score ?? int.MinValue;
            var filterInfo = _sequentialMethodTestMode && !string.IsNullOrEmpty(_testCategoryFilter)
                ? $" [category filter active: {_testCategoryFilter}]"
                : "";
            detail = $"No high-confidence WinForms animation handler found (best score: {bestScore}){filterInfo}.";
            target = null;
            method = null;
            return false;
        }

        target = best.Target;
        method = best.Method;
        detail = $"Selected WinForms animation handler {method.DeclaringType?.FullName}.{method.Name} (score={best.Score}).";
        return true;
    }

    private static bool TryGetAnimationHandlerFromAssemblies(out object? target, out MethodInfo? method, out string detail)
    {
        target = null;
        method = null;
        var candidates = new List<ScoredMethodCandidate>();

        foreach (var asm in GetCandidateAssembliesForDiscovery())
        {
            var asmName = asm.GetName().Name ?? string.Empty;
            try
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray()!;
                }

                foreach (var type in types)
                {
                    if (type == null || type.Name.StartsWith("<", StringComparison.Ordinal))
                        continue;

                    var hasCompilerGenerated = type.GetCustomAttributes(typeof(CompilerGeneratedAttribute), false).Length > 0;
                    if (hasCompilerGenerated)
                        continue;

                    var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    foreach (var candidate in methods)
                    {
                        if (candidate.IsSpecialName)
                            continue;

                        var parameters = candidate.GetParameters();
                        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string))
                            continue;

                        // Apply category filter if sequential method testing is active
                        if (_sequentialMethodTestMode && !string.IsNullOrEmpty(_testCategoryFilter))
                        {
                            var methodCategory = GetMethodCategory(candidate);
                            if (methodCategory != _testCategoryFilter)
                                continue;  // Skip methods that don't match the target category
                        }

                        candidates.Add(new ScoredMethodCandidate(target: null, candidate, score: 0));
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent($"[oww-animation-discovery] Error scanning {asmName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var best = FindBestScoredMethodCandidate(
            candidates,
            candidate => ScoreAnimationHandlerCandidate(candidate.Method),
            "oww-animation-discovery",
            "assembly");

        if (best == null || best.Score < 10)
        {
            var bestScore = best?.Score ?? int.MinValue;
            detail = $"No high-confidence animation handler found in assemblies (best score: {bestScore}).";
            return false;
        }

        method = best.Method;

        if (!method.IsStatic && !TryFindTargetInstance(method.DeclaringType, out target))
        {
            detail = $"Selected animation handler {method.DeclaringType?.FullName}.{method.Name} (score={best.Score}) but no compatible target instance was found.";
            method = null;
            return false;
        }

        detail = $"Selected assembly animation handler {method.DeclaringType?.FullName}.{method.Name} (score={best.Score}).";
        return true;
    }

    private static ScoredMethodCandidate? FindBestScoredMethodCandidate(
        IEnumerable<ScoredMethodCandidate> candidates,
        Func<ScoredMethodCandidate, int> scoreSelector,
        string logPrefix,
        string sourceLabel)
    {
        var scored = new List<ScoredMethodCandidate>();

        foreach (var candidate in candidates)
        {
            var score = scoreSelector(candidate);
            scored.Add(new ScoredMethodCandidate(candidate.Target, candidate.Method, score));
        }

        if (scored.Count == 0)
        {
            LogEvent($"[{logPrefix}] No candidate methods found from {sourceLabel} source.");
            return null;
        }

        var ranked = scored
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Method.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(c => c.Method.Name, StringComparer.Ordinal)
            .ToArray();

        LogEvent($"[{logPrefix}] Scored {ranked.Length} candidates from {sourceLabel} source.");

        foreach (var top in ranked.Take(10))
        {
            LogEvent($"[{logPrefix}] Candidate: {top.Method.DeclaringType?.FullName}.{top.Method.Name} (score={top.Score})");
        }

        return ranked[0];
    }

    private static Assembly[] GetCandidateAssembliesForDiscovery()
    {
        var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        var entryAsm = Assembly.GetEntryAssembly();
        if (entryAsm == null)
            return allAssemblies;

        var asmSet = new List<Assembly>(allAssemblies);
        if (!asmSet.Any(a => a.FullName == entryAsm.FullName))
            asmSet.Add(entryAsm);

        return asmSet
            .Where(asm =>
            {
                var name = asm.GetName().Name ?? string.Empty;
                if (name.StartsWith("System.", StringComparison.Ordinal) ||
                    name == "mscorlib" ||
                    name.StartsWith("MS.Internal", StringComparison.Ordinal) ||
                    name.StartsWith("netstandard", StringComparison.Ordinal) ||
                    name.StartsWith("WindowsBase", StringComparison.Ordinal) ||
                    name == "PresentationCore")
                {
                    return false;
                }

                return !name.StartsWith("CrossPlatformPatcher", StringComparison.Ordinal);
            })
            .ToArray();
    }

    private static bool TryFindTargetInstance(Type? declaringType, out object? target)
    {
        target = null;
        if (declaringType == null)
            return false;

        foreach (var form in GetOpenFormsSnapshot())
        {
            if (declaringType.IsInstanceOfType(form))
            {
                target = form;
                return true;
            }
        }

        return false;
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

    private static string[] BuildAnimationDispatchArguments(CommandAction action)
    {
        var token = action.CommandToken?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            return new[] { action.DispatchPhrase };

        return new[]
        {
            $"animations/{token}.txt",
            $"custom-commands/{token}.txt",
            $"{token}.txt",
            token,
            action.DispatchPhrase
        };
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
            var methodBody = method.GetMethodBody();
            var ilSize = methodBody?.GetILAsByteArray()?.Length ?? 0;
            if (ilSize >= 64 && ilSize <= 16384)
                score += 8;
            else if (ilSize >= 32)
                score += 4;

            var locals = methodBody?.LocalVariables.Count ?? 0;
            if (locals >= 2)
                score += 2;
            if (locals >= 8)
                score += 2;

            var handlerCount = methodBody?.ExceptionHandlingClauses.Count ?? 0;
            if (handlerCount > 0)
                score += 1;

            if (!method.IsStatic && method.DeclaringType != null)
            {
                if (method.DeclaringType.Name.IndexOf("Form", StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 4;
                if (method.DeclaringType.Name.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 2;
            }
        }
        catch
        {
            // Reflection can throw for dynamic/protected methods; keep score as-is.
        }

        return score;
    }

    private static int ScoreAnimationHandlerCandidate(MethodInfo method)
    {
        var score = ScoreGameHandlerCandidate(method);

        var name = method.Name;
        if (name.IndexOf("anim", StringComparison.OrdinalIgnoreCase) >= 0)
            score += 12;
        if (name.IndexOf("show", StringComparison.OrdinalIgnoreCase) >= 0)
            score += 6;
        if (name.IndexOf("hide", StringComparison.OrdinalIgnoreCase) >= 0)
            score += 6;
        if (name.IndexOf("react", StringComparison.OrdinalIgnoreCase) >= 0)
            score += 5;

        try
        {
            var methodBody = method.GetMethodBody();
            var ilSize = methodBody?.GetILAsByteArray()?.Length ?? 0;
            if (ilSize >= 256)
                score += 6;
            if (ilSize >= 1024)
                score += 4;
            if (ilSize >= 4096)
                score += 2;
        }
        catch
        {
            // Keep score as-is if IL introspection fails.
        }

        return score;
    }

    private static bool TryInvokeOnUiThread(object target, MethodInfo method, string phrase, out string detail)
    {
        var beginInvoke = target.GetType().GetMethod(
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
        detail = $"Queued '{phrase}' via UI dispatcher {method.Name}.";
        
        // Log method test result if testing is enabled
        if (_sequentialMethodTestMode && ShouldTestMethod(method))
        {
            RecordMethodTestAttempt(method, true, detail);
        }
        
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

        if (token.Contains("..", StringComparison.Ordinal))
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
            return null;

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
            if (!_runtimeDiagnosticCategoryCounts.TryAdd(category, 1))
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
            if (lower.Contains(keyword, StringComparison.Ordinal))
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

    /// <summary>
    /// Graceful shutdown (called on app exit or cleanup).
    /// Stops background inference, flushes queues.
    /// </summary>
    public static void Shutdown()
    {
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
            _cachedAnimationHandlerMethod = null;
            _cachedAnimationHandlerTarget = null;
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
