# Cross-Platform Installer Plan

## Purpose

Create a Windows installer build that uses the same installer workflow as the current macOS app, while keeping the existing macOS experience intact. The goal is to separate shared installer behavior from platform-specific UI and packaging so the repo can produce a Windows installer when targeting Windows and a macOS app when targeting macOS.

## Current State

- `SetupWizardMacApp/` is a native SwiftUI macOS installer.
- `Core/` contains the patcher and launcher generation logic.
- `Core/LauncherGenerator.cs` already emits `run.bat` and includes a Windows `SetupWizard.exe` launch hook.
- The macOS installer currently owns GitHub release lookup, Vosk model download, file extraction, patcher execution, and dependency guidance.
- The macOS installer currently assumes macOS-only UI and platform APIs in several places.

## Target Shape

The repository should end up with three layers:

1. A shared installer core that contains the non-UI workflow.
2. A Windows GUI installer that calls into the shared core.
3. The existing macOS GUI installer, kept working until the shared core is stable enough to unify or partially reuse it.

This keeps the current macOS flow safe while making Windows a first-class installer target.

## Platform Choice For Windows GUI

Use Avalonia UI for the Windows installer front end.

- It supports a SwiftUI-like level of flexibility for desktop workflows.
- It can handle folder selection, async downloads, progress reporting, logs, and modal guidance screens.
- It keeps the option open to reuse the same UI stack later if the macOS installer is ever unified.

WPF is acceptable only if the project wants a Windows-only GUI with less cross-platform reuse. Avalonia is the preferred option for this repo because the installer flow is meant to branch by OS while sharing behavior.

## File-Level Change Plan

### Shared core files to add

- Add a new shared installer project under a name such as `SetupWizardCore/`.
- Move the non-UI setup workflow into that project.
- The shared project should own:
  - GitHub release lookup and asset selection.
  - Model download and extraction.
  - Patcher download and execution orchestration.
  - Dependency checks that are not UI-specific.
  - Progress and log events that the UI can subscribe to.
  - A target-platform selector so builds can choose Windows or macOS behavior explicitly.

### macOS installer files to keep and adjust

- `SetupWizardMacApp/Sources/SetupWizard/Services/APIClient.swift`
  - Remove the hardcoded assumption that the patcher asset is selected only by CPU architecture.
  - Make patcher asset resolution aware of the selected target platform.
  - Keep macOS-specific release selection behavior isolated from the shared installer core.

- `SetupWizardMacApp/Sources/SetupWizard/Services/VoskModelDownloader.swift`
  - Replace macOS-only extraction assumptions with cross-platform extraction behavior.
  - Avoid direct dependence on `/usr/bin/unzip` as the only extraction path.
  - Keep the model naming and destination layout compatible with the Windows installer.

- `SetupWizardMacApp/Sources/SetupWizard/Models/SetupWizardViewModel.swift`
  - Treat this file as the macOS UI coordinator only.
  - Reduce direct workflow ownership by moving business logic into the shared core.
  - Keep progress state, screen changes, and logging in the view model.

- `SetupWizardMacApp/Sources/SetupWizard/Views/*.swift`
  - Preserve the current screens and user flow.
  - Update only where the UI needs to reflect shared-core behavior or platform naming.

- `SetupWizardMacApp/Sources/SetupWizard/SetupWizardApp.swift`
  - Keep as the macOS app entry point.
  - No Windows-specific logic should be introduced here.

- `SetupWizardMacApp/Package.swift`
  - Keep the macOS package definition intact for now.
  - Add shared-core dependencies only if the macOS app is refactored to call into the shared library directly.

- `SetupWizardMacApp/build.sh`
  - Keep packaging the `.app` bundle.
  - Make sure the build continues to bundle any resources that the macOS installer still needs.

### Windows installer files to add

- Add a new Windows installer project, such as `SetupWizardWindows/`.
  - This project should host the Avalonia GUI.
  - It should depend on the shared installer core.
  - It should expose the same setup flow as the macOS app: folder selection, model selection, download, patch, dependency checks, and finish state.

- Add a Windows-specific entry point for packaging and launch.
  - The repo already expects `SetupWizard.exe` in generated launcher output.
  - The Windows installer project should build to that name or a compatible launcher name.

- Add a Windows installer packaging definition.
  - Use a Windows-native distribution format such as a zip bundle, MSIX, or an Inno Setup installer.
  - Keep the packaging step separate from the patcher build so the installer can be rebuilt independently.

### Patcher and launcher files to update

- `Core/LauncherGenerator.cs`
  - Keep `run.bat` support for Windows.
  - Keep the `SetupWizard.exe` launch hook in the batch launcher.
  - Make the generated setup entry points clearly platform-aware.
  - Preserve the macOS shell launchers unchanged except where shared installer naming needs alignment.

- `Core/VoskSpeechRecognizer.cs`
  - Review model search expectations if the shared installer changes the model directory layout.
  - Keep existing runtime model discovery compatible with both installer outputs.

- `CrossPlatformPatcher.csproj`
  - Keep the patcher build separate from the installer GUI builds.
  - Add only the dependencies needed for the shared behavior if any installer logic is moved into the main repo project.

- `scripts/publish-all.bat` and `scripts/publish-all.sh`
  - Keep these focused on patcher publish artifacts.
  - Add installer build steps only if the repo decides to ship installer binaries alongside the patcher artifacts.

### Documentation files to update

- `README.md`
  - Update the installer section so it explains that macOS and Windows use different GUI front ends but share the same installer core.
  - Add a Windows installer build path.

- `docs/INSTALLER_GUIDE.md`
  - Expand it beyond macOS-only guidance.
  - Add a Windows installer section with build, packaging, and launch instructions.

- `docs/BUILD_SYSTEM.md`
  - Add installer build steps and output locations.
  - Document how to select a Windows versus macOS installer build.

- `docs/QUICK_START.md`
  - Add a short Windows installer quick start once the Windows GUI exists.

- `docs/PROJECT_STRUCTURE.md`
  - Document the new installer project layout and the shared-core relationship.

## Build Strategy

### Patcher build

Keep the patcher build as the baseline validation step:

- `dotnet build CrossPlatformPatcher.csproj -c Release`
- `dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release`

### macOS installer build

Keep the existing macOS app build path:

- Build the macOS installer from `SetupWizardMacApp/`.
- Package the `.app` bundle as the existing scripts already do.
- Continue to treat macOS signing and notarization as a separate release step.

### Windows installer build

Add a new Windows installer build path:

- Restore and build the shared installer core.
- Build the Windows GUI project for the Windows runtime identifier.
- Publish the Windows installer as a standalone executable or installer package.
- Verify the resulting output can be launched from the patcher-generated `run.bat` flow.

### Release selection

Build selection should be explicit:

- Windows build produces the Windows installer.
- macOS build produces the macOS app.
- Shared code should not infer the installer target from CPU architecture alone.
- Release asset selection should choose by OS target first, then by architecture if needed.

## Validation Plan

1. Build the patcher in Release mode.
2. Run the test suite.
3. Build the macOS installer and confirm the current `.app` output still works.
4. Build the Windows installer and confirm it produces a launchable `SetupWizard.exe` or package-equivalent output.
5. Generate patcher artifacts and verify `run.bat` still launches the Windows installer hook when present.
6. Confirm the macOS launcher flow still points to `setup.command` and `launch.command` without regressions.

## Implementation Order

1. Extract the shared installer logic from the macOS app.
2. Add platform-aware asset selection and model handling to the shared layer.
3. Build the Windows Avalonia GUI around that shared layer.
4. Wire the Windows installer into the existing launcher hook.
5. Update docs and release instructions.
6. Validate patcher, macOS installer, and Windows installer builds separately.

## Acceptance Criteria

- The patcher still builds and tests cleanly.
- The macOS installer still ships as a native `.app`.
- A Windows installer build exists and is selected explicitly for Windows.
- Shared installer behavior is not duplicated in both GUIs.
- No macOS-specific APIs are required for the Windows installer path.
- No Windows-specific assumptions break the existing macOS flow.
