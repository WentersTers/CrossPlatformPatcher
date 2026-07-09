# Cross-Platform SetupWizard — Migration Plan

## Purpose

Port all functionality of the existing [`SetupWizardMacApp/`](../SetupWizardMacApp/) (a SwiftUI macOS-native app) into a **cross-platform .NET 8.0 Avalonia UI application** that runs on **Windows, Linux (including AppImage), and macOS** — while leaving the existing Swift app completely intact as the macOS-native distribution path.

---

## Technology Stack

| Layer | Choice | Rationale |
|---|---|---|
| GUI Framework | **Avalonia UI** | Cross-platform desktop (Windows, Linux, macOS). Supports .NET 8.0. Same architectural patterns as WPF but portable. |
| Runtime | **.NET 8.0** | Already the project's base runtime ([`CrossPlatformPatcher.csproj`](../CrossPlatformPatcher.csproj:5)). Single-file publish and trimming available. |
| Dependency Injection | **Microsoft.Extensions.DependencyInjection** (no third-party) | Native .NET DI, lightweight, sufficient for this scope. |
| HTTP Client | **System.Net.Http.HttpClient** | Cross-platform, already available in .NET 8.0. Replaces `URLSession` from Swift. |
| Zip Extraction | **System.IO.Compression.ZipFile** | Cross-platform zip handling. Replaces `/usr/bin/unzip` shell call from Swift. |
| Process Execution | **System.Diagnostics.Process** | Cross-platform. Replaces Foundation `Process` from Swift. |
| JSON | **System.Text.Json** | Built into .NET 8.0. Replaces `JSONDecoder` from Swift. |
| Logging | **Microsoft.Extensions.Logging** | Structured logging, file + console sinks available. |
| AppImage Packaging | **`appimagetool`** (external CLI) | Converts published Linux build into AppImage format. |

---

## Architecture Overview

### Three-Project Layout

```
SetupWizardCore/           ← SHARED LIBRARY (netstandard2.0 / net8.0)
  └── All non-UI business logic

SetupWizardWindows/        ← AVALONIA UI APP (net8.0)
  └── Cross-platform GUI (Win/Linux/Mac)

SetupWizardMacApp/         ← UNCHANGED EXISTING SWIFT APP
  └── macOS-native .app bundle (untouched)
```

### Dependency Flow

```
┌──────────────────────────────────────────────────┐
│                  SetupWizardWindows               │
│  (Avalonia UI App — Win/Linux/Mac .NET build)     │
│                                                   │
│   Views ───> ViewModel ──> SetupWizardCore        │
└──────────────────────────────────────────────────┘
                              │
                              ▼
                ┌─────────────────────────┐
                │     SetupWizardCore      │
                │  (Shared Business Logic)  │
                │                          │
                │  ┌──────────────────────┐│
                │  │ GitHubClient          ││
                │  │ ModelDownloader       ││
                │  │ PatcherOrchestrator   ││
                │  │ DependencyChecker     ││
                │  │ DotNetInstaller       ││
                │  │ StateManager          ││
                │  │ ProcessRunner         ││
                │  │ Logger                ││
                │  └──────────────────────┘│
                └─────────────────────────┘
                              │
                              ▼
                ┌─────────────────────────┐
                │  CrossPlatformPatcher    │
                │  (Core/ — unchanged)     │
                │  Existing IL rewriters,  │
                │  launcher generators,    │
                │  resource embedders      │
                └─────────────────────────┘
```

---

## File-by-File Mapping: Swift → C#

Each existing Swift service in [`SetupWizardMacApp/Sources/SetupWizard/Services/`](../SetupWizardMacApp/Sources/SetupWizard/Services/) maps to a C# equivalent in [`SetupWizardCore/`](../SetupWizardCore/).

### 1. GitHub API Client

| Swift Source | C# Destination | Key Differences from Swift |
|---|---|---|
| [`APIClient.swift`](../SetupWizardMacApp/Sources/SetupWizard/Services/APIClient.swift) | `SetupWizardCore/Services/GitHubClient.cs` | Use `HttpClient` instead of `URLSession`. Use `System.Text.Json` instead of `JSONDecoder`. Platform detection uses `System.Runtime.InteropServices.RuntimeInformation` instead of `uname()`. |

**Functions to port:**

- **`FetchLatestReleaseAsync()`** — Calls GitHub Releases API, returns release metadata (tag name, assets list). Handles 404 fallback to list endpoint.
- **`GetPatcherAssetUrlAsync()`** — Selects the correct patcher asset by OS+architecture using `RuntimeInformation.OSDescription` and `RuntimeInformation.ProcessArchitecture`. Currently Swift selects `CrossPlatformPatcher-M-Arm` or `CrossPlatformPatcher-M-x64` — the C# version must select from: `CrossPlatformPatcher-W-x64`, `CrossPlatformPatcher-L-x64`, `CrossPlatformPatcher-M-x64`, `CrossPlatformPatcher-M-Arm`. Remove the `getArchitecture()` private method that uses `uname()`.
- **`DownloadAsync()`** — Downloads a file from URL to a local path with progress reporting via `IProgress<double>` callback.

**OS detection mapping:**
| RuntimeIdentifier | `OSPlatform` Check | Asset to Download |
|---|---|---|
| `win-x64` | `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` | `CrossPlatformPatcher-W-x64` |
| `linux-x64` | `RuntimeInformation.IsOSPlatform(OSPlatform.Linux)` && `Architecture.X64` | `CrossPlatformPatcher-L-x64` |
| `osx-x64` | `RuntimeInformation.IsOSPlatform(OSPlatform.OSX)` && `Architecture.X64` | `CrossPlatformPatcher-M-x64` |
| `osx-arm64` | `RuntimeInformation.IsOSPlatform(OSPlatform.OSX)` && `Architecture.Arm64` | `CrossPlatformPatcher-M-Arm` |

---

### 2. Vosk Model Downloader

| Swift Source | C# Destination | Key Differences from Swift |
|---|---|---|
| [`VoskModelDownloader.swift`](../SetupWizardMacApp/Sources/SetupWizard/Services/VoskModelDownloader.swift) | `SetupWizardCore/Services/ModelDownloader.cs` | Replace `/usr/bin/unzip` with `ZipFile.ExtractToDirectory()`. Use `HttpClient` for downloads instead of `URLSession`. Remove `NSTemporaryDirectory()`, use `Path.GetTempPath()`. |

**Functions to port:**

- **`DownloadModelAsync()`** — Downloads a Vosk model zip from the Alphacephei CDN, saves to temp path, extracts to a target models directory. Reports download progress. Verifies extraction by checking directory contents.
- **`DownloadCustomModelAsync()`** — Accepts either a URL (download then extract) or a local file path (extract directly). Determines model name from the path or a provided name parameter.
- **`VoskModel` enum** — Port the 4 model options (`small-en-us-0.15`, `en-us-0.22`, `en-us-0.22-lgraph`, `en-us-0.42-gigaspeech`) as either a C# enum with `DisplayName` and `DownloadUrl` attributes, or as a static class with named instances. The model selection presented to the user should preserve the same descriptions (fast/lightweight, balanced, large graph, large/accurate).

**Important platform change:** The Swift version calls `/usr/bin/unzip` as a shell process for local zip files. The C# version must use `System.IO.Compression.ZipFile.ExtractToDirectory()` instead, which is natively cross-platform and avoids external dependencies.

---

### 3. Process Runner

| Swift Source | C# Destination | Key Differences from Swift |
|---|---|---|
| [`ProcessRunner.swift`](../SetupWizardMacApp/Sources/SetupWizard/Services/ProcessRunner.swift) | `SetupWizardCore/Services/ProcessRunner.cs` | Use `System.Diagnostics.Process` instead of Foundation `Process`. Implement `IAsyncDisposable` for cleanup. Use `StreamReader` for output capture instead of `Pipe`. |

**Functions to port:**

- **`RunAsync()`** — Executes a command with arguments. Captures stdout and stderr in real-time via `DataReceivedEventHandler`. Reports lines via a callback. Returns exit code. Sets working directory and environment variables.
- **`Cancel()`** / **`CancelAsync()`** — Sends `SIGTERM` (via `Process.CloseMainWindow()` or `Process.Kill()` on Windows) or `Process.Kill(entireProcessTree: true)` for forceful termination. Implements a 2-stage cancellation: graceful (wait 5s) then forceful.
- **`OutputLines`** — Exposes captured output for diagnostics.

**Cancellation semantics:** The Swift version sends SIGTERM then SIGKILL. On .NET: use `Process.CloseMainWindow()` for Windows graceful shutdown, `Process.Kill()` as fallback. On Linux/macOS, setting `Process.StartInfo.RedirectStandardInput` and sending Ctrl+C equivalent is not directly possible — use `Process.Kill(entireProcessTree: true)` after a grace period.

---

### 4. Dependency Checker

| Swift Source | C# Destination | Key Differences from Swift |
|---|---|---|
| [`DependencyChecker.swift`](../SetupWizardMacApp/Sources/SetupWizard/Services/DependencyChecker.swift) | `SetupWizardCore/Services/DependencyChecker.cs` | Platform-agnostic design: abstract base with platform-specific implementations registered via DI. Separate Wine detection into a `WineDetector` that has Windows, Linux, and macOS strategies. |

**Functions to port:**

- **`CheckWineAsync()`** — Returns installed Wine path or null. Must implement **three platform strategies**:
  - *Windows:* Wine is not typically needed natively, but could check for bundled Wine or WSL. For Windows, return "native" as the runtime type.
  - *Linux:* Checks `PATH` for `wine` and `wine64`, checks common paths (`/usr/bin/wine`, `/usr/local/bin/wine`). Checks Wine prefix status.
  - *macOS:* Checks `/Applications/Whisky.app/Contents/MacOS/wine`, `/opt/homebrew/bin/wine64`, `/usr/local/bin/wine64`, and `PATH`. Checks Whisky CLI (`whisky` command).
- **`CheckHomebrewAsync()`** — macOS-only: checks `/opt/homebrew/bin/brew` and `/usr/local/bin/brew`. For Linux/Windows: return "not applicable" status.
- **`CheckWhiskyAsync()`** — macOS-only: checks `/Applications/Whisky.app`. For Linux/Windows: return "not applicable".
- **`GetArchitecture()`** — Use `RuntimeInformation.OSArchitecture` and `RuntimeInformation.OSDescription` instead of `uname()`.
- **`RecommendedWineSetup()`** — Returns the best available runtime: `"whisky"` (macOS), `"system_wine"` (Linux), `"native"` (Windows), or `"none"`.

**DependencyStatus enum** — Port as a C# enum with cases: `Installed(path: string)`, `NotInstalled`, `Partial`.

---

### 5. .NET Installer

| Swift Source | C# Destination | Key Differences from Swift |
|---|---|---|
| [`DotNetInstaller.swift`](../SetupWizardMacApp/Sources/SetupWizard/Services/DotNetInstaller.swift) | `SetupWizardCore/Services/DotNetInstaller.cs` | Platform-agnostic with strategy pattern. Windows version uses `dotnet --info` or registry check instead of winetricks. Linux version uses winetricks directly (system winetricks via apt/pacman). macOS version preserves Whisky bottle path. |

**Installation strategies to port:**

- **Strategy 1 — Bundled winetricks (macOS):** The Swift version uses a hardcoded path `/Applications/CrossPlatformPatcher/SetupWizardApp.app/Contents/Resources/winetricks`. The C# version must resolve the bundled winetricks from the app's own resource directory (platform-appropriate path).
- **Strategy 1b — Bundled winetricks (Linux):** Same logic but resolves to `$APP_DIR/Resources/winetricks` or similar.
- **Strategy 1c — Native (Windows):** Check if .NET Framework 4.8 is already installed via registry key `HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full\Release` (value >= 528040 indicates 4.8). If not present, download and run the .NET 4.8 runtime installer from Microsoft.
- **Strategy 2 — System winetricks (Linux/macOS):** Same as Swift: find winetricks in `PATH`, `/opt/homebrew/bin/`, `/usr/local/bin/`. Run `winetricks dotnet48`.
- **Strategy 3 — Registry approach (macOS/Linux):** Direct Wine registry manipulation as fallback.

**Important structural change:** The Swift version assumes the wine prefix exists and the app is inside a Whisky bottle. The C# version must determine the wine prefix location based on the platform:
- *macOS:* `~/.wine/` or Whisky bottle path
- *Linux:* `$WINEPREFIX` or `~/.wine-prefix/`
- *Windows:* N/A, use native framework detection

---

### 6. State Manager

| Swift Source | C# Destination | Key Differences from Swift |
|---|---|---|
| [`SetupStateManager.swift`](../SetupWizardMacApp/Sources/SetupWizard/Services/SetupStateManager.swift) + [`SetupStateFile.swift`](../SetupWizardMacApp/Sources/SetupWizard/Models/SetupStateFile.swift) | `SetupWizardCore/Services/SetupStateManager.cs` + `SetupWizardCore/Models/SetupState.cs` | Use `System.Text.Json` serialization. Store state file in platform-appropriate directory: `Environment.SpecialFolder.ApplicationData` (.NET cross-platform API) instead of hardcoded `~/.wine/`. |

**Data model to port:**

The [`SetupStateFile`](../SetupWizardMacApp/Sources/SetupWizard/Models/SetupStateFile.swift) struct has these fields that must be preserved in the C# model:
- `CurrentStep` (enum: Welcome, FolderPicker, ModelSelection, Download, DependencyCheck, DotNetInstall, Complete)
- `SelectedFolder` (string? — path to PAIcom folder)
- `PatchedExePath` (string?)
- `SelectedWineType` (string? — "whisky", "system_wine", "native")
- `DotNetStatus` (enum: NotStarted, InProgress, Completed, Failed, Cancelled)
- `DotNetError` (string?)
- `WizardStatus` (enum)
- `CompletionTime` (DateTime?)
- `LastError` (string?)

**Platform-appropriate state directory:**
| Platform | State Directory |
|---|---|
| Windows | `%APPDATA%/CrossPlatformPatcher/` |
| Linux | `~/.config/CrossPlatformPatcher/` or `$XDG_CONFIG_HOME/CrossPlatformPatcher/` |
| macOS | `~/Library/Application Support/CrossPlatformPatcher/` |

Use `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)` which maps correctly on all three platforms.

**Functions to port:**
- **State Loading/Saving** — Load from JSON file on startup, save after each state change.
- **Individual setters** — `SetCurrentStep()`, `SetSelectedFolder()`, `SetPatchedExePath()`, `SetSelectedWineType()`, `SetDotNetStatus()`, `MarkComplete()`
- **CleanupStaleProcesses()** — Check if a previously recorded operation PID is still running; kill if stale.

---

### 7. Logger

| Swift Source | C# Destination | Key Differences from Swift |
|---|---|---|
| [`Logger.swift`](../SetupWizardMacApp/Sources/SetupWizard/Utilities/Logger.swift) | `SetupWizardCore/Services/SetupWizardLogger.cs` | Use `ILogger<T>` pattern from `Microsoft.Extensions.Logging`. Write to file + console via standard logging providers. Use platform-appropriate log directory (same state directory logic). |

**Functions to port:**
- **Log(level, message)** — Timestamped, level-prefixed log entries.
- **Debug()**, **Info()**, **Error()** — Level-specific convenience methods.
- **GetLogPath()** — Return the current log file path for display in UI.

**Platform log file location:**
- Same directory pattern as the state manager: `ApplicationData/CrossPlatformPatcher/setup-wizard.log`

---

### 8. Folder Selection Dialog

| Swift Source | C# Destination | Key Differences from Swift |
|---|---|---|
| [`SetupWizardViewModel.swift:selectFolderDialog()`](../SetupWizardMacApp/Sources/SetupWizard/Models/SetupWizardViewModel.swift:109-123) | `SetupWizardWindows/ViewModels/MainViewModel.cs` | Use Avalonia's `FolderDialog` (via `StorageProvider.OpenFolderPickerAsync()`) instead of `NSOpenPanel`. |

The Swift version uses:
```swift
let panel = NSOpenPanel()
panel.canChooseDirectories = true
panel.canChooseFiles = false
panel.allowsMultipleSelection = false
```

Avalonia equivalent (Avalonia 11+):
- Use `TopLevel.GetTopLevel(visual).StorageProvider.OpenFolderPickerAsync()` with `FolderPickerOpenOptions` set to `AllowMultiple = false`.
- The result is a list of `IStorageFolder` — take the first item's `Path.LocalPath`.

---

### 9. ViewModel (Orchestrator)

| Swift Source | C# Destination | Key Differences from Swift |
|---|---|---|
| [`SetupWizardViewModel.swift`](../SetupWizardMacApp/Sources/SetupWizard/Models/SetupWizardViewModel.swift) | `SetupWizardWindows/ViewModels/MainViewModel.cs` | Use Avalonia's `INotifyPropertyChanged` / `ObservableObject` pattern with `[ObservableProperty]` source generators. Business logic calls into `SetupWizardCore` services. |

**Swift properties to port to the C# ViewModel:**
- `CurrentScreen` (enum: Welcome, FolderPicker, ModelSelection, Download, DependencyCheck, DotNetInstall, Complete, Error)
- `SelectedFolder` (string?)
- `LogOutput` (ObservableCollection<string>)
- `IsProcessing` (bool)
- `Progress` (double, 0.0–1.0)
- `ErrorMessage` (string?)
- `ModelPath` (string?)

**Swift methods to port as IAsyncRelayCommand implementations:**
- **`GoToFolderPicker()`** → `GoToFolderPickerCommand`
- **`GoToModelSelection()`** → `GoToModelSelectionCommand`
- **`GoToDownload()`** → `GoToDownloadCommand`
- **`GoToDependencyCheck()`** → `GoToDependencyCheckCommand`
- **`GoToDotNetInstall()`** → `GoToDotNetInstallCommand`
- **`GoToComplete()`** → `GoToCompleteCommand`
- **`ShowError()`** → `ShowErrorCommand`
- **`SelectFolderDialog()`** → `SelectFolderCommand` (invokes Avalonia folder picker)
- **`DownloadAndPatch()`** → `DownloadAndPatchCommand` (orchestrates: fetch release → download patcher → run patcher)
- **`CheckDependencies()`** → `CheckDependenciesCommand`
- **`DownloadModel()`** → `DownloadModelCommand`
- **`Cancel()`** → `CancelCommand`

---

## UI Screens (Avalonia Views)

Each existing SwiftUI view maps to an Avalonia UserControl. The layout and user flow stay identical.

| Swift View | Avalonia View | Key Controls Needed |
|---|---|---|
| [`WelcomeView.swift`](../SetupWizardMacApp/Sources/SetupWizard/Views/WelcomeView.swift) | `SetupWizardWindows/Views/WelcomeView.axaml` | TextBlock (title, description), Button ("Get Started") |
| [`FolderPickerView.swift`](../SetupWizardMacApp/Sources/SetupWizard/Views/FolderPickerView.swift) | `SetupWizardWindows/Views/FolderPickerView.axaml` | Button ("Browse for PAIcom Folder"), TextBlock showing selected path, Button ("Continue") |
| [`ModelSelectionView.swift`](../SetupWizardMacApp/Sources/SetupWizard/Views/ModelSelectionView.swift) | `SetupWizardWindows/Views/ModelSelectionView.axaml` | ListBox or RadioButtons for model selection, Button ("Download & Continue") |
| [`DownloadPatchView.swift`](../SetupWizardMacApp/Sources/SetupWizard/Views/DownloadPatchView.swift) | `SetupWizardWindows/Views/DownloadPatchView.axaml` | ProgressBar, TextBlock (percentage), ScrollViewer with ItemsControl for log output, Button ("Cancel") |
| [`DependencyCheckView.swift`](../SetupWizardMacApp/Sources/SetupWizard/Views/DependencyCheckView.swift) | `SetupWizardWindows/Views/DependencyCheckView.axaml` | Status indicators (checkmarks/crosses), TextBlock descriptions, Button ("Continue") |
| [`DotNetInstallView.swift`](../SetupWizardMacApp/Sources/SetupWizard/Views/DotNetInstallView.swift) | `SetupWizardWindows/Views/DotNetInstallView.axaml` | ProgressBar, ScrollViewer for log output, Button ("Cancel"), status TextBlock |
| [`CompleteView.swift`](../SetupWizardMacApp/Sources/SetupWizard/Views/CompleteView.swift) | `SetupWizardWindows/Views/CompleteView.axaml` | Success icon, TextBlock (summary), Button ("Launch PAIcom"), Button ("Close") |
| [`ErrorView.swift`](../SetupWizardMacApp/Sources/SetupWizard/Views/ErrorView.swift) | `SetupWizardWindows/Views/ErrorView.axaml` | Error icon, TextBlock (error message), Button ("Back to Start") |

**Window configuration** (from [`SetupWizardApp.swift`](../SetupWizardMacApp/Sources/SetupWizard/SetupWizardApp.swift)):
- Minimum window size: 600×500
- Hidden title bar on macOS (Avalonia: `ExtendClientAreaToDecorationsHint = true`, `ExtendClientAreaChromeHints = NoChrome`)
- System background color (`Color(.controlBackgroundColor)` → Avalonia theme resource)

---

## Project Files and Build Configuration

### SetupWizardCore.csproj

Create a new .NET 8.0 class library project. It should **not** reference Avalonia or any UI framework — this is pure business logic.

**NuGet dependencies needed:**
- `Microsoft.Extensions.Logging` (abstractions only)
- `Microsoft.Extensions.Logging.Console` (for console sink)
- `Microsoft.Extensions.Logging.File` (or custom file logger)
- `System.Text.Json` (built-in, no package needed)

**Target framework:** `net8.0` (matches [`CrossPlatformPatcher.csproj`](../CrossPlatformPatcher.csproj:5))

### SetupWizardWindows.csproj

Create a new .NET 8.0 Avalonia Application project.

**NuGet dependencies needed:**
- `Avalonia` (11.0+)
- `Avalonia.Desktop`
- `Avalonia.Themes.Fluent`
- `CommunityToolkit.Mvvm` (for `[ObservableProperty]`, `[RelayCommand]` source generators)
- `Microsoft.Extensions.DependencyInjection`
- Project reference to `SetupWizardCore`

**Build targets:**
```xml
<TargetFrameworks>net8.0</TargetFrameworks>
<RuntimeIdentifiers>win-x64;linux-x64;osx-x64;osx-arm64</RuntimeIdentifiers>
```

### Solution File Update

Add the two new projects to [`CrossPlatformPatcher.sln`](../CrossPlatformPatcher.sln).

---

## AppImage Packaging for Linux

After building `SetupWizardWindows` for `linux-x64`, package it as an AppImage:

**Required tool:** [`appimagetool`](https://github.com/AppImage/AppImageKit/releases) (download the `appimagetool-x86_64.AppImage`).

**AppDir structure to create:**
```
SetupWizard.AppDir/
├── AppRun                          # Entry script
├── SetupWizard.desktop             # FreeDesktop entry
├── SetupWizard.png                 # Application icon (256x256)
└── usr/
    └── bin/
        ├── SetupWizard             # Published .NET binary
        └── lib*                    # Any native .so dependencies
```

**AppRun script contents:**
```sh
#!/bin/bash
exec "$APPDIR/usr/bin/SetupWizard" "$@"
```

**SetupWizard.desktop contents:**
```ini
[Desktop Entry]
Name=CrossPlatformPatcher SetupWizard
Comment=Install and configure PAIcom for cross-platform play
Exec=SetupWizard
Icon=SetupWizard
Terminal=false
Type=Application
Categories=Game;Utility;
```

**Build command:**
```sh
ARCH=x86_64 appimagetool SetupWizard.AppDir SetupWizard-x86_64.AppImage
```

**Include the AppImage packaging in the build script** at [`scripts/publish-all.sh`](../scripts/publish-all.sh) as an optional step after the `linux-x64` patcher publish.

---

## Cross-Platform Feature Matrix

This table shows which Swift features have platform-specific implementations and how the C# version handles each:

| Feature | macOS (Swift - unchanged) | Windows (C# new) | Linux / AppImage (C# new) |
|---|---|---|---|
| **Folder selection** | `NSOpenPanel` | Avalonia `FolderPicker` | Avalonia `FolderPicker` |
| **GitHub release fetch** | `URLSession` + `JSONDecoder` | `HttpClient` + `System.Text.Json` | Same as Windows |
| **Patcher download** | `URLSession` + progress | `HttpClient` + `IProgress<double>` | Same as Windows |
| **Patcher execution** | Foundation `Process` | `System.Diagnostics.Process` | Same as Windows |
| **Model download** | `URLSession` | `HttpClient` | Same as Windows |
| **Model extraction** | `/usr/bin/unzip` shell call | `ZipFile.ExtractToDirectory()` | Same as Windows |
| **Wine detection** | Whisky + Homebrew paths | N/A (native execution) | System `wine`/`wine64` in PATH |
| **.NET installation** | winetricks via Whisky bottle | Native .NET check + download | winetricks via system wine |
| **State persistence** | `~/.wine/setup-state.json` | `%APPDATA%/CrossPlatformPatcher/` | `~/.config/CrossPlatformPatcher/` |
| **Logging** | `~/Library/Application Support/` | `%APPDATA%/CrossPlatformPatcher/` | `~/.config/CrossPlatformPatcher/` |
| **Process cancellation** | SIGTERM → SIGKILL | `CloseMainWindow()` → `Kill()` | `Kill(entireProcessTree: true)` |
| **Arch detection** | `uname()` | `RuntimeInformation.OSArchitecture` | Same as Windows |

---

## Implementation Order

### Phase 1: Shared Library Foundation

1. Create [`SetupWizardCore.csproj`](../SetupWizardCore/) project file with `net8.0` target.
2. **Create `Models/SetupState.cs`** — Port the `SetupStateFile` struct. Fields: CurrentStep, SelectedFolder, PatchedExePath, SelectedWineType, DotNetStatus, WizardStatus, etc.
3. **Create `Services/SetupStateManager.cs`** — Load/save state from `ApplicationData` directory. Individual setter methods. PID cleanup for stale processes.
4. **Create `Services/SetupWizardLogger.cs`** — Write log entries to a file in the same state directory. Console output. Timestamped, level-prefixed entries.
5. **Create `Services/GitHubClient.cs`** — Fetch latest release from GitHub API. Select asset by OS + architecture. Download with progress. Deserialize JSON responses.

### Phase 2: Model & Process Services

6. **Create `Services/ProcessRunner.cs`** — Execute processes with output streaming and cancellation. Support environment variables, working directory.
7. **Create `Services/ModelDownloader.cs`** — Download Vosk models from Alphacephei CDN. Extract zip archives. Support URL and local file inputs. Define the VoskModel enum/class.

### Phase 3: Platform-Specific Services

8. **Create `Services/DependencyChecker.cs`** — Detect Wine/Whisky/native runtime availability per platform.
9. **Create `Services/DotNetInstaller.cs`** — Implement 3 installation strategies (bundled winetricks, system winetricks, native/registry). Platform branching for Windows vs Linux vs macOS.
10. **Create `Services/PatcherOrchestrator.cs`** — High-level orchestration: fetch release → download → run patcher on selected PAIcom.exe with `--migration-mode full`.

### Phase 4: Avalonia GUI

11. **Create `SetupWizardWindows.csproj`** — Avalonia project with necessary dependencies and project reference to `SetupWizardCore`.
12. **Create `App.axaml` / `App.axaml.cs`** — Application entry point. Configure DI container registering all services from `SetupWizardCore`. Set theme and window defaults.
13. **Create `ViewModels/MainViewModel.cs`** — Observable properties for CurrentScreen, SelectedFolder, LogOutput, IsProcessing, Progress. IAsyncRelayCommand implementations for each navigation and action method. Inject services via constructor DI.
14. **Create `Views/MainWindow.axaml`** — Root window with content area that switches views based on CurrentScreen.
15. **Create all 8 view files** (`WelcomeView`, `FolderPickerView`, `ModelSelectionView`, `DownloadPatchView`, `DependencyCheckView`, `DotNetInstallView`, `CompleteView`, `ErrorView`) following the layout of their SwiftUI counterparts.

### Phase 5: Build & Packaging

16. **Update solution file** — Add both new projects.
17. **Create `scripts/build-setupwizard-all.sh`** — Builds `SetupWizardWindows` for all 3 platforms:
    ```sh
    dotnet publish SetupWizardWindows -c Release -r win-x64 --self-contained true
    dotnet publish SetupWizardWindows -c Release -r linux-x64 --self-contained true
    dotnet publish SetupWizardWindows -c Release -r osx-x64 --self-contained true
    dotnet publish SetupWizardWindows -c Release -r osx-arm64 --self-contained true
    ```
18. **Create `scripts/package-appimage.sh`** — AppDir assembly + `appimagetool` invocation.
19. **Create `scripts/package-windows-installer.ps1`** — Optional: Inno Setup or MSIX packaging for Windows.

### Phase 6: Integration & Documentation

20. **Update [`Core/LauncherGenerator.cs`](../Core/LauncherGenerator.cs)** — Add a `SetupWizard.desktop` alongside the existing `setup.command` and `launch.command`. Add Linux desktop entry generation in `WriteSetupLinux()` or a new `WriteSetupDesktop()` method.
21. **Update [`docs/INSTALLER_GUIDE.md`](../docs/INSTALLER_GUIDE.md)** — Add sections for Windows and Linux/AppImage installer build instructions alongside the existing macOS guide.
22. **Update [`docs/BUILD_SYSTEM.md`](../docs/BUILD_SYSTEM.md)** — Document the new project structure and build commands.
23. **Update [`README.md`](../README.md)** — Mention the cross-platform SetupWizard as the non-macOS alternative.

---

## What NOT to Change (Preserve Invariants)

These files must remain **completely unchanged** to avoid breaking the existing macOS flow:

| File | Reason |
|---|---|
| [`SetupWizardMacApp/`](../SetupWizardMacApp/) (entire directory) | The native SwiftUI macOS app must keep working as the macOS distribution path. |
| [`SetupWizardMacApp/build.sh`](../SetupWizardMacApp/build.sh) | macOS .app bundle build process. |
| [`SetupWizardMacApp/Package.swift`](../SetupWizardMacApp/Package.swift) | Swift package definition. |
| [`Core/`](../Core/) (all .cs files) | The patcher IL rewriters, launcher generators, and resource embedders are cross-platform and should not be modified except for adding `launch.desktop` generation in [`LauncherGenerator.cs`](../Core/LauncherGenerator.cs). |
| [`CrossPlatformPatcher.csproj`](../CrossPlatformPatcher.csproj) | The patcher build definition should stay focused on the patcher tool itself, not the installer. |

---

## Validation Checkpoints

After each phase, verify:

1. **Phase 1:** Unit tests can create/save/load a `SetupState` object to `ApplicationData`. GitHubClient can deserialize a GitHub release payload.
2. **Phase 2:** ProcessRunner can execute a simple command (`echo "hello"`) and return output. ModelDownloader can extract a known zip file.
3. **Phase 3:** DependencyChecker correctly identifies the current OS. DotNetInstaller can detect .NET on Windows / Wine prefix on Linux/macOS.
4. **Phase 4:** All 8 views render correctly. Navigation flows work. Folder picker opens. Progress bar updates. Log output streams in real-time.
5. **Phase 5:** `dotnet publish` produces runnable binaries for all 3 platforms. `appimagetool` produces a runnable AppImage.
6. **Phase 6:** Generated `setup.desktop` / `launch.desktop` files open correctly on Linux. `launch.command` still works on macOS (regression check).
