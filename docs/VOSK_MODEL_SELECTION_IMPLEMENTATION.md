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

### 2. macOS SetupWizard.app

**Integration:**
- Vosk model selection is fully integrated into the native SwiftUI `SetupWizard.app`
- Users can select from pre-built models or provide custom models
- Models are downloaded and extracted during setup workflow

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

## For SetupWizard.app Users (macOS)

When users launch the native `SetupWizard.app`:
1. They're presented with an interactive setup wizard
2. After selecting PAIcom folder, they reach the Vosk model selection step
3. They can choose from pre-built models (downloaded on demand) or provide a custom model
4. Selected model is extracted to `PAIcom_Player_Folder/models/<modelname>/`
5. Model persists for all subsequent game runs

## Download Sources

Models are downloaded from: `https://alphacephei.com/vosk/<modelname>.zip`

These are official Vosk project CDN URLs with reliable hosting.

## Testing

The implementation has been:
- ✅ Compiled successfully (Swift 5 with SwiftUI)
- ✅ Integrated into SetupWizard.app
- ✅ Model ZIP downloads verified

Next steps for user:
1. Build and run SetupWizard.app: `cd SetupWizardMacApp && ./build.sh`
2. Launch the app
3. Select PAIcom folder
4. Choose a model from the list or provide your own
5. Wizard handles download and setup automatically
