using System;
using System.Diagnostics;
using System.Threading;
using System.Linq;
using System.IO;
using System.Collections.Generic;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Test harness for command injection without requiring voice input.
/// 
/// Directly injects CommandAction objects into the OpenWakeWord dispatcher
/// to test if the command handler discovery and invocation works.
/// 
/// Usage:
///   dotnet run --test-commands <paicom-path> [--interval 1000] [--duration 60] [--command "phrase1" "phrase2" ...]
///   
/// Examples:
///   dotnet run --test-commands "/path/to/PAIcom.exe"                              # Loads real commands, runs until Ctrl+C
///   dotnet run --test-commands "/path/to/PAIcom.exe" --duration 30                # Stops after 30 seconds
///   dotnet run --test-commands "/path/to/PAIcom.exe" --interval 2000              # Every 2 seconds
///   dotnet run --test-commands "/path/to/PAIcom.exe" --command "open the browser" # Test specific command
/// </summary>
public class TestCommandInjection
{
    private static Process? _gameProcess;
    private static CancellationTokenSource? _cancellationTokenSource;
    private static string? _ipcQueueFilePath;

    public static void Run(string[] args)
    {
        Console.WriteLine("[TEST] Command Injection Test Mode");
        Console.WriteLine("==================================\n");

        if (args.Length == 0)
        {
            PrintUsage();
            return;
        }

        var paicomPath = args[0];
        var interval = 1000; // milliseconds
        int? duration = null; // seconds (null = infinite)
        string[] testPhrases = Array.Empty<string>();
        var enableRuntimeDiagnostics = false;
        int? runtimeDiagnosticDurationSeconds = null;

        // Parse arguments
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--interval":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var ms))
                        interval = ms;
                    break;
                case "--duration":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var sec))
                        duration = sec;
                    break;
                case "--command":
                    var customPhrases = new List<string>();
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        customPhrases.Add(args[++i]);
                    if (customPhrases.Count > 0)
                        testPhrases = customPhrases.ToArray();
                    break;
                case "--runtime-diagnostic":
                    enableRuntimeDiagnostics = true;
                    break;
                case "--runtime-diagnostic-duration":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var diagSec) && diagSec > 0)
                        runtimeDiagnosticDurationSeconds = diagSec;
                    break;
            }
        }

        // If no custom commands, load from commands.txt
        if (testPhrases.Length == 0)
        {
            testPhrases = LoadCommandsFromFile();
            if (testPhrases.Length == 0)
            {
                Console.WriteLine("[TEST] ERROR: Could not load commands from commands.txt and no custom commands provided.");
                return;
            }
        }

        Console.WriteLine($"[TEST] PAIcom Path: {paicomPath}");
        Console.WriteLine($"[TEST] Interval: {interval}ms");
        Console.WriteLine($"[TEST] Duration: {(duration.HasValue ? duration + "s" : "infinite (Ctrl+C to stop)")}");
        Console.WriteLine($"[TEST] Runtime diagnostics: {(enableRuntimeDiagnostics ? "enabled" : "disabled")}");
        if (runtimeDiagnosticDurationSeconds.HasValue)
            Console.WriteLine($"[TEST] Runtime diagnostic duration: {runtimeDiagnosticDurationSeconds.Value}s");
        Console.WriteLine($"[TEST] Test phrases ({testPhrases.Length}):");
        for (int i = 0; i < Math.Min(5, testPhrases.Length); i++)
            Console.WriteLine($"  - {testPhrases[i]}");
        if (testPhrases.Length > 5)
            Console.WriteLine($"  ... and {testPhrases.Length - 5} more");

        Console.WriteLine("\n[TEST] Launching PAIcom.exe...");
        if (!LaunchGame(paicomPath, enableRuntimeDiagnostics, runtimeDiagnosticDurationSeconds))
        {
            Console.WriteLine("[TEST] ERROR: Failed to launch PAIcom.exe");
            return;
        }

        Console.WriteLine("[TEST] Waiting 35 seconds for game UI to fully render and assemblies to load...");
        Thread.Sleep(35000);

        Console.WriteLine("[TEST] Starting command injection (in-game IPC mode)...");
        if (duration.HasValue)
            Console.WriteLine($"[TEST] Will run for {duration} seconds. Press Ctrl+C to stop early.\n");
        else
            Console.WriteLine("[TEST] Press Ctrl+C to stop.\n");

        // Set up Ctrl+C handler
        _cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            _cancellationTokenSource?.Cancel();
        };

        RunCommandLoop(testPhrases, interval, duration);

        // Cleanup
        Console.WriteLine("\n[TEST] Cleaning up...");
        KillGame();
        Console.WriteLine("[TEST] Test completed.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --test-commands <paicom-path> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --interval <ms>          Time between command injections (default: 1000)");
        Console.WriteLine("  --duration <seconds>     How long to run (default: infinite)");
        Console.WriteLine("  --command <phrases...>   Custom command phrases to test");
        Console.WriteLine("  --runtime-diagnostic     Enable runtime diagnostic mode in the launched game process");
        Console.WriteLine("  --runtime-diagnostic-duration <seconds>  Runtime diagnostic snapshot duration (default: 180)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --test-commands /path/to/PAIcom.exe");
        Console.WriteLine("  dotnet run --test-commands /path/to/PAIcom.exe --duration 30");
        Console.WriteLine("  dotnet run --test-commands /path/to/PAIcom.exe --interval 500");
        Console.WriteLine("  dotnet run --test-commands /path/to/PAIcom.exe --command \"open the browser\" \"play some music\"");
        Console.WriteLine("  dotnet run --test-commands /path/to/PAIcom.exe --runtime-diagnostic --runtime-diagnostic-duration 120");
    }

    private static string[] LoadCommandsFromFile()
    {
        try
        {
            var commandsFile = Path.Combine(
                Directory.GetCurrentDirectory(),
                "PAIcom_Player_Folder", "custom-commands", "commands.txt"
            );

            if (!File.Exists(commandsFile))
            {
                Console.WriteLine($"[TEST] WARNING: Could not find commands.txt at {commandsFile}");
                return Array.Empty<string>();
            }

            var phrases = File.ReadAllLines(commandsFile)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrEmpty(line) && line.Contains('('))
                .Select(line =>
                {
                    // Extract just the phrase part: "hey paicom xyz (token.txt)" -> "hey paicom xyz"
                    var parenIndex = line.LastIndexOf('(');
                    return parenIndex > 0 ? line.Substring(0, parenIndex).Trim() : line;
                })
                .ToArray();

            Console.WriteLine($"[TEST] Loaded {phrases.Length} commands from commands.txt");
            return phrases;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TEST] ERROR loading commands.txt: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static bool LaunchGame(string paicomPath, bool enableRuntimeDiagnostics, int? runtimeDiagnosticDurationSeconds)
    {
        try
        {
            var gameDirectory = Path.GetDirectoryName(paicomPath) ?? Directory.GetCurrentDirectory();
            var diagnosticsDir = Path.Combine(gameDirectory, "diagnostics");
            Directory.CreateDirectory(diagnosticsDir);
            _ipcQueueFilePath = Path.Combine(diagnosticsDir, "test-command-queue.txt");
            File.WriteAllText(_ipcQueueFilePath, string.Empty);

            // Determine if it's a Windows path or if we need to use wine/run scripts
            bool isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            
            ProcessStartInfo psi;

            if (isWindows)
            {
                psi = new ProcessStartInfo
                {
                    FileName = paicomPath,
                    UseShellExecute = false,
                };
            }
            else
            {
                // On macOS/Linux, try to use the shell script or wine
                var launchScript = Path.Combine(Path.GetDirectoryName(paicomPath) ?? ".", "launch.command");
                if (!File.Exists(launchScript))
                {
                    launchScript = Path.Combine(Path.GetDirectoryName(paicomPath) ?? ".", "run.sh");
                }

                if (File.Exists(launchScript))
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = $"\"{launchScript}\"",
                        UseShellExecute = false,
                    };
                }
                else
                {
                    // Fall back to running exe directly with wine
                    psi = new ProcessStartInfo
                    {
                        FileName = "wine",
                        Arguments = $"\"{paicomPath}\"",
                        UseShellExecute = false,
                    };
                }
            }

            // When UseShellExecute=false, we must populate the environment dictionary
            // with all parent environment variables first, then override/add our test variables
            if (!psi.UseShellExecute)
            {
                foreach (var key in Environment.GetEnvironmentVariables().Keys)
                {
                    var keyStr = key?.ToString();
                    if (!string.IsNullOrEmpty(keyStr))
                    {
                        var value = Environment.GetEnvironmentVariable(keyStr);
                        psi.Environment[keyStr] = value ?? string.Empty;
                    }
                }
            }

            if (enableRuntimeDiagnostics)
            {
                psi.Environment["PAICOM_RUNTIME_DIAGNOSTIC_MODE"] = "1";
                if (runtimeDiagnosticDurationSeconds.HasValue && runtimeDiagnosticDurationSeconds.Value > 0)
                    psi.Environment["PAICOM_RUNTIME_DIAGNOSTIC_DURATION_SECONDS"] = runtimeDiagnosticDurationSeconds.Value.ToString();
            }

            // Pass through sequential method testing environment variables if set
            var sequentialMethodTest = Environment.GetEnvironmentVariable("PAICOM_SEQUENTIAL_METHOD_TEST");
            if (!string.IsNullOrEmpty(sequentialMethodTest))
                psi.Environment["PAICOM_SEQUENTIAL_METHOD_TEST"] = sequentialMethodTest;

            var testCategory = Environment.GetEnvironmentVariable("PAICOM_TEST_CATEGORY");
            if (!string.IsNullOrEmpty(testCategory))
                psi.Environment["PAICOM_TEST_CATEGORY"] = testCategory;

            var methodTestNum = Environment.GetEnvironmentVariable("PAICOM_METHOD_TEST_NUM");
            if (!string.IsNullOrEmpty(methodTestNum))
                psi.Environment["PAICOM_METHOD_TEST_NUM"] = methodTestNum;

            var liveMethodTest = Environment.GetEnvironmentVariable("PAICOM_LIVE_METHOD_TEST");
            if (!string.IsNullOrEmpty(liveMethodTest))
                psi.Environment["PAICOM_LIVE_METHOD_TEST"] = liveMethodTest;

            var liveTestLog = Environment.GetEnvironmentVariable("PAICOM_LIVE_TEST_LOG");
            if (!string.IsNullOrEmpty(liveTestLog))
                psi.Environment["PAICOM_LIVE_TEST_LOG"] = liveTestLog;

            // In test mode, force sequential method candidate testing and enable
            // in-game command queue processing.
            psi.Environment["PAICOM_TRY_ALL_CANDIDATE_METHODS"] = "1";
            psi.Environment["PAICOM_CANDIDATE_METHOD_LIMIT"] = "25";
            psi.Environment["PAICOM_TEST_COMMAND_QUEUE_FILE"] = _ipcQueueFilePath;

            _gameProcess = Process.Start(psi);
            return _gameProcess != null && !_gameProcess.HasExited;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TEST] ERROR launching game: {ex.Message}");
            return false;
        }
    }

    private static void KillGame()
    {
        try
        {
            if (_gameProcess != null && !_gameProcess.HasExited)
            {
                Console.WriteLine("[TEST] Terminating PAIcom.exe...");
                _gameProcess.Kill(entireProcessTree: true);
                _gameProcess.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TEST] WARNING: Error killing game process: {ex.Message}");
        }
    }

    private static void RunCommandLoop(string[] testPhrases, int interval, int? duration)
    {
        var sw = Stopwatch.StartNew();
        int injectionCount = 0;
        int phraseIndex = 0;

        while (_cancellationTokenSource != null && !_cancellationTokenSource.Token.IsCancellationRequested)
        {
            // Check duration timeout
            if (duration.HasValue && sw.Elapsed.TotalSeconds >= duration.Value)
            {
                Console.WriteLine($"\n[TEST] Duration limit reached ({duration}s).");
                break;
            }

            var phrase = testPhrases[phraseIndex];
            phraseIndex = (phraseIndex + 1) % testPhrases.Length;

            InjectCommand(phrase);
            injectionCount++;

            Console.WriteLine($"[TEST] ({sw.Elapsed:mm\\:ss\\.ff}) Injected #{injectionCount}: \"{phrase}\"");

            // Wait with cancellation support
            if (!WaitWithCancellation(interval))
            {
                Console.WriteLine($"\n[TEST] Interrupted by user.");
                break;
            }
        }

        sw.Stop();
        Console.WriteLine($"[TEST] Completed after {sw.Elapsed:mm\\:ss}. {injectionCount} commands injected.");
    }

    private static bool WaitWithCancellation(int milliseconds)
    {
        var remainingMs = milliseconds;
        const int checkInterval = 100; // Check cancellation every 100ms

        while (remainingMs > 0)
        {
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true)
                return false;

            var sleepMs = Math.Min(checkInterval, remainingMs);
            Thread.Sleep(sleepMs);
            remainingMs -= sleepMs;
        }

        return true;
    }

    private static void InjectCommand(string dispatchPhrase)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_ipcQueueFilePath))
            {
                Console.WriteLine("[TEST]   -> Dispatch: FAILED, IPC queue path is unavailable.");
                return;
            }

            var record = $"{DateTime.UtcNow:O}\t{dispatchPhrase}{Environment.NewLine}";
            File.AppendAllText(_ipcQueueFilePath, record);
            Console.WriteLine($"[TEST]   -> Dispatch: QUEUED, sent to game IPC queue '{_ipcQueueFilePath}'.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TEST] ERROR injecting command: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Console.WriteLine($"[TEST] Inner: {ex.InnerException.Message}");
        }
    }
}

