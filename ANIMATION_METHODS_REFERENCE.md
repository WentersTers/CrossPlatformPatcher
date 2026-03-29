# Animation Command Methods Reference

Quick reference for all methods attempted during animation script execution.

---

## Command: HIDE_ALL

**What it does:** Hides all animation frames

**Methods tried (in order):**

| Method | Type | Purpose | Status |
|--------|------|---------|--------|
| `System.Windows.Forms.Application.OpenForms` | Property | Get open forms list | ✅ Critical |
| `System.Windows.Forms.Application.ActiveForm` | Property | Fallback when OpenForms is empty | ✅ Important |
| `System.Windows.Forms.Form.Controls` | Property | Get form controls collection | ✅ Critical |
| `.GetEnumerator()` on Controls | Method | Iterate through controls | ✅ Critical |
| Recursive control traversal | Pattern | Find nested PictureBox controls | ✅ Critical |
| `PictureBox` (type check) | Type | Check if control is PictureBox | ✅ Critical |
| `PictureBox.Visible` | Property | Set to false | ✅ Critical |
| `PictureBox.Image` | Property | Get current image object | ✅ Important |
| `Image.Dispose()` | Method | Free image resources | ✅ Important |
| `Control.InvokeRequired` | Property | Detect cross-thread UI access | ✅ Critical |
| `Control.BeginInvoke(Delegate)` | Method | Marshal UI updates to UI thread | ✅ Critical |

---

## Command: SHOW {frameNum}

**What it does:** Load and display frame image (e.g., `12.png`)

**Methods tried (in order):**

| Method | Type | Purpose | Status |
|--------|------|---------|--------|
| `System.IO.File.Exists()` | Static | Check if frame file exists | ✅ Critical |
| `System.Drawing.Image` (type) | Type | Get Image type via reflection | ✅ Critical |
| `Image.FromFile(string)` | Static | Load PNG from path | ✅ Critical |
| `System.Windows.Forms.Application.OpenForms` | Property | Get main form | ✅ Critical |
| `System.Windows.Forms.Application.ActiveForm` | Property | Fallback when OpenForms is empty | ✅ Important |
| `Form.Controls` | Property | Get form controls | ✅ Critical |
| `PictureBox` (type check) | Type | Find PictureBox controls | ✅ Critical |
| `PictureBox.Image` | Property | Set image content | ✅ Critical |
| `PictureBox.Visible` | Property | Set to true | ✅ Critical |
| `PictureBox.ImageLocation` | Property | Clear file-backed image path | ✅ Important |
| `PictureBox.BackgroundImage` | Property | Clear stale background rendering | 🟡 Optional |
| `PictureBox.BringToFront()` | Method | Ensure the frame is not obscured | 🟡 Optional |
| `PictureBox.Load(string)` | Method | Alternate frame-loading path | ✅ Important |
| `PictureBox.Refresh()` | Method | Force redraw after frame swap | ✅ Important |
| `PictureBox.Invalidate()` | Method | Mark frame for repaint | ✅ Important |
| `PictureBox.Update()` | Method | Flush pending paint messages | ✅ Important |
| `Control.Parent` | Property | Reach the parent container for z-order fixes | ✅ Important |
| `Control.Controls.SetChildIndex(Control, int)` | Method | Move the frame to the front of the z-order | ✅ Important |
| `Control.PerformLayout()` | Method | Recompute layout after frame updates | ✅ Important |
| `Control.SuspendLayout()` | Method | Avoid partial redraw during updates | 🟡 Optional |
| `Control.ResumeLayout(bool)` | Method | Resume layout after updates | 🟡 Optional |
| `Control.Refresh()` | Method | Force redraw after frame swap | ✅ Important |
| `Control.Invalidate(bool)` | Method | Mark the parent area for repaint | ✅ Important |
| `Control.Update()` | Method | Flush pending paint messages | ✅ Important |
| `Control.Bounds` | Property | Log control position and size for diagnostics | ✅ Important |
| `Control.Location` | Property | Log control position for diagnostics | ✅ Important |
| `Control.Size` | Property | Log control size for diagnostics | ✅ Important |
| `System.Windows.Forms.Application.DoEvents()` | Static | Pump pending UI paint messages | ✅ Important |
| `PictureBox.Name` | Property | Get control name for logging | 🟡 Optional |
| `Control.InvokeRequired` | Property | Detect cross-thread UI access | ✅ Critical |
| `Control.BeginInvoke(Delegate)` | Method | Marshal UI updates to UI thread | ✅ Critical |

---

## Command: HIDE {frameNum}

**What it does:** Hide the first visible animation frame

**Methods tried (in order):**

| Method | Type | Purpose | Status |
|--------|------|---------|--------|
| `System.Windows.Forms.Application.OpenForms` | Property | Get main form | ✅ Critical |
| `System.Windows.Forms.Application.ActiveForm` | Property | Fallback when OpenForms is empty | ✅ Important |
| `Form.Controls` | Property | Get form controls | ✅ Critical |
| `.GetEnumerator()` on Controls | Method | Iterate through controls | ✅ Critical |
| `PictureBox` (type check) | Type | Check if control is PictureBox | ✅ Critical |
| `PictureBox.Visible` | Property | Check if visible (get) | ✅ Critical |
| `PictureBox.Visible` | Property | Set to false | ✅ Critical |
| `PictureBox.Image` | Property | Get image to dispose | ✅ Important |
| `PictureBox.ImageLocation` | Property | Clear file-backed image path | ✅ Important |
| `PictureBox.BackgroundImage` | Property | Clear stale background rendering | 🟡 Optional |
| `PictureBox.Refresh()` | Method | Force redraw after hide | ✅ Important |
| `Image.Dispose()` | Method | Free resources | ✅ Important |
| `Control.InvokeRequired` | Property | Detect cross-thread UI access | ✅ Critical |
| `Control.BeginInvoke(Delegate)` | Method | Marshal UI updates to UI thread | ✅ Critical |

---

## Command: WAIT {milliseconds}

**What it does:** Pause execution for N milliseconds

**Methods tried (in order):**

| Method | Type | Purpose | Status |
|--------|------|---------|--------|
| `System.Threading.Thread.Sleep(int)` | Static | Block current thread | ✅ Critical |

*Note: This is the simplest command - just a direct method call, no reflection needed.*

---

## Command: PLAY_AUDIO {audioFile}

**What it does:** Load and play audio file

**Methods tried (in order):**

| Method | Type | Purpose | Status |
|--------|------|---------|--------|
| `System.IO.File.Exists()` | Static | Check if audio exists | ✅ Critical |
| `System.IO.Path.GetFileNameWithoutExtension()` | Static | Try .wav extension | ✅ Important |
| `System.IO.Path.GetFileNameWithoutExtension()` | Static | Try .mp3 extension | ✅ Important |
| Parent/audio path fallback | Pattern | Search `animations/`, root, and `audio/` folders | ✅ Critical |
| `System.Media.SoundPlayer` (type) | Type | Get SoundPlayer type via reflection | ✅ Critical |
| `SoundPlayer(string)` | Constructor | Create player with path (via Activator) | ✅ Critical |
| `SoundPlayer.Play()` | Method | Start audio playback | ✅ Critical |
| `Type.GetType(...assembly-qualified...)` | Pattern | Resolve `SoundPlayer` and `Image` from loaded assemblies | ✅ Critical |

---

## Command: OPEN_URL {url}

**What it does:** Open URL in default browser

**Methods tried (in order):**

| Method | Type | Purpose | Status |
|--------|------|---------|--------|
| `System.Diagnostics.ProcessStartInfo` | Constructor | Create process info object | ✅ Critical |
| `ProcessStartInfo.FileName` | Property | Set to URL | ✅ Critical |
| `ProcessStartInfo.UseShellExecute` | Property | Set to true (for shell interpretation) | ✅ Critical |
| `System.Diagnostics.Process.Start()` | Static | Launch browser process | ✅ Critical |

---

## Method Resolution Via Reflection

All GUI methods use reflection to be compatible with PAIcom.OWW (.NET Standard library):

### Pattern 1: Get Type and Call Static Method
```csharp
var imageType = TryResolveRuntimeType(
    "System.Drawing.Image",
    "System.Drawing.Image, System.Drawing.Common",
    "System.Drawing.Image, System.Drawing");
var fromFileMethod = imageType.GetMethod("FromFile", new[] { typeof(string) });
var image = fromFileMethod.Invoke(null, new object[] { filePath });
```

### Pattern 1c: Alternate Image Load Paths
```csharp
var fromFileColorMethod = imageType.GetMethod("FromFile", new[] { typeof(string), typeof(bool) });
var fromStreamMethod = imageType.GetMethod("FromStream", new[] { typeof(Stream) });
```

### Pattern 1b: Resolve Types from Loaded Assemblies
```csharp
private static Type? TryResolveRuntimeType(params string[] typeNames)
{
    foreach (var typeName in typeNames)
    {
        var type = System.Type.GetType(typeName, throwOnError: false);
        if (type != null)
            return type;
    }

    return null;
}
```

### Pattern 2: Get and Set Property
```csharp
var visibleProperty = picturBoxType.GetProperty("Visible");
visibleProperty.SetValue(control, true);  // Set
var isVisible = (bool?)visibleProperty.GetValue(control);  // Get
```

### Pattern 2b: Refresh PictureBox Rendering
```csharp
var imageLocationProperty = pictureBoxType.GetProperty("ImageLocation", BindingFlags.Instance | BindingFlags.Public);
imageLocationProperty?.SetValue(control, string.Empty);

var backgroundImageProperty = pictureBoxType.GetProperty("BackgroundImage", BindingFlags.Instance | BindingFlags.Public);
backgroundImageProperty?.SetValue(control, null);

pictureBoxType.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)?
    .Invoke(control, Array.Empty<object>());
```

### Pattern 3: Create Instance
```csharp
var soundPlayerType = System.Type.GetType("System.Media.SoundPlayer");
var player = System.Activator.CreateInstance(soundPlayerType, audioPath);
```

### Pattern 4: Invoke Method on Instance
```csharp
var playMethod = soundPlayerType.GetMethod("Play");
playMethod.Invoke(player, null);
```

### Pattern 5: Enumerate Collections
```csharp
var enumerable = ((System.Collections.IEnumerable)controls);
foreach (var ctrl in enumerable)
{
    // Check type and invoke methods
}
```

### Pattern 6: Marshal to UI Thread
```csharp
var invokeRequired = (bool?)(control.GetType().GetProperty("InvokeRequired")?.GetValue(control)) ?? false;
if (invokeRequired)
{
    control.GetType().GetMethod("BeginInvoke", new[] { typeof(Delegate) })?
        .Invoke(control, new object[] { (Action)(() => UpdateUi()) });
}
```

### Pattern 7: Alternate PictureBox Display Path
```csharp
var pictureBox = control;
pictureBox.GetType().GetProperty("Image", BindingFlags.Instance | BindingFlags.Public)?.SetValue(pictureBox, image);
pictureBox.GetType().GetProperty("SizeMode", BindingFlags.Instance | BindingFlags.Public)?.SetValue(pictureBox, Enum.Parse(pictureBox.GetType().GetProperty("SizeMode")!.PropertyType, "Zoom"));
pictureBox.GetType().GetMethod("BringToFront", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)?.Invoke(pictureBox, Array.Empty<object>());
pictureBox.GetType().GetMethod("Invalidate", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)?.Invoke(pictureBox, Array.Empty<object>());
pictureBox.GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)?.Invoke(pictureBox, Array.Empty<object>());
```

### Pattern 8: Control-State Diagnostics and Candidate Scoring
```csharp
var bounds = control.GetType().GetProperty("Bounds", BindingFlags.Instance | BindingFlags.Public)?.GetValue(control)?.ToString();
var location = control.GetType().GetProperty("Location", BindingFlags.Instance | BindingFlags.Public)?.GetValue(control)?.ToString();
var size = control.GetType().GetProperty("Size", BindingFlags.Instance | BindingFlags.Public)?.GetValue(control)?.ToString();
```

The helper now logs the selected PictureBox and its parent chain, and ranks candidate PictureBox controls by visibility and bounds before applying a frame.

---

## Critical Dependencies

### Assemblies Required (via Reflection):
- `System.Drawing` (for `Image`, `SoundPlayer`)
- `System.Windows.Forms` (for `Application`, `Form`, `PictureBox`)
- `System.Diagnostics` (for `Process`, `ProcessStartInfo`)

### Classes Used:
- `System.Drawing.Image` - Load and display images
- `System.Windows.Forms.Application` - Access open forms
- `System.Windows.Forms.Form` - Access form controls
- `System.Windows.Forms.PictureBox` - Display frames
- `System.Media.SoundPlayer` - Play audio
- `System.Diagnostics.Process` - Launch browser
- `System.IO.File` - Check file existence
- `System.Threading.Thread` - Implement delays

---

## Error Handling

Each command includes try-catch block with specific error logging:

```csharp
catch (Exception ex)
{
    LogEvent($"[animation-script-error] {ex.GetType().Name}: {ex.Message}");
}
```

Common errors handled:
- Type not found (e.g., Image, SoundPlayer)
- File not found (frame images, audio files)
- Form/control not found
- Method invocation failures
- Resource disposal errors

---

## Platform Compatibility

### Windows (Direct):
- All methods available directly
- `System.Drawing.Image` works with GDI+
- `System.Media.SoundPlayer` uses WinMM
- Browser launch via shell execution

### macOS (Via Wine/Whisky):
- All methods available via .NET Framework emulation
- Images display in simulated PictureBox
- Audio plays via WinMM emulation
- Browser launch via `open` command
- UI updates are marshaled through `BeginInvoke` when a WinForms dispatcher exists

### Linux:
- All methods available via .NET Framework
- Images display in X11 context (if available)
- Audio plays via WinMM emulation
- Browser launch via `xdg-open` command

---

## Diagnostic Output

### Success Pattern:
```
[animation-script] TryExecuteAnimationScriptSync called with: internet.txt
[animation-script] Loading script from: Z:\animations\internet.txt
[animation-script-action] SHOW 12
[animation-script-action] Displayed frame 12 in pbxAnimation
[animation-script-action] WAIT 1000ms
[animation-script-action] HIDE 12
[animation-script] Script execution completed: internet.txt
```

### Error Pattern:
```
[animation-script-error] Frame image not found: Z:\animations\99.png
[animation-script-error] No PictureBox controls found to display frame
[animation-script-error] SoundPlayer type not found
```

---

## Method Call Sequence During Script Execution

```
TryExecuteAnimationScriptAsync()
│
├─ FindCommandManifestPath()
├─ Resolve root directory
├─ Load script file (File.ReadAllLines)
│
├─ FOR EACH COMMAND LINE:
│  │
│  ├─ HIDE_ALL
│  │  └─ Application.OpenForms → Form.Controls → PictureBox.Visible=false
│  │
│  ├─ SHOW 12
│  │  ├─ File.Exists(12.png)
│  │  ├─ Image.FromFile()
│  │  ├─ Application.OpenForms → Form.Controls
│  │  └─ PictureBox.Image = image; PictureBox.Visible = true
│  │
│  ├─ HIDE 12
│  │  └─ Application.OpenForms → Form.Controls → PictureBox.Visible=false
│  │
│  ├─ WAIT 1000
│  │  └─ Thread.Sleep(1000)
│  │
│  ├─ PLAY_AUDIO audio/internet.wav
│  │  ├─ File.Exists(audio/internet.wav) [try .wav, .mp3]
│  │  ├─ SoundPlayer(audioPath) constructor
│  │  └─ SoundPlayer.Play()
│  │
│  └─ OPEN_URL https://google.com
│     ├─ ProcessStartInfo()
│     ├─ Process.Start()
│     └─ Browser launches
│
└─ Log completion
```

---

**Last Updated:** March 28, 2026  
**Version:** 1.0
