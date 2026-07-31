using SetupWizardCore.Models;

namespace SetupWizardCore.Services;

/// <summary>
/// Manages persistent setup state across wizard sessions.
/// Port of Swift SetupStateManager actor.
/// </summary>
public class SetupStateManager
{
    private SetupState _state;
    private readonly string _stateFilePath;
    private readonly object _lock = new();

    public SetupStateManager()
    {
        _stateFilePath = SetupState.GetStateFilePath();

        // Load existing state or create new
        var loaded = SetupState.Load(_stateFilePath);
        if (loaded != null)
        {
            _state = loaded;
            SetupWizardLogger.Instance.Debug("Loaded existing setup state");
        }
        else
        {
            _state = new SetupState();
            SetupWizardLogger.Instance.Debug("Created new setup state");
        }
    }

    public SetupState.Step GetCurrentStep() => _state.CurrentStep;

    public void SetCurrentStep(SetupState.Step step)
    {
        lock (_lock)
        {
            _state.CurrentStep = step;
            Save();
        }
    }

    public void SetSelectedFolder(string folder)
    {
        lock (_lock)
        {
            _state.SelectedFolder = folder;
            Save();
        }
    }

    public string? GetSelectedFolder() => _state.SelectedFolder;

    public void SetPatchedExePath(string path)
    {
        lock (_lock)
        {
            _state.PatchedExePath = path;
            Save();
        }
    }

    public string? GetPatchedExePath() => _state.PatchedExePath;

    public void SetSelectedWineType(string type)
    {
        lock (_lock)
        {
            _state.SelectedWineType = type;
            Save();
        }
    }

    public string? GetSelectedWineType() => _state.SelectedWineType;

    public void SetDotNetStatus(SetupState.OperationStatus status, string? error = null)
    {
        lock (_lock)
        {
            _state.DotNetStatus = status;
            _state.DotNetError = error;
            Save();
        }
    }

    public (SetupState.OperationStatus status, string? error) GetDotNetStatus()
    {
        lock (_lock)
        {
            return (_state.DotNetStatus, _state.DotNetError);
        }
    }

    public void SetCurrentOperationPID(int pid)
    {
        lock (_lock)
        {
            _state.CurrentOperationPID = pid;
            _state.OperationStartTime = DateTime.UtcNow;
            Save();
        }
    }

    public void ClearCurrentOperationPID()
    {
        lock (_lock)
        {
            _state.CurrentOperationPID = null;
            _state.OperationStartTime = null;
            Save();
        }
    }

    public void RecordCancellation()
    {
        lock (_lock)
        {
            _state.LastCancellationTime = DateTime.UtcNow;
            Save();
        }
    }

    public DateTime? GetLastCancellationTime()
    {
        lock (_lock) return _state.LastCancellationTime;
    }

    public void MarkComplete()
    {
        lock (_lock)
        {
            _state.WizardStatus = SetupState.OperationStatus.Completed;
            _state.CompletionTime = DateTime.UtcNow;
            Save();
        }
    }

    public void MarkFailed(string error)
    {
        lock (_lock)
        {
            _state.WizardStatus = SetupState.OperationStatus.Failed;
            _state.LastError = error;
            Save();
        }
    }

    public (SetupState.OperationStatus status, string? error) GetWizardStatus()
    {
        lock (_lock) return (_state.WizardStatus, _state.LastError);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _state = new SetupState();
            try { File.Delete(_stateFilePath); }
            catch { /* ignore */ }
            SetupWizardLogger.Instance.Debug("Reset setup state");
        }
    }

    public SetupState GetFullState()
    {
        lock (_lock) return _state;
    }

    /// <summary>
    /// Check if a previously recorded operation PID is still running; mark failed if stale.
    /// Port of Swift cleanupStaleProcesses().
    /// </summary>
    public void CleanupStaleProcesses()
    {
        lock (_lock)
        {
            if (_state.CurrentOperationPID.HasValue && _state.DotNetStatus == SetupState.OperationStatus.InProgress)
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(_state.CurrentOperationPID!.Value);
                    if (process.HasExited)
                    {
                        SetupWizardLogger.Instance.Log("Process " + _state.CurrentOperationPID.Value + " no longer exists, cleaning up");
                        _state.CurrentOperationPID = null;
                        _state.DotNetStatus = SetupState.OperationStatus.Failed;
                        _state.DotNetError = "Process was terminated unexpectedly";
                        Save();
                    }
                }
                catch (ArgumentException)
                {
                    // Process doesn't exist
                    SetupWizardLogger.Instance.Log("Process " + _state.CurrentOperationPID!.Value + " no longer exists, cleaning up");
                    _state.CurrentOperationPID = null;
                    _state.DotNetStatus = SetupState.OperationStatus.Failed;
                    _state.DotNetError = "Process was terminated unexpectedly";
                    Save();
                }
            }
        }
    }

    private void Save()
    {
        try
        {
            _state.Save(_stateFilePath);
        }
        catch (Exception ex)
        {
            SetupWizardLogger.Instance.Error("Failed to save state: " + ex.Message);
        }
    }
}
