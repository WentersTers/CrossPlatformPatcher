using System;
using System.Threading.Tasks;
using SetupWizardCore.Models;

namespace SetupWizardCore.Services
{
    /// <summary>
    /// Interface for the main installer service
    /// </summary>
    public interface IInstallerService
    {
        /// <summary>
        /// Gets the current installer state
        /// </summary>
        InstallerState State { get; }

        /// <summary>
        /// Event raised when the state changes
        /// </summary>
        event EventHandler<InstallerState> StateChanged;

        /// <summary>
        /// Event raised when progress is updated
        /// </summary>
        event EventHandler<double> ProgressUpdated;

        /// <summary>
        /// Event raised when a log message is available
        /// </summary>
        event EventHandler<string> LogMessage;

        /// <summary>
        /// Initialize the installer for the specified platform
        /// </summary>
        Task InitializeAsync(TargetPlatform platform);

        /// <summary>
        /// Navigate to the specified step
        /// </summary>
        Task NavigateToStepAsync(InstallerStep step);

        /// <summary>
        /// Select the installation folder
        /// </summary>
        Task SelectFolderAsync(string folderPath);

        /// <summary>
        /// Select and download a Vosk model
        /// </summary>
        Task DownloadModelAsync(VoskModel model, string targetDirectory);

        /// <summary>
        /// Download and run the patcher
        /// </summary>
        Task RunPatcherAsync(string patcherUrl, string targetDirectory);

        /// <summary>
        /// Check dependencies
        /// </summary>
        Task<bool> CheckDependenciesAsync();

        /// <summary>
        /// Install .NET runtime if needed
        /// </summary>
        Task InstallDotNetAsync();

        /// <summary>
        /// Complete the installation
        /// </summary>
        Task CompleteAsync();

        /// <summary>
        /// Handle errors
        /// </summary>
        Task HandleErrorAsync(string errorMessage);
    }
}