using System.Diagnostics;

namespace SetupWizardCore.Services;

/// <summary>
/// Runs shell commands with real-time output streaming, cancellation support, and state tracking.
/// Port of Swift ProcessRunner actor.
/// </summary>
public class ProcessRunner : IAsyncDisposable
{
    public class RunnerError : Exception
    {
        public int? ExitCode { get; }
        public RunnerError(string message, int? exitCode = null) : base(message)
        {
            ExitCode = exitCode;
        }
    }

    public delegate void OutputHandler(string line);

    private readonly string _executable;
    private readonly string[] _arguments;
    private Process? _process;
    private readonly List<string> _outputLines = new();
    private bool _isCancelled;
    private bool _isRunning;
    private OutputHandler? _onOutput;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public ProcessRunner(string executable, string[]? arguments = null)
    {
        _executable = executable;
        _arguments = arguments ?? Array.Empty<string>();
    }

    public void SetOutputHandler(OutputHandler handler)
    {
        _onOutput = handler;
    }

    /// <summary>
    /// Run the process with real-time output streaming.
    /// Port of Swift run().
    /// </summary>
    public async Task<int> RunAsync(
        Dictionary<string, string>? environment = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isCancelled = false;
        _isRunning = true;

        lock (_lock) _outputLines.Clear();

        // Verify executable exists
        if (!File.Exists(_executable))
        {
            SetupWizardLogger.Instance.Error($"Executable not found: {_executable}");
            _isRunning = false;
            throw new RunnerError("Invalid or missing executable");
        }

        var process = new Process();
        _process = process;

        process.StartInfo.FileName = _executable;
        process.StartInfo.Arguments = string.Join(" ", _arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        // Set working directory
        if (!string.IsNullOrEmpty(workingDirectory))
            process.StartInfo.WorkingDirectory = workingDirectory;

        // Set environment variables
        if (environment != null)
        {
            foreach (var kvp in environment)
            {
                process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }

        // Set up output capture
        var outputCompletion = new TaskCompletionSource<bool>();
        var errorCompletion = new TaskCompletionSource<bool>();

        process.OutputDataReceived += (sender, args) =>
        {
            if (args.Data != null)
            {
                AddOutputLine(args.Data);
            }
        };

        process.ErrorDataReceived += (sender, args) =>
        {
            if (args.Data != null)
            {
                AddOutputLine(args.Data);
            }
        };

        process.Exited += (sender, args) =>
        {
            outputCompletion.TrySetResult(true);
            errorCompletion.TrySetResult(true);
        };

        try
        {
            if (!process.Start())
            {
                SetupWizardLogger.Instance.Error("Failed to start process");
                _isRunning = false;
                throw new RunnerError("Failed to start process");
            }

            SetupWizardLogger.Instance.Debug($"Started process: {_executable} with args: {string.Join(" ", _arguments)}");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for exit or cancellation
            try
            {
                await process.WaitForExitAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                _isCancelled = true;
                await CancelAsync();
                _isRunning = false;
                throw new RunnerError("Process was cancelled by user");
            }

            // Ensure output has been fully read
            await Task.WhenAny(
                Task.WhenAll(outputCompletion.Task, errorCompletion.Task),
                Task.Delay(500));

            if (_isCancelled)
            {
                SetupWizardLogger.Instance.Log("Process cancelled");
                _isRunning = false;
                throw new RunnerError("Process was cancelled by user");
            }

            var exitCode = process.ExitCode;
            _isRunning = false;

            if (exitCode != 0)
            {
                SetupWizardLogger.Instance.Error($"Process exited with code: {exitCode}");
                throw new RunnerError($"Process exited with code {exitCode}", exitCode);
            }

            return exitCode;
        }
        catch (RunnerError) { throw; }
        catch (Exception ex)
        {
            SetupWizardLogger.Instance.Error($"Failed to start process: {ex.Message}");
            _isRunning = false;
            throw new RunnerError("Failed to start process");
        }
    }

    /// <summary>
    /// Cancel the running process gracefully (CloseMainWindow) then forcefully (Kill).
    /// Port of Swift cancel().
    /// </summary>
    public async Task CancelAsync()
    {
        _isCancelled = true;
        _cts?.Cancel();

        var process = _process;
        if (process == null || process.HasExited)
        {
            SetupWizardLogger.Instance.Log("No process to cancel");
            return;
        }

        SetupWizardLogger.Instance.Log($"Cancelling process (PID: {process.Id})");

        // Try graceful shutdown first
        if (!process.CloseMainWindow())
        {
            process.Kill(entireProcessTree: true);
        }
        else
        {
            // Wait up to 5 seconds for graceful termination
            try
            {
                await Task.Delay(5000, new CancellationTokenSource().Token);
                if (!process.HasExited)
                {
                    SetupWizardLogger.Instance.Log($"Force-killing process (PID: {process.Id})");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
    }

    public IReadOnlyList<string> GetOutputLines()
    {
        lock (_lock) return _outputLines.ToList().AsReadOnly();
    }

    public bool IsStillRunning()
    {
        return _isRunning && _process != null && !_process.HasExited;
    }

    private void AddOutputLine(string line)
    {
        lock (_lock) _outputLines.Add(line);
        _onOutput?.Invoke(line);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process != null && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
        _process?.Dispose();
        _cts?.Dispose();
        await Task.CompletedTask;
    }
}
