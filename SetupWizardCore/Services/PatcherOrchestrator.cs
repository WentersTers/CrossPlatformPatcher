namespace SetupWizardCore.Services;

/// <summary>
/// High-level orchestration: fetch release → download → run patcher on selected PAIcom.exe.
/// Port of the downloadAndPatch() logic from Swift SetupWizardViewModel.
/// </summary>
public class PatcherOrchestrator
{
    private readonly GitHubClient _gitHubClient;
    private readonly SetupStateManager _stateManager;

    public PatcherOrchestrator(GitHubClient gitHubClient, SetupStateManager stateManager)
    {
        _gitHubClient = gitHubClient;
        _stateManager = stateManager;
    }

    /// <summary>
    /// Orchestrate the full download-and-patch flow.
    /// Returns the path to the patched executable.
    /// </summary>
    public async Task<string> DownloadAndPatchAsync(
        string selectedFolder,
        ProcessRunner.OutputHandler onOutput,
        IProgress<double>? progress = null)
    {
        var paicomExe = Path.Combine(selectedFolder, "PAIcom.exe");
        if (!File.Exists(paicomExe))
            throw new FileNotFoundException("PAIcom.exe not found in selected folder", paicomExe);

        onOutput("Fetching latest patcher release...");
        progress?.Report(0.1);

        var (downloadUrl, assetName, totalSize) = await _gitHubClient.GetPatcherAssetUrlAsync();
        onOutput($"Found release asset: {assetName} ({totalSize / 1024 / 1024} MB)");

        var tempDir = Path.GetTempPath();
        var tempPatcher = Path.Combine(tempDir, assetName);

        onOutput("Downloading patcher...");
        var downloadProgress = new Progress<double>(p =>
        {
            progress?.Report(0.1 + (p * 0.3));
        });

        await _gitHubClient.DownloadAsync(downloadUrl, tempPatcher, downloadProgress);

        progress?.Report(0.4);
        onOutput("Patcher downloaded, making executable...");

        // Make executable on Unix systems
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPatcher,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        onOutput("Running patcher on PAIcom.exe...");
        progress?.Report(0.5);

        var runner = new ProcessRunner(tempPatcher, new[]
        {
            paicomExe,
            "--out", paicomExe,
            "--migration-mode", "full"
        });

        runner.SetOutputHandler(onOutput);

        await runner.RunAsync();

        progress?.Report(1.0);
        onOutput("Patching completed successfully!");

        _stateManager.SetPatchedExePath(paicomExe);

        // Cleanup temp file
        try { File.Delete(tempPatcher); }
        catch { /* ignore */ }

        return paicomExe;
    }
}
