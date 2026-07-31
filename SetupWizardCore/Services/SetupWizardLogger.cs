using SetupWizardCore.Models;

namespace SetupWizardCore.Services;

/// <summary>
/// Centralized logging utility for the setup wizard.
/// Port of Swift Logger class.
/// Writes to a file in the platform-appropriate state directory + console.
/// </summary>
public class SetupWizardLogger
{
    private static SetupWizardLogger? _instance;
    private static readonly object _instanceLock = new();

    private readonly string _logPath;
    private readonly object _writeLock = new();

    public static SetupWizardLogger Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_instanceLock)
                {
                    _instance ??= new SetupWizardLogger();
                }
            }
            return _instance;
        }
    }

    private SetupWizardLogger()
    {
        var logDir = SetupState.GetStateDirectory();
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, "setup-wizard.log");
    }

    public void Log(string message, string level = "INFO")
    {
        var timestamp = DateTime.UtcNow.ToString("O"); // ISO 8601
        var formatted = $"[{timestamp}] [{level}] {message}";

        lock (_writeLock)
        {
            try
            {
                File.AppendAllText(_logPath, formatted + Environment.NewLine);
            }
            catch
            {
                // Fail silently if file write fails
            }
        }

        Console.WriteLine(formatted);
    }

    public void Debug(string message) => Log(message, "DEBUG");

    public void Error(string message) => Log(message, "ERROR");

    public string GetLogPath() => _logPath;
}
