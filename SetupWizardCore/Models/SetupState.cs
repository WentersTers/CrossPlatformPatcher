using System.Text.Json;
using System.Text.Json.Serialization;

namespace SetupWizardCore.Models;

/// <summary>
/// Persistent state file tracking progress across wizard sessions for resume capability.
/// Port of Swift SetupStateFile struct.
/// </summary>
public class SetupState
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum Step
    {
        Welcome,
        FolderPicker,
        ModelSelection,
        Download,
        DependencyCheck,
        DotNetInstall,
        Complete
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum OperationStatus
    {
        NotStarted,
        InProgress,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>Current step in wizard</summary>
    public Step CurrentStep { get; set; } = Step.Welcome;

    /// <summary>Path to selected PAIcom folder</summary>
    public string? SelectedFolder { get; set; }

    /// <summary>Path to patched PAIcom executable</summary>
    public string? PatchedExePath { get; set; }

    /// <summary>
    /// Wine type chosen: "whisky", "system_wine", "native"
    /// </summary>
    public string? SelectedWineType { get; set; }

    /// <summary>.NET installation status</summary>
    public OperationStatus DotNetStatus { get; set; } = OperationStatus.NotStarted;

    /// <summary>Last .NET install error (if any)</summary>
    public string? DotNetError { get; set; }

    /// <summary>Process ID of currently running operation (for cleanup)</summary>
    public int? CurrentOperationPID { get; set; }

    /// <summary>When current operation started</summary>
    public DateTime? OperationStartTime { get; set; }

    /// <summary>When last cancellation occurred</summary>
    public DateTime? LastCancellationTime { get; set; }

    /// <summary>Overall wizard completion status</summary>
    public OperationStatus WizardStatus { get; set; } = OperationStatus.NotStarted;

    /// <summary>Completion timestamp</summary>
    public DateTime? CompletionTime { get; set; }

    /// <summary>Any error that caused failure</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Load state from a JSON file path.
    /// </summary>
    public static SetupState? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SetupState>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Save state to a JSON file path.
    /// </summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Get the platform-appropriate state directory path.
    /// </summary>
    public static string GetStateDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "CrossPlatformPatcher");
    }

    /// <summary>
    /// Get the platform-appropriate state file path.
    /// </summary>
    public static string GetStateFilePath()
    {
        return Path.Combine(GetStateDirectory(), "setup-state.json");
    }
}
