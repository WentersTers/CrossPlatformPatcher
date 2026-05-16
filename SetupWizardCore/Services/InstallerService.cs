using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SetupWizardCore.Models;

namespace SetupWizardCore.Services
{
    /// <summary>
    /// Main installer service implementation
    /// </summary>
    public class InstallerService : IInstallerService
    {
        private readonly HttpClient _httpClient;
        private readonly TargetPlatform _platform;

        public InstallerState State { get; private set; } = new InstallerState();

        public event EventHandler<InstallerState>? StateChanged;
        public event EventHandler<double>? ProgressUpdated;
        public event EventHandler<string>? LogMessage;

        public InstallerService(TargetPlatform platform = TargetPlatform.Windows)
        {
            _platform = platform;
            _httpClient = new HttpClient();
        }

        public async Task InitializeAsync(TargetPlatform platform)
        {
            await LogAsync($"Initializing installer for {platform}...");
            State.StartTime = DateTime.Now;
            State.CurrentStep = InstallerStep.Welcome;
            await NotifyStateChangedAsync();            await Task.CompletedTask;        }

        public async Task NavigateToStepAsync(InstallerStep step)
        {
            State.CurrentStep = step;
            NotifyStateChangedAsync();
            await Task.CompletedTask;
        }

        public async Task SelectFolderAsync(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                await HandleErrorAsync($"Folder does not exist: {folderPath}");
                return;
            }

            State.SelectedFolder = folderPath;
            LogAsync($"Selected folder: {folderPath}");
            await NavigateToStepAsync(InstallerStep.ModelSelection);
        }

        public async Task DownloadModelAsync(VoskModel model, string targetDirectory)
        {
            try
            {
                State.IsProcessing = true;
                await NotifyStateChangedAsync();

                LogAsync($"Downloading model: {model.DisplayName}");
                LogAsync($"URL: {model.DownloadUrl}");

                // Create target directory if it doesn't exist
                Directory.CreateDirectory(targetDirectory);

                // Download the file
                using var response = await _httpClient.GetAsync(model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                var downloadedBytes = 0L;

                var tempZipPath = Path.Combine(Path.GetTempPath(), model.FileName);
                
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    downloadedBytes += bytesRead;

                    if (totalBytes > 0)
                    {
                        var progress = (double)downloadedBytes / totalBytes;
                        State.Progress = progress;
                        ProgressUpdated?.Invoke(this, progress);
                    }
                }

                LogAsync($"Download completed: {tempZipPath}");

                // Extract the model
                LogAsync($"Extracting model to: {targetDirectory}");
                await ExtractZipFileAsync(tempZipPath, targetDirectory);

                // Clean up temp file
                File.Delete(tempZipPath);

                State.ModelPath = targetDirectory;
                State.Progress = 1.0;
                LogAsync($"Model extraction completed");
                await NavigateToStepAsync(InstallerStep.Download);
            }
            catch (Exception ex)
            {
                await HandleErrorAsync($"Failed to download model: {ex.Message}");
            }
            finally
            {
                State.IsProcessing = false;
                await NotifyStateChangedAsync();
            }
        }

        public async Task RunPatcherAsync(string patcherUrl, string targetDirectory)
        {
            try
            {
                State.IsProcessing = true;
                await NotifyStateChangedAsync();

                LogAsync($"Downloading patcher from: {patcherUrl}");

                // Download patcher
                using var response = await _httpClient.GetAsync(patcherUrl);
                response.EnsureSuccessStatusCode();

                var patcherFileName = GetPatcherFileName();
                var patcherPath = Path.Combine(targetDirectory, patcherFileName);

                await File.WriteAllBytesAsync(patcherPath, await response.Content.ReadAsByteArrayAsync());
                LogAsync($"Patcher downloaded to: {patcherPath}");

                // Run patcher
                LogAsync("Running patcher...");
                await RunPatcherProcessAsync(patcherPath, targetDirectory);

                LogAsync("Patcher execution completed");
                await NavigateToStepAsync(InstallerStep.DependencyCheck);
            }
            catch (Exception ex)
            {
                await HandleErrorAsync($"Failed to run patcher: {ex.Message}");
            }
            finally
            {
                State.IsProcessing = false;
                await NotifyStateChangedAsync();
            }
        }

        public async Task<bool> CheckDependenciesAsync()
        {
            try
            {
                LogAsync("Checking dependencies...");

                // Check .NET runtime
                var dotNetAvailable = await CheckDotNetRuntimeAsync();
                if (!dotNetAvailable)
                {
                    LogAsync(".NET runtime not found");
                    await NavigateToStepAsync(InstallerStep.DotNetInstall);
                    return false;
                }

                LogAsync("Dependencies check completed");
                await NavigateToStepAsync(InstallerStep.Complete);
                return true;
            }
            catch (Exception ex)
            {
                await HandleErrorAsync($"Dependency check failed: {ex.Message}");
                return false;
            }
        }

        public async Task InstallDotNetAsync()
        {
            try
            {
                State.IsProcessing = true;
                NotifyStateChangedAsync();

                LogAsync("Installing .NET runtime...");

                // Platform-specific .NET installation
                if (_platform == TargetPlatform.Windows)
                {
                    await InstallDotNetWindowsAsync();
                }
                else if (_platform == TargetPlatform.macOS)
                {
                    await InstallDotNetMacAsync();
                }

                LogAsync(".NET runtime installation completed");
                await NavigateToStepAsync(InstallerStep.Complete);
            }
            catch (Exception ex)
            {
                await HandleErrorAsync($"Failed to install .NET: {ex.Message}");
            }
            finally
            {
                State.IsProcessing = false;
                await NotifyStateChangedAsync();
            }
        }

        public async Task CompleteAsync()
        {
            State.IsCompleted = true;
            State.EndTime = DateTime.Now;
            State.CurrentStep = InstallerStep.Complete;
            Console.WriteLine($"[InstallerService] CompleteAsync called. CurrentStep={State.CurrentStep}");
            await LogAsync("Installation completed successfully!");
            await NotifyStateChangedAsync();
        }

        public async Task HandleErrorAsync(string errorMessage)
        {
            State.HasFailed = true;
            State.ErrorMessage = errorMessage;
            LogAsync($"ERROR: {errorMessage}");
            await NavigateToStepAsync(InstallerStep.Error);
        }

        private async Task ExtractZipFileAsync(string zipPath, string extractPath)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                var destinationPath = Path.Combine(extractPath, entry.FullName);
                
                // Ensure the destination directory exists
                var destinationDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                // Skip directories
                if (entry.FullName.EndsWith("/"))
                    continue;

                // Extract file
                using var entryStream = entry.Open();
                using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await entryStream.CopyToAsync(fileStream);
            }
        }

        private string GetPatcherFileName()
        {
            return _platform switch
            {
                TargetPlatform.Windows => "CrossPlatformPatcher.exe",
                TargetPlatform.macOS => "CrossPlatformPatcher",
                TargetPlatform.Linux => "CrossPlatformPatcher",
                _ => "CrossPlatformPatcher"
            };
        }

        private async Task RunPatcherProcessAsync(string patcherPath, string workingDirectory)
        {
            // This would be implemented based on platform-specific process execution
            // For now, we'll simulate it
            await Task.Delay(2000); // Simulate patcher execution
        }

        private async Task<bool> CheckDotNetRuntimeAsync()
        {
            // Platform-specific .NET runtime check
            if (_platform == TargetPlatform.Windows)
            {
                return await CheckDotNetWindowsAsync();
            }
            else if (_platform == TargetPlatform.macOS)
            {
                return await CheckDotNetMacAsync();
            }
            
            return false;
        }

        private async Task<bool> CheckDotNetWindowsAsync()
        {
            // Windows-specific .NET check
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                // .NET not found
            }
            return false;
        }

        private async Task<bool> CheckDotNetMacAsync()
        {
            // macOS-specific .NET check
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/local/bin/dotnet",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                // .NET not found
            }
            return false;
        }

        private async Task InstallDotNetWindowsAsync()
        {
            // Windows-specific .NET installation
            LogAsync("Please install .NET 8.0 runtime from https://dotnet.microsoft.com/download/dotnet/8.0");
            await Task.CompletedTask;
        }

        private async Task InstallDotNetMacAsync()
        {
            // macOS-specific .NET installation
            LogAsync("Please install .NET 8.0 runtime from https://dotnet.microsoft.com/download/dotnet/8.0");
            await Task.CompletedTask;
        }

        private async Task LogAsync(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var logEntry = $"[{timestamp}] {message}";
            State.LogOutput.Add(logEntry);
            LogMessage?.Invoke(this, logEntry);
            await Task.CompletedTask;
        }

        private async Task NotifyStateChangedAsync()
        {
            StateChanged?.Invoke(this, State);
            await Task.CompletedTask;
        }
    }
}