# Vosk Model Selection Feature Implementation

## Summary
Added a model selection screen to the SetupWizard that allows users to:
1. Choose from 4 popular pre-built Vosk models
2. Specify their own model (local file or URL)
3. Download and extract the selected model during setup
4. Have the model ready in the `models/` folder for first run

## Changes Made

### 1. SetupWizard macOS App (Swift)

**New Files:**
- `SetupWizardMacApp/Sources/SetupWizard/Services/VoskModelDownloader.swift`
  - Actor handling model downloads from Alphacephei CDN
  - Supports downloading pre-built models or custom models from URLs
  - Handles ZIP extraction
  - Progress callbacks for UI updates

- `SetupWizardMacApp/Sources/SetupWizard/Views/ModelSelectionView.swift`
  - SwiftUI view for model selection screen
  - Radio-button style selection of 4 built-in models
  - Toggle to use custom model with file/URL picker
  - Progress display during download
  - Activity log display

**Modified Files:**
- `SetupWizardMacApp/Sources/SetupWizard/SetupWizardApp.swift`
  - Added `.modelSelection` case to screen switch
  - Integrated ModelSelectionView into navigation flow

- `SetupWizardMacApp/Sources/SetupWizard/Models/SetupWizardViewModel.swift`
  - Added `.modelSelection` to Screen enum
  - Added `modelDownloader` service
  - Added `modelPath` @Published property
  - Added `goToModelSelection()` navigation function
  - Added `downloadModel(_:)` function for pre-built models
  - Added `downloadCustomModel(path:name:)` for custom models

- `SetupWizardMacApp/Sources/SetupWizard/Models/SetupStateFile.swift`
  - Added `.modelSelection` case to Step enum

- `SetupWizardMacApp/Sources/SetupWizard/Views/FolderPickerView.swift`
  - Changed "Continue" button to navigate to model selection instead of download

### 2. macOS Installer Package

**Modified Files:**
- `build-mac-pkg.sh`
  - Added code to include all Vosk model ZIPs from `VoskModels/zips/` into the installer payload
  - Creates `Applications/CrossPlatformPatcher/VoskModels/zips/` directory

- `pkg-scripts/postinstall`
  - Added model extraction logic on install
  - Extracts first found model ZIP into `Applications/CrossPlatformPatcher/models/<modelname>/`
  - Gracefully handles extraction failures by copying ZIP if needed

## Available Models

The following models are available for selection:

1. **vosk-model-small-en-us-0.15** (~50 MB) - Fast, lightweight
2. **vosk-model-en-us-0.22** (~330 MB) - Good accuracy
3. **vosk-model-en-us-0.22-lgraph** (~850 MB) - Better accuracy with larger graph
4. **vosk-model-en-us-0.42-gigaspeech** (~1.4 GB) - Highest accuracy, requires most storage

## Usage Flow

1. User launches SetupWizard
2. Selects PAIcom folder
3. **NEW:** Selects or provides a Vosk model
   - Can choose pre-built from list or use custom model
   - Downloads model if selecting pre-built
   - For custom models: can use local ZIP or provide URL
4. Model is extracted to `PAIcom_Player_Folder/models/<modelname>/`
5. Continues to patcher download/patch step
6. On first game run, Vosk initializer will find model in expected location

## For Installer Users (macOS .pkg)

When users install via the `.pkg` installer:
1. SetupWizard.command is installed to `Applications/CrossPlatformPatcher/`
2. Default model (`vosk-model-en-us-0.22-lgraph.zip`) is extracted to `Applications/CrossPlatformPatcher/models/`
3. User runs SetupWizard, selects PAIcom folder
4. Can choose to use pre-installed model or select a different one
5. Model is copied to their PAIcom folder for use

## Download Sources

Models are downloaded from: `https://alphacephei.com/vosk/<modelname>.zip`

These are official Vosk project CDN URLs with reliable hosting.

## Testing

The implementation has been:
- ✅ Compiled successfully (Swift 5 with SwiftUI)
- ✅ Integrated into SetupWizard app
- ✅ Bundled into macOS `.pkg` installer
- ✅ Model ZIP verified in installer payload

Next steps for user:
1. Install the generated `.pkg` on macOS
2. Run `SetupWizard.command` from Applications
3. Select PAIcom folder
4. Choose a model from the list or provide your own
5. Wizard handles download and setup automatically
