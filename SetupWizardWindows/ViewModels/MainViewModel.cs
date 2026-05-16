using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using SetupWizardCore.Models;
using SetupWizardCore.Services;
using Avalonia.Controls;

namespace SetupWizardWindows.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly IInstallerService _installerService;
        private InstallerStep _currentStep;
        private string? _selectedFolder;
        private VoskModel? _selectedModel;
        private bool _isProcessing;
        private double _progress;
        private string? _errorMessage;
        private string _logOutput = string.Empty;

        public MainViewModel()
        {
            _installerService = new InstallerService(TargetPlatform.Windows);
            _installerService.StateChanged += OnInstallerStateChanged;
            _installerService.ProgressUpdated += OnProgressUpdated;
            _installerService.LogMessage += OnLogMessage;

            // Initialize commands
            SelectFolderCommand = new RelayCommand(async () => await SelectFolderAsync());
            DownloadModelCommand = new RelayCommand(async () => await DownloadModelAsync(), () => SelectedModel != null && !IsProcessing);
            RunPatcherCommand = new RelayCommand(async () => await RunPatcherAsync(), () => !IsProcessing);
            CheckDependenciesCommand = new RelayCommand(async () => await CheckDependenciesAsync(), () => !IsProcessing);
            InstallDotNetCommand = new RelayCommand(async () => await InstallDotNetAsync(), () => !IsProcessing);
            NavigateToStepCommand = new RelayCommand<InstallerStep>(async (step) => await NavigateToStepAsync(step));

            // Initialize available models
            AvailableModels = new ObservableCollection<VoskModel>(VoskModel.GetAvailableModels());

            // Initialize the installer
            Task.Run(async () => await _installerService.InitializeAsync(TargetPlatform.Windows));
        }

        public InstallerStep CurrentStep
        {
            get => _currentStep;
            set => SetProperty(ref _currentStep, value);
        }

        public string? SelectedFolder
        {
            get => _selectedFolder;
            set => SetProperty(ref _selectedFolder, value);
        }

        public VoskModel? SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (SetProperty(ref _selectedModel, value))
                {
                    // Update command availability
                    ((RelayCommand)DownloadModelCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (SetProperty(ref _isProcessing, value))
                {
                    // Update command availability
                    ((RelayCommand)DownloadModelCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)RunPatcherCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)CheckDependenciesCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)InstallDotNetCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

        public string LogOutput
        {
            get => _logOutput;
            set => SetProperty(ref _logOutput, value);
        }

        public ObservableCollection<VoskModel> AvailableModels { get; }

        public ICommand SelectFolderCommand { get; }
        public ICommand DownloadModelCommand { get; }
        public ICommand RunPatcherCommand { get; }
        public ICommand CheckDependenciesCommand { get; }
        public ICommand InstallDotNetCommand { get; }
        public ICommand NavigateToStepCommand { get; }

        private async Task SelectFolderAsync()
        {
            var dialog = new OpenFolderDialog();
            var result = await dialog.ShowAsync(new Window());
            
            if (!string.IsNullOrEmpty(result))
            {
                await _installerService.SelectFolderAsync(result);
            }
        }

        private async Task DownloadModelAsync()
        {
            if (SelectedModel == null || string.IsNullOrEmpty(SelectedFolder))
                return;

            var modelDirectory = System.IO.Path.Combine(SelectedFolder, "models");
            await _installerService.DownloadModelAsync(SelectedModel, modelDirectory);
        }

        private async Task RunPatcherAsync()
        {
            if (string.IsNullOrEmpty(SelectedFolder))
                return;

            // Get the latest patcher URL
            var githubClient = new GitHubApiClient();
            var release = await githubClient.FetchLatestReleaseAsync();
            var asset = githubClient.GetPatcherAsset(release, TargetPlatform.Windows, GitHubApiClient.GetCurrentArchitecture());
            
            if (asset == null)
            {
                await _installerService.HandleErrorAsync("Could not find appropriate patcher for Windows");
                return;
            }

            await _installerService.RunPatcherAsync(asset.BrowserDownloadUrl, SelectedFolder);
        }

        private async Task CheckDependenciesAsync()
        {
            await _installerService.CheckDependenciesAsync();
        }

        private async Task InstallDotNetAsync()
        {
            await _installerService.InstallDotNetAsync();
        }

        private async Task NavigateToStepAsync(InstallerStep step)
        {
            await _installerService.NavigateToStepAsync(step);
        }

        private void OnInstallerStateChanged(object? sender, InstallerState state)
        {
            CurrentStep = state.CurrentStep;
            SelectedFolder = state.SelectedFolder;
            IsProcessing = state.IsProcessing;
            Progress = state.Progress;
            ErrorMessage = state.ErrorMessage;
        }

        private void OnProgressUpdated(object? sender, double progress)
        {
            Progress = progress;
        }

        private void OnLogMessage(object? sender, string message)
        {
            LogOutput += message + Environment.NewLine;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            return _canExecute?.Invoke() ?? true;
        }

        public async void Execute(object? parameter)
        {
            await _execute();
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Func<T?, Task> _execute;
        private readonly Func<T?, bool>? _canExecute;

        public RelayCommand(Func<T?, Task> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            return _canExecute?.Invoke((T?)parameter) ?? true;
        }

        public async void Execute(object? parameter)
        {
            await _execute((T?)parameter);
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}