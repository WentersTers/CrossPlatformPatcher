# SetupWizard macOS App

A native SwiftUI application that guides users through setting up PAIcom on macOS with Wine/Whisky.

## Building

### Prerequisites
- macOS 12+
- Swift 5.9+
- Xcode command line tools

### Quick Build

```bash
cd SetupWizardMacApp
chmod +x build.sh
./build.sh
```

The built app will be at: `SetupWizardMacApp/build/Release/SetupWizard.app`

### Manual Build (SPM)

```bash
cd SetupWizardMacApp
swift build -c release --static-swift-stdlib
```

## Architecture

### Services Layer
- **ProcessRunner**: Manages shell command execution with:
  - Real-time output streaming via Pipes
  - Graceful cancellation (SIGTERM → SIGKILL)
  - Process state tracking for recovery
  
- **SetupStateManager**: Persistent state across sessions
  - Tracks step-by-step progress
  - Stores selected folder, Wine type, .NET status
  - Enables resume after cancellation
  - Stored in `~/.wine/setup-state.json`

- **APIClient**: GitHub API integration
  - Fetches latest patcher release
  - Auto-detects architecture (arm64 vs x86_64)
  - Downloads with progress tracking

- **DependencyChecker**: System dependency detection
  - Whisky installation check
  - System Wine availability
  - Homebrew presence detection

- **DotNetInstaller**: .NET installation with fallbacks
  - Strategy 1: Bundled winetricks (no Homebrew needed)
  - Strategy 2: System winetricks
  - Strategy 3: Native wine regedit approach

### UI Views
- **WelcomeView**: Introduction and flow overview
- **FolderPickerView**: NSOpenPanel integration for folder selection
- **DownloadPatchView**: GitHub download + patcher execution
- **DependencyCheckView**: Wine/Whisky detection and guidance
- **DotNetInstallView**: Real-time .NET installation with cancellation
- **CompleteView**: Success confirmation and next steps
- **ErrorView**: Error handling and troubleshooting
- **LogViewComponent**: Reusable real-time log display

## Key Features

### Real-Time Streaming
All long-running operations stream output to the UI in real-time for visibility:
- GitHub downloads
- Patcher execution
- .NET installation via winetricks

### Graceful Cancellation
Users can cancel long operations:
1. Cancel button sends SIGTERM (graceful, 5-10s)
2. If still running, sends SIGKILL (forceful)
3. Wine prefix preserved (not deleted)
4. State recorded for resume on next launch

### Fallback Strategies
For .NET installation:
1. **Bundled winetricks**: App includes standalone script (no Homebrew dependency)
2. **System winetricks**: Falls back to system installation if available
3. **Native regedit**: Uses wine regedit directly for .NET registry keys
4. **User skip**: If all fail, user can skip and troubleshoot manually

### State Recovery
All progress saved to `~/.wine/setup-state.json`:
- Current step in wizard
- Selected folder path
- Wine type chosen (Whisky/system)
- .NET installation status
- Last error or cancellation time

On app restart, users can resume from where they left off.

## Logging

All operations logged to: `~/Library/Application Support/CrossPlatformPatcher/setup-wizard.log`

Includes:
- Timestamps for each operation
- Debug information from all services
- Full error traces
- User can view logs from "Show Log File" button in UI

## Testing

### Build Test
```bash
./build.sh
# Verify build/Release/SetupWizard.app exists
```

### Manual Testing
```bash
open build/Release/SetupWizard.app
```

Test workflow:
1. Welcome → Start
2. Folder Picker → Select PAIcom folder → Continue
3. Download & Patch → Monitor progress → (should succeed)
4. Dependency Check → Verify Wine detection → Continue
5. .NET Install → Monitor winetricks output → Complete
6. Done → View log files

### Test Scenarios
- [ ] No PAIcom.exe: should show error
- [ ] Network failure during download: should show retry option
- [ ] Cancel during .NET install: should preserve prefix and allow retry
- [ ] Missing Wine: should offer installation guidance
- [ ] On arm64 Mac: correct asset downloaded
- [ ] On x86_64 Mac: correct asset downloaded

## Integration with build-mac-pkg.sh

The main patcher's `build-mac-pkg.sh` will:
1. Run `./SetupWizardMacApp/build.sh`
2. Copy `SetupWizardMacApp/build/Release/SetupWizard.app` into the .pkg payload
3. Create launcher wrapper for entry point

See [CrossPlatformPatcher/build-mac-pkg.sh](../build-mac-pkg.sh) for integration.

## Future Enhancements

- [ ] Localization (i18n) for non-English locales
- [ ] Automatic Homebrew installation if missing
- [ ] Whisky bottle creation and shortcut generation
- [ ] Progress percentage estimation for long operations
- [ ] Crash recovery and automatic log upload for debugging
- [ ] Unit tests for services layer
