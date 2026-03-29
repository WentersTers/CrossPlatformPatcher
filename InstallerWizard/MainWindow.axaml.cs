using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SetupWizard;

public partial class MainWindow : Window
{
    private readonly InstallerWorkflow _workflow;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();

        _workflow = new InstallerWorkflow(AppContext.BaseDirectory);

        DiagnoseButton.Click += OnDiagnoseClicked;
        InstallButton.Click += OnInstallClicked;
        LaunchButton.Click += OnLaunchClicked;
        ExitButton.Click += OnExitClicked;

        UpdateOsSummary();
        Log($"Setup wizard started in: {AppContext.BaseDirectory}");
        Log($"Detected operating system: {_workflow.GetOperatingSystemLabel()}");
        Log("Use 'Install Required Files' to run guided dependency setup.");
    }

    private void UpdateOsSummary()
    {
        var os = _workflow.GetOperatingSystemLabel();
        var arch = RuntimeInformation.ProcessArchitecture;
        SummaryText.Text = $"Operating system: {os} | Architecture: {arch}";
    }

    private async void OnDiagnoseClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await RunBusyAsync("Running diagnostics", async progress =>
        {
            await _workflow.RunDiagnosticsAsync(
                progress,
                log => Dispatcher.UIThread.Post(() => Log(log)));
        });
    }

    private async void OnInstallClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await RunBusyAsync("Installing required files", async progress =>
        {
            await _workflow.InstallRequirementsAsync(
                progress,
                log => Dispatcher.UIThread.Post(() => Log(log)));
        });
    }

    private async void OnLaunchClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var launched = await _workflow.TryLaunchGameAsync(
            log => Dispatcher.UIThread.Post(() => Log(log)));

        FooterText.Text = launched
            ? "PAIcom launch command started."
            : "PAIcom launch command failed. Check log output.";
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task RunBusyAsync(string actionLabel, Func<IProgress<InstallerProgress>, Task> action)
    {
        SetBusy(true, actionLabel);

        try
        {
            var progress = new Progress<InstallerProgress>(p =>
            {
                SetupProgress.Value = p.Percent;
                ProgressLabel.Text = p.Message;
                FooterText.Text = p.Message;
            });

            await action(progress);
            FooterText.Text = $"{actionLabel} completed.";
        }
        catch (Exception ex)
        {
            Log($"[error] {ex.Message}");
            FooterText.Text = $"{actionLabel} failed.";
        }
        finally
        {
            SetBusy(false, "Ready");
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;

        DiagnoseButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        LaunchButton.IsEnabled = !busy;

        ProgressLabel.Text = status;
    }

    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        StatusLog.Text += $"{timestamp} {message}{Environment.NewLine}";
        StatusLog.CaretIndex = StatusLog.Text?.Length ?? 0;
    }
}
