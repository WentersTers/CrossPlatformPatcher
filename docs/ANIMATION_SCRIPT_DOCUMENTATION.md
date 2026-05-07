# Animation Script Execution Documentation

## Overview

The animation script system executes frame-by-frame animations and audio playback triggered by voice commands. Animation scripts are text files located in the `PAIcom_Player_Folder/animations/` directory.

When a voice command is recognized (e.g., "open the browser"), the system loads and executes the corresponding animation script (e.g., `internet.txt`) using reflection-based method invocation for cross-platform compatibility.

---

## Command Format

Animation scripts contain simple text-based commands, one per line:

```
HIDE_ALL
SHOW 12
OPEN_URL https://www.google.com/
PLAY_AUDIO audio/internet.wav
WAIT 1000
HIDE 12
SHOW 20
WAIT 800
HIDE 20
SHOW 4
```

---

## Command Specifications

### 1. **HIDE_ALL** - Hide all animation frames

**Purpose:** Clears all visible animation frames from the display.

**Execution Steps:**
1. Gets the main application form via reflection
   - Method: `Application.OpenForms[0]` (first open form)
2. Retrieves the `Controls` collection
3. Iterates through all controls looking for `PictureBox` elements
4. For each PictureBox found:
   - Sets `Visible = false`
   - Disposes the current `Image` object
   - Sets `Image = null`

**Methods Attempted:**
- `System.Windows.Forms.Application.OpenForms` (property)
- `System.Windows.Forms.Application.ActiveForm` (fallback property)
- `System.Windows.Forms.Form.Controls` (property)
- Recursive control traversal through nested containers
- `System.Windows.Forms.PictureBox.Visible` (property setter)
- `System.Windows.Forms.PictureBox.Image` (property)
- `System.Drawing.Image.Dispose()` (method)
- `System.Windows.Forms.Control.InvokeRequired` (property)
- `System.Windows.Forms.Control.BeginInvoke(Delegate)` (method)

**Example Log Output:**
```
[animation-script-action] HIDE_ALL
```

---

### 2. **SHOW {frameNum}** - Display an animation frame

**Purpose:** Loads a numbered PNG image (e.g., `12.png`) and displays it in the best available PictureBox control.

**Execution Steps:**
1. Constructs path: `{animationDir}/{frameNum}.png`
2. Checks if file exists
3. Loads image via `System.Drawing.Image.FromFile()` (reflection)
4. Gets main application form
5. Iterates through form controls to find candidate `PictureBox` controls
6. Scores candidates using visibility, bounds, and parent visibility
7. Sets the selected PictureBox's `Image` property to the loaded image
8. Sets `Visible = true`

**Methods Attempted:**
- `System.Drawing.Image.FromFile(string)` (static method)
- `System.Drawing.Image.FromFile(string, bool)` (fallback overload)
- `System.Drawing.Image.FromStream(Stream)` (final fallback when file-based loading fails)
- `System.Windows.Forms.Application.OpenForms` (property)
- `System.Windows.Forms.Application.ActiveForm` (fallback property)
- `System.Windows.Forms.Form.Controls` (property)
- `System.Windows.Forms.PictureBox.Image` (property setter)
- `System.Windows.Forms.PictureBox.ImageLocation` (property setter)
- `System.Windows.Forms.PictureBox.BackgroundImage` (property setter)
- `System.Windows.Forms.PictureBox.BringToFront()` (method)
- `System.Windows.Forms.PictureBox.Refresh()` (method)
- `System.Windows.Forms.PictureBox.Invalidate()` (method)
- `System.Windows.Forms.PictureBox.Update()` (method)
- `System.Windows.Forms.PictureBox.Load(string)` (alternate load path)
- `System.Windows.Forms.Control.Parent` (property)
- `System.Windows.Forms.Control.Controls.SetChildIndex(Control, int)` (method)
- `System.Windows.Forms.Control.PerformLayout()` (method)
- `System.Windows.Forms.Control.SuspendLayout()` / `ResumeLayout(bool)` (methods)
- `System.Windows.Forms.Control.Refresh()` (method)
- `System.Windows.Forms.Control.Invalidate(bool)` (method)
- `System.Windows.Forms.Control.Update()` (method)
- `System.Windows.Forms.Control.Bounds` (property, diagnostics)
- `System.Windows.Forms.Control.Location` (property, diagnostics)
- `System.Windows.Forms.Control.Size` (property, diagnostics)
- `System.Windows.Forms.Application.DoEvents()` (static method)
- `System.Windows.Forms.PictureBox.Visible` (property setter)
- `System.Windows.Forms.Control.InvokeRequired` (property)
- `System.Windows.Forms.Control.BeginInvoke(Delegate)` (method)

**Parameter:**
- `frameNum`: Integer frame number (e.g., 12 → loads `12.png`)

**Example Log Output:**
```
[animation-script-action] SHOW 12
[animation-script-action] Displayed frame 12 in pbxAnimation
[animation-script-diag] Selected PictureBox: PictureBox name='pbxAnimation';visible=True;enabled=True;bounds={X=24,Y=116,Width=640,Height=360}
```

**File Resolution:**
```
PAIcom_Player_Folder/
└── animations/
    ├── 1.png
    ├── 4.png
    ├── 11.png
    ├── 12.png (loaded by SHOW 12)
    └── 20.png
```

---

### 3. **HIDE {frameNum}** - Hide a specific animation frame

**Purpose:** Hides the first visible PictureBox and clears its image.

**Execution Steps:**
1. Gets the main application form
2. Iterates through form controls looking for `PictureBox`
3. Checks if the PictureBox is currently `Visible = true`
4. If found:
   - Sets `Visible = false`
   - Disposes the image
   - Sets `Image = null`
   - Returns (stops after first match)

**Methods Attempted:**
- `System.Windows.Forms.Application.OpenForms` (property)
- `System.Windows.Forms.Application.ActiveForm` (fallback property)
- `System.Windows.Forms.Form.Controls` (property)
- `System.Windows.Forms.PictureBox.Visible` (property getter/setter)
- `System.Windows.Forms.PictureBox.Image` (property)
- `System.Windows.Forms.PictureBox.ImageLocation` (property)
- `System.Windows.Forms.PictureBox.BackgroundImage` (property)
- `System.Windows.Forms.PictureBox.Refresh()` (method)
- `System.Drawing.Image.Dispose()` (method)
- `System.Windows.Forms.Control.InvokeRequired` (property)
- `System.Windows.Forms.Control.BeginInvoke(Delegate)` (method)

**Parameter:**
- `frameNum`: Integer (currently unused; always hides first visible frame)

**Example Log Output:**
```
[animation-script-action] HIDE 12
```

**Note:** The `frameNum` parameter is parsed but not currently used for targeted frame hiding. The implementation hides the first visible PictureBox regardless of the parameter value.

---

### 4. **WAIT {milliseconds}** - Pause for specified duration

**Purpose:** Blocks script execution for a specified number of milliseconds to create animation timing.

**Execution Steps:**
1. Parses milliseconds parameter
2. Calls `Thread.Sleep(milliseconds)`
3. Resumes script execution after delay

**Methods Attempted:**
- `System.Threading.Thread.Sleep(int)` (static method)

**Parameter:**
- `milliseconds`: Integer delay in milliseconds (e.g., 1000 = 1 second)

**Example Log Output:**
```
[animation-script-action] WAIT 1000ms
```

**Timing Note:** The WAIT command uses synchronous blocking. This is intentional to ensure sequential frame timing. Animations will pause on the calling thread while the delay completes.

---

### 5. **PLAY_AUDIO {audioFile}** - Play audio file

**Purpose:** Loads and plays an audio file using `System.Media.SoundPlayer`.

**Execution Steps:**
1. Constructs path: `{animationDir}/{audioFile}`
2. If file not found, tries the parent folder and `audio/` folder fallbacks
3. If still not found, tries with `.wav` extension
4. If still not found, tries with `.mp3` extension
5. Gets `System.Media.SoundPlayer` type via reflection and loaded-assembly lookup
6. Creates SoundPlayer instance with audio path
7. Invokes `Play()` method via reflection

**Methods Attempted:**
- `System.Media.SoundPlayer(string)` (constructor via Activator.CreateInstance)
- `System.Media.SoundPlayer.Play()` (method)
- `System.Type.GetType(...)` with assembly-qualified fallbacks

**Parameter:**
- `audioFile`: Relative path to audio file (e.g., `audio/internet.wav`)

**File Resolution Fallback:**
1. Try exact path: `{animationDir}/audio/internet.wav`
2. Try parent root: `{root}/audio/internet.wav`
3. Try with .wav: `{animationDir}/audio/internet.wav`
4. Try with .mp3: `{animationDir}/audio/internet.mp3`
5. Try `animations/audio/` and parent `audio/` variants

**Example Log Output:**
```
[animation-script-action] PLAY_AUDIO audio/internet.wav
[animation-script-action] Audio playing: audio/internet.wav
```

**Supported Formats:**
- `.wav` (Wave Audio File)
- `.mp3` (MPEG Audio Layer III)
- Any format supported by `System.Media.SoundPlayer`

---

### 6. **OPEN_URL {url}** - Open URL in default browser

**Purpose:** Launches the default web browser and navigates to the specified URL.

**Execution Steps:**
1. Parses URL from command
2. Creates `ProcessStartInfo` object
3. Sets `FileName` to the URL
4. Sets `UseShellExecute = true` (allows shell to interpret URL)
5. Calls `Process.Start()` to launch browser

**Methods Attempted:**
- `System.Diagnostics.ProcessStartInfo` (constructor)
- `System.Diagnostics.Process.Start(ProcessStartInfo)` (static method)

**Parameter:**
- `url`: Full URL to open (e.g., `https://www.google.com/`)

**Example Log Output:**
```
[animation-script-action] OPEN_URL https://www.google.com/
```

**Behavior:**
- On Windows/Wine: Opens URL in default browser
- On macOS: Uses standard `open` command via shell
- On Linux: Uses standard `xdg-open` command via shell

---

## Reflection-Based Method Resolution

All method invocations use **reflection** to ensure cross-platform compatibility and to avoid hard dependencies on GUI frameworks in the PAIcom.OWW .NET Standard library.

The current runtime helper also uses two practical fallbacks that were added after the first round of diagnostics:
- assembly-aware type lookup for `System.Drawing.Image`, `System.Media.SoundPlayer`, and `System.Windows.Forms.Application`
- UI-thread marshaling via `InvokeRequired` and `BeginInvoke` when a WinForms dispatcher is available
- PictureBox redraw fallbacks via `ImageLocation`, `BackgroundImage`, `Refresh`, `Invalidate`, `Update`, and `BringToFront`
- Parent layout fallbacks via `SetChildIndex`, `PerformLayout`, `SuspendLayout`, `ResumeLayout`, and `Application.DoEvents`

### Key Reflection Patterns:

**Getting a Type:**
```csharp
var imageType = System.Type.GetType("System.Drawing.Image");
```

**Calling Static Methods:**
```csharp
var fromFileMethod = imageType.GetMethod("FromFile");
var image = fromFileMethod.Invoke(null, new object[] { filePath });
```

**Getting/Setting Properties:**
```csharp
var visibleProperty = pbxType.GetProperty("Visible");
visibleProperty.SetValue(control, true);  // Set
var value = visibleProperty.GetValue(control);  // Get
```

**Creating Instances:**
```csharp
var soundPlayerType = System.Type.GetType("System.Media.SoundPlayer");
var player = System.Activator.CreateInstance(soundPlayerType, audioPath);
```

---

## Error Handling and Logging

All animation commands include comprehensive error handling and diagnostic logging:

### Log Prefixes:
- `[animation-script]` - General script execution messages
- `[animation-script-action]` - Command execution results
- `[animation-script-error]` - Errors and exceptions

### Example Error Logs:
```
[animation-script-error] Frame image not found: Z:\animations\99.png
[animation-script-error] Audio file not found: audio/missing.wav
[animation-script-error] Could not find main game form
[animation-script-error] No PictureBox controls found to display frame
[animation-script-error] Image type not found
[animation-script-error] SoundPlayer type not found
```

---

## Script Execution Flow

```
Voice Command Recognized
        ↓
Script Reference Extracted (e.g., "internet.txt")
        ↓
TryExecuteAnimationScriptSync() Called
        ↓
Script File Loaded from animations/internet.txt
        ↓
For Each Line in Script:
    ├─ HIDE_ALL → Hide all PictureBox controls
    ├─ SHOW 12 → Load 12.png, display in PictureBox
    ├─ PLAY_AUDIO audio/internet.wav → Play sound
    ├─ WAIT 1000 → Sleep 1000ms
    ├─ HIDE 12 → Hide PictureBox
    └─ OPEN_URL https://google.com → Launch browser
        ↓
Script Complete
        ↓
Log: [animation-script] Script execution completed: internet.txt
```

---

## Execution Context

**Threading:** Animation scripts execute on a **background thread** via `ThreadPool.UnsafeQueueUserWorkItem()`. This prevents blocking the main speech recognition loop.

**Execution Timing:** 
- Script execution begins **after** command dispatch succeeds
- WAIT commands block only the animation thread, not the UI

**Concurrency:** Multiple scripts can queue but execute sequentially on the thread pool.

---

## Known Limitations

1. **No Named Frame Targeting:** HIDE command hides the first visible frame, not a specific-numbered frame
2. **Single PictureBox:** Only the first PictureBox in the form is used for display
3. **Blocking WAIT:** WAIT commands block the animation thread (intentional for timing)
4. **Linear Playback:** Scripts execute linearly; no branching or conditionals supported
5. **Platform Differences:** Audio playback and browser opening depend on platform-specific support via reflection

---

## Testing and Diagnostics

### Enable Diagnostic Logging:
Animation script execution is always logged with diagnostic prefixes. Check console output for:
```
[animation-script] TryExecuteAnimationScriptSync called with: internet.txt
[animation-script] Loading script from: Z:\Users\...\animations\internet.txt
[animation-script-action] SHOW 12
[animation-script-action] Displayed frame 12 in pbxAnimation
[animation-script] Script execution completed: internet.txt
```

### Manual Testing:
Run a voice command to trigger animation:
```bash
./build-patch-and-launch.sh --migration-mode full
# Game opens, then say: "Hey PAIcom, open the browser"
```

For focused testing workflows, use:
- `LIVE-TESTING.md` for live runtime method testing
- `TEST_COMMANDS.md` for command injection validation

Expected behavior:
1. Command recognized at ~87-100% confidence
2. UI dispatch succeeds (TextBox + Button simulation)
3. Animation script queues and executes
4. Frames display, audio plays, timings respected
