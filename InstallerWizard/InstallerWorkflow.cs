using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SetupWizard;

public sealed class InstallerWorkflow
{
    private readonly string _baseDir;

    public InstallerWorkflow(string baseDir)
    {
        _baseDir = baseDir;
    }

    public string GetOperatingSystemLabel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "Windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macOS";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "Linux";
        }

        return "Unknown";
    }

    public async Task RunDiagnosticsAsync(IProgress<InstallerProgress> progress, Action<string> log)
    {
        Report(progress, 5, "Checking files");
        var outExe = Path.Combine(_baseDir, "out.exe");
        var runSh = Path.Combine(_baseDir, "run.sh");
        var runBat = Path.Combine(_baseDir, "run.bat");

        log(File.Exists(outExe)
            ? "[ok] Found out.exe"
            : "[warn] out.exe was not found in this folder.");

        log(File.Exists(runSh)
            ? "[ok] Found run.sh"
            : "[warn] run.sh was not found in this folder.");

        log(File.Exists(runBat)
            ? "[ok] Found run.bat"
            : "[warn] run.bat was not found in this folder.");

        Report(progress, 35, "Checking runtime dependencies");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            log("[ok] Windows detected. No Wine dependency required.");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var whisky = await WhichAsync("whisky");
            var wine = await WhichAsync("wine");

            if (!string.IsNullOrWhiteSpace(whisky))
            {
                log($"[ok] Whisky detected at {whisky}");
            }
            else if (!string.IsNullOrWhiteSpace(wine))
            {
                log($"[ok] Wine detected at {wine}");
            }
            else
            {
                log("[warn] Neither Whisky nor Wine was detected.");
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var wine = await WhichAsync("wine");
            if (!string.IsNullOrWhiteSpace(wine))
            {
                log($"[ok] Wine detected at {wine}");
            }
            else
            {
                log("[warn] Wine was not detected.");
            }
        }
        else
        {
            log("[warn] Unsupported operating system for automatic diagnostics.");
        }

        Report(progress, 70, "Checking launcher entry points");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            log("[info] Launcher command: run.bat");
        }
        else
        {
            log("[info] Launcher command: sh run.sh");
        }

        Report(progress, 100, "Diagnostics completed");
    }

    public async Task InstallRequirementsAsync(IProgress<InstallerProgress> progress, Action<string> log)
    {
        Report(progress, 5, "Preparing guided setup");
        var os = GetOperatingSystemLabel();
        log($"[setup] Starting setup for {os}");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await InstallOnWindowsAsync(progress, log);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await InstallOnLinuxAsync(progress, log);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await InstallOnMacAsync(progress, log);
            return;
        }

        log("[error] Automatic setup is not available on this operating system.");
        Report(progress, 100, "Setup halted");
    }

    public Task<bool> TryLaunchGameAsync(Action<string> log)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var runBat = Path.Combine(_baseDir, "run.bat");
            if (!File.Exists(runBat))
            {
                log("[error] run.bat was not found.");
                return Task.FromResult(false);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = runBat,
                WorkingDirectory = _baseDir,
                UseShellExecute = true
            });

            log("[ok] run.bat launched.");
            return Task.FromResult(true);
        }

        var runSh = Path.Combine(_baseDir, "run.sh");
        if (!File.Exists(runSh))
        {
            log("[error] run.sh was not found.");
            return Task.FromResult(false);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { runSh },
            WorkingDirectory = _baseDir,
            UseShellExecute = false
        });

        log("[ok] run.sh launched.");
        return Task.FromResult(true);
    }

    private Task InstallOnWindowsAsync(IProgress<InstallerProgress> progress, Action<string> log)
    {
        Report(progress, 30, "Validating Windows launcher files");

        var outExe = Path.Combine(_baseDir, "out.exe");
        var runBat = Path.Combine(_baseDir, "run.bat");

        if (!File.Exists(outExe))
        {
            log("[warn] out.exe is missing. Patch output may not be ready yet.");
        }

        if (!File.Exists(runBat))
        {
            log("[warn] run.bat is missing.");
        }

        log("[ok] Windows setup completed.");
        Report(progress, 100, "Windows setup completed");

        return Task.CompletedTask;
    }

    private async Task InstallOnLinuxAsync(IProgress<InstallerProgress> progress, Action<string> log)
    {
        Report(progress, 20, "Checking Wine installation");

        var winePath = await WhichAsync("wine");
        if (!string.IsNullOrWhiteSpace(winePath))
        {
            log($"[ok] Wine already installed at {winePath}");
            Report(progress, 100, "Linux setup completed");
            return;
        }

        log("[setup] Wine not found. Attempting install through your package manager.");
        Report(progress, 40, "Detecting package manager");

        string? installCommand = null;
        if (await CommandExistsAsync("apt"))
        {
            installCommand = "sudo dpkg --add-architecture i386 && sudo apt update && sudo apt install -y wine wine32 wine64";
        }
        else if (await CommandExistsAsync("dnf"))
        {
            installCommand = "sudo dnf install -y wine";
        }
        else if (await CommandExistsAsync("pacman"))
        {
            installCommand = "sudo pacman -S --noconfirm wine";
        }

        if (string.IsNullOrWhiteSpace(installCommand))
        {
            log("[error] No supported package manager was detected.");
            log("[info] Follow manual instructions in SETUP_LINUX.md.");
            Report(progress, 100, "Linux setup needs manual completion");
            return;
        }

        Report(progress, 55, "Running package installation");
        var result = await RunShellCommandAsync(installCommand, log);

        if (result.ExitCode != 0)
        {
            log($"[warn] Installation command exited with code {result.ExitCode}.");
            log($"[info] You can run this command manually: {installCommand}");
        }

        Report(progress, 85, "Verifying Wine installation");
        winePath = await WhichAsync("wine");
        if (!string.IsNullOrWhiteSpace(winePath))
        {
            log($"[ok] Wine installed at {winePath}");
            Report(progress, 100, "Linux setup completed");
            return;
        }

        log("[warn] Wine still not detected after installer run.");
        log("[info] Follow manual instructions in SETUP_LINUX.md.");
        Report(progress, 100, "Linux setup needs manual completion");
    }

    private async Task InstallOnMacAsync(IProgress<InstallerProgress> progress, Action<string> log)
    {
        Report(progress, 20, "Checking Whisky/Wine");

        var whiskyPath = await WhichAsync("whisky");
        var winePath = await WhichAsync("wine");

        if (!string.IsNullOrWhiteSpace(whiskyPath) || !string.IsNullOrWhiteSpace(winePath))
        {
            log(!string.IsNullOrWhiteSpace(whiskyPath)
                ? $"[ok] Whisky detected at {whiskyPath}"
                : $"[ok] Wine detected at {winePath}");
            Report(progress, 100, "macOS setup completed");
            return;
        }

        Report(progress, 35, "Checking Homebrew");
        var brewPath = await WhichAsync("brew");
        if (string.IsNullOrWhiteSpace(brewPath))
        {
            log("[setup] Homebrew is not installed. Installing Homebrew first.");
            var brewInstall = await RunShellCommandAsync(
                "/bin/bash -c \"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"",
                log);

            if (brewInstall.ExitCode != 0)
            {
                log("[error] Homebrew installation failed.");
                log("[info] Install Homebrew manually from https://brew.sh and rerun setup.");
                Report(progress, 100, "macOS setup needs manual completion");
                return;
            }
        }

        Report(progress, 60, "Installing Whisky with Homebrew");
        var whiskyInstall = await RunShellCommandAsync("brew install --cask whisky", log);
        if (whiskyInstall.ExitCode != 0)
        {
            log("[warn] Automatic Whisky installation failed.");
            log("[info] Install manually with: brew install --cask whisky");
            Report(progress, 100, "macOS setup needs manual completion");
            return;
        }

        Report(progress, 85, "Verifying Whisky");
        whiskyPath = await WhichAsync("whisky");
        if (!string.IsNullOrWhiteSpace(whiskyPath))
        {
            log($"[ok] Whisky installed at {whiskyPath}");
            Report(progress, 100, "macOS setup completed");
            return;
        }

        log("[warn] Whisky was not detected after installation.");
        log("[info] Open a new terminal and run: brew install --cask whisky");
        Report(progress, 100, "macOS setup needs manual completion");
    }

    private static void Report(IProgress<InstallerProgress> progress, int percent, string message)
    {
        progress.Report(new InstallerProgress(percent, message));
    }

    private async Task<bool> CommandExistsAsync(string command)
    {
        var path = await WhichAsync(command);
        return !string.IsNullOrWhiteSpace(path);
    }

    private async Task<string?> WhichAsync(string command)
    {
        var probeCommand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"where {command}"
            : $"command -v {command}";

        var result = await RunShellCommandAsync(probeCommand, _ => { }, includeCommandInLog: false);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var firstLine = result.Output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine.Trim();
    }

    private async Task<CommandResult> RunShellCommandAsync(string command, Action<string> log, bool includeCommandInLog = true)
    {
        if (includeCommandInLog)
        {
            log($"[cmd] {command}");
        }

        var psi = BuildShellStartInfo(command);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var lines = new List<string>();

        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                lock (lines)
                {
                    lines.Add(args.Data);
                }
                log(args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                lock (lines)
                {
                    lines.Add(args.Data);
                }
                log($"[stderr] {args.Data}");
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        var output = string.Join(Environment.NewLine, lines);
        return new CommandResult(process.ExitCode, output);
    }

    private static ProcessStartInfo BuildShellStartInfo(string command)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                ArgumentList = { "/c", command },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-lc", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private readonly record struct CommandResult(int ExitCode, string Output);
}

public readonly record struct InstallerProgress(int Percent, string Message);
