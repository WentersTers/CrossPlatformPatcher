using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SetupWizardCore.Models;
using SetupWizardCore.Services;
using Avalonia.Platform.Storage;

namespace SetupWizardWindows.ViewModels;

/// <summary>
/// Main view model for the setup wizard.
/// Port of Swift SetupWizardViewModel.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public enum Screen
    {
        Welcome,
        FolderPicker,
        ModelSelection,
        Download,
        DependencyCheck,
        DotNetInstall,
        Complete,
        Error
    }

    // Services
    private readonly SetupStateManager _stateManager;
    private readonly GitHubClient _gitHubClient;
    private readonly DependencyChecker _dependencyChecker;
    private readonly DotNetInstaller _dotNetInstaller;
    private readonly ModelDownloader _modelDownloader;
    private readonly PatcherOrchestrator _patcherOrchestrator;

    // Folder picker top-level reference (set from MainWindow code-behind)
    public Visual? TopLevelVisual { get; set; }

    public MainViewModel(
        SetupStateManager stateManager,
        GitHubClient gitHubClient,
        DependencyChecker dependencyChecker,
        DotNetInstaller dotNetInstaller,
        ModelDownloader modelDownloader,
        PatcherOrchestrator patcherOrchestrator)
    {
        _stateManager = stateManager;
        _gitHubClient = gitHubClient;
        _dependencyChecker = dependencyChecker;
        _dotNetInstaller = dotNetInstaller;
        _modelDownloader = modelDownloader;
        _patcherOrchestrator = patcherOrchestrator;

        // Clean up any stale processes
        _stateManager.CleanupStaleProcesses();

        SelectedModel = ModelDownloader.VoskModelInfo.SmallEnUs;
    }

    // ===== Observable Properties =====

    [ObservableProperty]
    private Screen _currentScreen = Screen.Welcome;

    [ObservableProperty]
    private string? _selectedFolder;

    [ObservableProperty]
    private ObservableCollection<string> _logOutput = new();

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _modelPath;

    [ObservableProperty]
    private ModelDownloader.VoskModelInfo? _selectedModel;

    [ObservableProperty]
    private bool _hasWine;

    [ObservableProperty]
    private string? _wineType;

    public IReadOnlyList<ModelDownloader.VoskModelInfo> AvailableModels => ModelDownloader.VoskModelInfo.All;

    // ===== Navigation Commands =====

    [RelayCommand]
    private void GoToFolderPicker()
    {
        CurrentScreen = Screen.FolderPicker;
        _stateManager.SetCurrentStep(SetupState.Step.FolderPicker);
    }

    [RelayCommand]
    private void GoToModelSelection()
    {
        CurrentScreen = Screen.ModelSelection;
        _stateManager.SetCurrentStep(SetupState.Step.ModelSelection);
    }

    [RelayCommand]
    private void GoToDownload()
    {
        CurrentScreen = Screen.Download;
        _stateManager.SetCurrentStep(SetupState.Step.Download);
    }

    [RelayCommand]
    private void GoToDependencyCheck()
    {
        CurrentScreen = Screen.DependencyCheck;
        _stateManager.SetCurrentStep(SetupState.Step.DependencyCheck);
    }

    [RelayCommand]
    private void GoToDotNetInstall()
    {
        CurrentScreen = Screen.DotNetInstall;
        _stateManager.SetCurrentStep(SetupState.Step.DotNetInstall);
    }

    [RelayCommand]
    private void GoToComplete()
    {
        CurrentScreen = Screen.Complete;
        _stateManager.MarkComplete();
    }

    [RelayCommand]
    private void ShowError(string message)
    {
        ErrorMessage = message;
        CurrentScreen = Screen.Error;
        _stateManager.MarkFailed(message);
    }

    // ===== Folder Selection =====

    [RelayCommand]
    private async Task SelectFolderAsync()
    {
        if (TopLevelVisual == null) return;

        var topLevel = TopLevel.GetTopLevel(TopLevelVisual);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select PAIcom Folder"
        });

        var folder = folders?.FirstOrDefault();
        if (folder != null)
        {
            var path = folder.Path.LocalPath;
            SelectedFolder = path;
            _stateManager.SetSelectedFolder(path);
        }
    }

    // ===== Download & Patch =====

    [RelayCommand]
    private async Task DownloadAndPatchAsync()
    {
        IsProcessing = true;
        LogOutput.Clear();
        Progress = 0.0;

        try
        {
            if (string.IsNullOrEmpty(SelectedFolder))
            {
                ShowErrorCommand.Execute("No folder selected");
                return;
            }

            var paicomExe = Path.Combine(SelectedFolder, "PAIcom.exe");
            if (!File.Exists(paicomExe))
            {
                ShowErrorCommand.Execute("PAIcom.exe not found in selected folder");
                return;
            }

            var progressReporter = new Progress<double>(p =>
            {
                // Schedule on UI thread via main thread dispatcher
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Progress = p);
            });

            void onOutput(string line)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => LogOutput.Add(line));
            }

            await _patcherOrchestrator.DownloadAndPatchAsync(
                SelectedFolder, onOutput, progressReporter);

            Progress = 1.0;
            GoToCompleteCommand.Execute(null);
        }
        catch (Exception ex)
        {
            ShowErrorCommand.Execute($"Download/patch failed: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    // ===== Dependency Check =====

    [RelayCommand]
    private void CheckDependencies()
    {
        LogOutput.Clear();
        AddLog("Checking dependencies...");

        var deps = _dependencyChecker.CheckAll();

        switch (deps.whisky.Status)
        {
            case DependencyChecker.DependencyStatus.Installed:
                AddLog($"✓ Found Whisky at: {deps.whisky.Path}");
                HasWine = true;
                WineType = "whisky";
                return;
            default:
                AddLog("✗ Whisky not found");
                break;
        }

        switch (deps.wine.Status)
        {
            case DependencyChecker.DependencyStatus.Installed:
                AddLog($"✓ Found Wine at: {deps.wine.Path}");
                HasWine = true;
                WineType = "system_wine";
                return;
            default:
                AddLog("✗ Wine not found");
                break;
        }

        AddLog("No Wine/Whisky installation detected");
        HasWine = false;
        WineType = null;
    }

    // ===== Model Download =====

    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (SelectedModel == null) return;

        IsProcessing = true;
        LogOutput.Clear();
        Progress = 0.0;

        try
        {
            if (string.IsNullOrEmpty(SelectedFolder))
            {
                ShowErrorCommand.Execute("No folder selected");
                return;
            }

            var modelDir = Path.Combine(SelectedFolder, "models");

            AddLog("=== Starting Model Download ===");
            AddLog($"Model: {SelectedModel.Id}");
            AddLog($"Destination: {modelDir}");

            var progressReporter = new Progress<double>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Progress = p;
                    if (p > 0 && p < 1.0)
                        AddLog($"Download progress: {(int)(p * 100)}%");
                });
            });

            await _modelDownloader.DownloadModelAsync(SelectedModel, modelDir, progressReporter);

            AddLog("✓ Model downloaded successfully!");
            ModelPath = Path.Combine(modelDir, SelectedModel.Id);
            AddLog($"Model path: {ModelPath}");

            // Verify
            if (ModelPath != null && Directory.Exists(ModelPath))
            {
                var contents = Directory.GetFileSystemEntries(ModelPath);
                AddLog($"✓ Model folder verified with {contents.Length} files/folders");
            }
        }
        catch (Exception ex)
        {
            AddLog($"✗ ERROR: Model download failed: {ex.Message}");
            ShowErrorCommand.Execute($"Model download failed: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    // ===== .NET Installation =====

    [RelayCommand]
    private async Task InstallDotNetAsync()
    {
        IsProcessing = true;
        LogOutput.Clear();
        Progress = 0.0;

        try
        {
            var winePrefix = GetWinePrefix();

            var progressReporter = new Progress<double>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Progress = p);
            });

            void onOutput(string line)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => LogOutput.Add(line));
            }

            await _dotNetInstaller.InstallDotNetAsync(winePrefix, onOutput);

            AddLog("✓ .NET installation completed!");
            Progress = 1.0;
        }
        catch (Exception ex)
        {
            AddLog($"✗ .NET installation failed: {ex.Message}");
            ShowErrorCommand.Execute($".NET installation failed: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    // ===== Cancellation =====

    [RelayCommand]
    private async Task CancelAsync()
    {
        AddLog("Cancelling operation...");
        await _dotNetInstaller.CancelAsync();
        IsProcessing = false;
    }

    // ===== Helpers =====

    private void AddLog(string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LogOutput.Add(message);
            SetupWizardLogger.Instance.Debug(message);
        });
    }

    private static string GetWinePrefix()
    {
        // Return the default wine prefix based on platform
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".wine");
    }
}
