using System.Runtime.InteropServices;
using SetupWizardCore.Models;

namespace SetupWizardCore.Services;

/// <summary>
/// Orchestrates .NET installation across platforms with strategy pattern.
/// Port of Swift DotNetInstaller actor.
/// </summary>
public class DotNetInstaller
{
    public class InstallError : Exception
    {
        public InstallError(string message) : base(message) { }
        public InstallError(string message, Exception inner) : base(message, inner) { }
    }

    private readonly SetupStateManager _stateManager;
    private ProcessRunner? _currentRunner;

    public DotNetInstaller(SetupStateManager stateManager)
    {
        _stateManager = stateManager;
    }

    /// <summary>
    /// Attempt to install .NET 4.8 using available strategies.
    /// Port of Swift installDotNet().
    /// </summary>
    public async Task InstallDotNetAsync(string winePrefix, ProcessRunner.OutputHandler onOutput)
    {
        _stateManager.SetDotNetStatus(SetupState.OperationStatus.InProgress);
        SetupWizardLogger.Instance.Log($"Starting .NET installation in prefix: {winePrefix}");

        // Strategy 1: Native Windows - check registry / download installer
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                await InstallNativeWindowsAsync(onOutput);
                _stateManager.SetDotNetStatus(SetupState.OperationStatus.Completed);
                SetupWizardLogger.Instance.Log(".NET installation completed successfully (native)");
                return;
            }
            catch (Exception ex)
            {
                SetupWizardLogger.Instance.Error($"Native Windows install failed: {ex.Message}");
            }
        }

        // Strategy 1b: Bundled winetricks (macOS/Linux)
        try
        {
            await InstallViaBundledWinetricksAsync(winePrefix, onOutput);
            _stateManager.SetDotNetStatus(SetupState.OperationStatus.Completed);
            SetupWizardLogger.Instance.Log(".NET installation completed successfully");
            return;
        }
        catch (Exception ex)
        {
            SetupWizardLogger.Instance.Error($"Bundled winetricks failed: {ex.Message}");
        }

        // Strategy 2: System winetricks (macOS/Linux)
        try
        {
            await InstallViaSystemWinetricksAsync(winePrefix, onOutput);
            _stateManager.SetDotNetStatus(SetupState.OperationStatus.Completed);
            SetupWizardLogger.Instance.Log(".NET installation completed successfully");
            return;
        }
        catch (Exception ex)
        {
            SetupWizardLogger.Instance.Error($"System winetricks failed: {ex.Message}");
        }

        // Strategy 3: Registry approach (macOS/Linux)
        try
        {
            await InstallViaRegistryAsync(winePrefix, onOutput);
            _stateManager.SetDotNetStatus(SetupState.OperationStatus.Completed);
            SetupWizardLogger.Instance.Log(".NET installation completed successfully (via registry)");
            return;
        }
        catch (Exception ex)
        {
            SetupWizardLogger.Instance.Error($"Registry approach failed: {ex.Message}");
        }

        // All strategies failed
        var errorMsg = "All .NET installation methods failed";
        SetupWizardLogger.Instance.Error(errorMsg);
        _stateManager.SetDotNetStatus(SetupState.OperationStatus.Failed, errorMsg);
        throw new InstallError(errorMsg);
    }

    /// <summary>
    /// Strategy 1: Native Windows .NET installation.
    /// </summary>
    private async Task InstallNativeWindowsAsync(ProcessRunner.OutputHandler onOutput)
    {
        onOutput("Checking Windows .NET Framework status...");

        // Check if .NET 4.8 is already installed via registry
        if (IsDotNet48Installed())
        {
            onOutput("✓ .NET Framework 4.8 is already installed");
            return;
        }

        onOutput("Downloading .NET Framework 4.8 Runtime...");
        var installerUrl = "https://go.microsoft.com/fwlink/?linkid=2088631";
        var tempDir = Path.GetTempPath();
        var installerPath = Path.Combine(tempDir, "ndp48-x86-x64-allos-enu.exe");

        using var client = new HttpClient();
        using var response = await client.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write);
        await contentStream.CopyToAsync(fileStream);

        onOutput("Running .NET Framework 4.8 installer...");
        var runner = new ProcessRunner(installerPath, new[] { "/q", "/norestart" });
        _currentRunner = runner;
        runner.SetOutputHandler(onOutput);

        try
        {
            await runner.RunAsync();
            onOutput("✓ .NET Framework 4.8 installation completed");
        }
        catch
        {
            throw new InstallError("Native .NET installer failed");
        }
        finally
        {
            try { File.Delete(installerPath); }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Strategy 1b: Use bundled winetricks from app resources.
    /// </summary>
    private async Task InstallViaBundledWinetricksAsync(string winePrefix, ProcessRunner.OutputHandler onOutput)
    {
        // Try to find bundled winetricks
        string? bundledPath = FindBundledWinetricks();
        if (bundledPath == null)
            throw new InstallError("Bundled winetricks not found");

        onOutput($"Using bundled winetricks: {bundledPath}");

        var runner = new ProcessRunner("/bin/bash", new[] { bundledPath, "dotnet48" });
        _currentRunner = runner;

        var env = new Dictionary<string, string>
        {
            ["WINEPREFIX"] = winePrefix,
            ["WINEARCH"] = "win64"
        };

        runner.SetOutputHandler(onOutput);

        try
        {
            await runner.RunAsync(environment: env);
            onOutput("✓ Bundled winetricks completed");
        }
        catch (ProcessRunner.RunnerError ex) when (ex.Message.Contains("cancelled"))
        {
            throw new InstallError("Installation cancelled by user");
        }
        catch (Exception ex)
        {
            throw new InstallError($"Bundled winetricks failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Strategy 2: Use system winetricks.
    /// </summary>
    private async Task InstallViaSystemWinetricksAsync(string winePrefix, ProcessRunner.OutputHandler onOutput)
    {
        var paths = new[]
        {
            "/usr/local/bin/winetricks",
            "/usr/bin/winetricks",
            "/opt/homebrew/bin/winetricks"
        };

        var winetricksPath = paths.FirstOrDefault(File.Exists);
        if (winetricksPath == null)
            throw new InstallError("System winetricks not found");

        onOutput($"Using system winetricks: {winetricksPath}");

        var runner = new ProcessRunner(winetricksPath, new[] { "dotnet48" });
        _currentRunner = runner;

        var env = new Dictionary<string, string>
        {
            ["WINEPREFIX"] = winePrefix,
            ["WINEARCH"] = "win64"
        };

        runner.SetOutputHandler(onOutput);

        try
        {
            await runner.RunAsync(environment: env);
            onOutput("✓ System winetricks completed");
        }
        catch (ProcessRunner.RunnerError ex) when (ex.Message.Contains("cancelled"))
        {
            throw new InstallError("Installation cancelled by user");
        }
        catch (Exception ex)
        {
            throw new InstallError($"System winetricks failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Strategy 3: Direct Wine registry manipulation as fallback.
    /// </summary>
    private async Task InstallViaRegistryAsync(string winePrefix, ProcessRunner.OutputHandler onOutput)
    {
        onOutput("Falling back to registry approach...");

        var runner = new ProcessRunner("/bin/bash", new[]
        {
            "-c",
            $"WINEPREFIX=\"{winePrefix}\" WINEARCH=win64 wine regedit /e /d \"HKEY_LOCAL_MACHINE\\\\Software\\\\Microsoft\\\\.NETFramework\" 2>/dev/null | grep -q \"4.8\" && exit 0; " +
            $"echo \"Setting .NET registry keys...\"; " +
            $"WINEPREFIX=\"{winePrefix}\" WINEARCH=win64 wine regedit <<'EOF'\n" +
            "REGEDIT4\n" +
            "[HKEY_LOCAL_MACHINE\\Software\\Microsoft\\.NETFramework]\n" +
            "\"InstallRoot\"=\"C:\\\\Windows\\\\Microsoft.NET\\\\Framework64\\\\\"\n" +
            "[HKEY_LOCAL_MACHINE\\Software\\Microsoft\\.NETFramework\\v4.0.30319]\n" +
            "\"Install\"=dword:00000001\n" +
            "EOF"
        });

        _currentRunner = runner;
        runner.SetOutputHandler(onOutput);

        try
        {
            await runner.RunAsync();
            onOutput("✓ Registry approach completed");
        }
        catch (ProcessRunner.RunnerError ex) when (ex.Message.Contains("cancelled"))
        {
            throw new InstallError("Installation cancelled by user");
        }
        catch (Exception ex)
        {
            throw new InstallError($"Registry approach failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Cancel the current installation.
    /// </summary>
    public async Task CancelAsync()
    {
        SetupWizardLogger.Instance.Log("Cancelling .NET installation");
        if (_currentRunner != null)
        {
            await _currentRunner.CancelAsync();
        }
        _stateManager.RecordCancellation();
    }

    /// <summary>
    /// Find bundled winetricks in platform-appropriate locations.
    /// </summary>
    private static string? FindBundledWinetricks()
    {
        // On macOS: bundled in the .app bundle
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var appBundlePath = "/Applications/CrossPlatformPatcher/SetupWizardApp.app/Contents/Resources/winetricks";
            if (File.Exists(appBundlePath))
                return appBundlePath;
        }

        // Check relative to the running assembly
        var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var assemblyDir = Path.GetDirectoryName(assemblyLocation);
        if (assemblyDir != null)
        {
            var relativePath = Path.Combine(assemblyDir, "Resources", "winetricks");
            if (File.Exists(relativePath))
                return relativePath;
        }

        // Check current directory
        if (File.Exists("Resources/winetricks"))
            return Path.GetFullPath("Resources/winetricks");

        return null;
    }

    /// <summary>
    /// Check if .NET Framework 4.8 is installed on Windows via registry.
    /// </summary>
    private static bool IsDotNet48Installed()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            const string keyPath = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full";
            using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry32)
                .OpenSubKey(keyPath);

            var release = key?.GetValue("Release") as int?;
            // Release >= 528040 indicates .NET Framework 4.8 or later
            return release.HasValue && release.Value >= 528040;
        }
        catch
        {
            return false;
        }
    }
}
