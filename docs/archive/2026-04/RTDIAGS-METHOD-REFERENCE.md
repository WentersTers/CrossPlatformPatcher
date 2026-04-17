# Runtime Diagnostics Method Reference
**Generated:** March 28, 2026  
**Source:** runtime-diagnostics-raw-2026-03-26T23:37:51.log

## Method Inventory by Category

This document provides a complete reference of all methods extracted from runtime diagnostics, organized by functional category.

---

## 🎵 AUDIO METHODS

### Audio Loader (SoundPlayer Factory)
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ‪⁪​‍‌‍‫⁬‎‌‏⁮⁬⁭‫​⁫‫‎‭⁪‭‪‌⁬⁭‫‪⁪⁫‮‫⁯⁬​⁪⁬‬⁯⁬‮
Returns: SoundPlayer
Parameters: (String)
Static: True
```
**Purpose:** Load or create a SoundPlayer from a resource string (path)  
**Expected Usage:** `SoundPlayer sound = LoadSound("path/to/audio.wav");`

### Audio Playback Handler
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ‪‭‮⁯‌‎‮‫⁬‬⁭‮⁯‍‪⁭⁬⁮⁯⁪‭⁮⁬⁫⁬‫⁯‌‮‬‎‎‪‫⁪‭⁯‬⁬‎‮
Returns: Void
Parameters: (SoundPlayer)
Static: True
```
**Purpose:** Play or queue the given SoundPlayer for audio output  
**Expected Usage:** `PlayAudio(soundPlayer);`

---

## ⏱️ ANIMATION METHODS

### Async Animation (Task-Based Timing)
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ‌‪⁫‫​⁭⁯⁫‮⁮⁯‪⁬‭⁯‪⁬⁭‪⁮‬‮‎⁮‫‫‌‭⁫⁬​⁫⁫​⁪⁮⁪‮⁮‬‮
Returns: Task
Parameters: (Int32) [millisecond delay]
Static: True
```
**Purpose:** Execute asynchronous animation with specified frame timing  
**Parameter Guide:**
- `0ms` = Immediate (no delay)
- `10-50ms` = Rapid animation frames
- `100-500ms` = Standard animation timing
- `1000ms+` = Slow/transition animations

**Expected Usage:** `Task animFrameTask = AnimateFrame(100);`

### Task Completion Handler
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ⁯⁪⁬⁬⁮⁫⁯⁮‍‬‭‏‪‍⁯⁭⁫⁬‫‭​⁪‮⁬‎⁮‭‫‏‫​‬⁫‎⁭⁬⁯‎‪‭‮
Returns: Void
Parameters: (Task) [completed or running task]
Static: True
```
**Purpose:** Handle completion/synchronization of animation tasks  
**Expected Usage:** `OnAnimationComplete(task);`

---

## 🎨 UI/FORM METHODS

### Form Initialization
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ⁫‎‏​‮⁪⁫⁮⁬⁮⁮‌⁬‌⁪⁪⁪⁪‏​‪⁮⁫‬‬‎⁬‮‫⁯⁭‭‍⁮​‎‮⁮⁫‪‮
Returns: Void
Parameters: (Form) [main window]
Static: True
```
**Purpose:** Initialize or setup the main form and its components  
**Expected Usage:** `InitializeForm(mainForm);`

### UI Factory Methods (4 total)
```
TextBox Factory:
  Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
  Method: ‮⁭⁮⁬⁯‪⁫‫‮‌⁬⁯⁮‪‮‏‫⁫‮​‭‬⁫‏‪‎‌⁬‫‮‮‏‍⁬⁮⁪‬​⁬‮‮
  Returns: TextBox()
  
RichTextBox Factory:
  Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
  Method: ⁪⁮‫‬‫‎⁫‮‏‎‬⁫‎‫‏⁮⁯‪⁭‫⁬‫‎‎⁭⁭⁬‮‫⁫‬‏⁪‌‏‌⁮‬‏⁫‮
  Returns: RichTextBox()

Button Factory:
  Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
  Method: ⁯⁯⁮⁫⁮⁫‮‫​‮‏⁮‫‍‍‎​‪⁭⁭⁭‪⁬⁬‪⁪‪‌‌‍‏⁫⁯‎⁯⁮‏‫‌‮‮
  Returns: Button()

PictureBox Factory:
  Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
  Method: ‪‪‌​‭​‭‍‏⁭​‏⁫‎‍⁫⁪‮‍‌⁪‌⁯‌⁭‬⁬⁫‍⁪⁫​‫‮‪‮⁫‎‍⁭‮
  Returns: PictureBox()
```
**Purpose:** Create new UI control instances  
**Expected Usage:** `TextBox tb = CreateTextBox();`

---

## 🖼️ IMAGE/RENDERING METHODS

### Image Loader
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ‬⁪​‪⁯⁬‬⁮⁬⁭‫⁮‌⁪‮⁬‫‎‎⁮‎⁯‎⁪⁪​‍‪‫⁪‫‮​‬⁫‫⁬⁯‬‬‮
Returns: Image
Parameters: (String) [resource path]
Static: True
```
**Purpose:** Load image resources from file paths  
**Supported Formats:** .png, .jpg, .bmp, .gif  
**Expected Usage:** `Image img = LoadImage("path/to/image.png");`

### Image Display (PictureBox)
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ‫‮⁬‭‌⁭‮⁪⁪‫‬⁮‪​‮‫⁫‭⁭‪‬⁬‬⁫‍⁯‌⁪‏‬⁭⁯‫​‮⁭‍​​‍‮
Returns: Void
Parameters: (PictureBox, Image)
Static: True
```
**Purpose:** Display an Image in a PictureBox control  
**Expected Usage:** `DisplayImage(pictureBox, loadedImage);`

### PictureBox Size Mode
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ‌‌‍‏‬‮‭‪‪⁬⁫⁭‫​⁬‏⁪‭‏‮‏‫‫‎⁪‬‌‭‎⁪⁫⁬‌‭‪⁪⁬⁮⁫‮
Returns: Void
Parameters: (PictureBox, PictureBoxSizeMode)
Static: True
```
**Purpose:** Control image scaling and display mode  
**Size Modes:**
- `Normal` - Image shown at original size
- `StretchImage` - Image stretched to fit control
- `AutoSize` - Control resizes to fit image
- `CenterImage` - Image centered in control
- `Zoom` - Image scaled proportionally

**Expected Usage:** `SetPictureBoxMode(pictureBox, PictureBoxSizeMode.Zoom);`

---

## 📝 TEXT/INPUT METHODS

### Text Box Control Enablement
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ⁯⁯‌‫⁬⁫⁬⁭⁭‪‮‎⁪​⁫‬‮‌‍⁪⁪‭‌⁭‮⁯⁫​‫‪‍‪⁪⁫‎‏‏‌‫‬‮
Returns: Void
Parameters: (TextBoxBase, Boolean) [enable/disable read-only]
Static: True
```
**Purpose:** Enable/disable editing for TextBox or RichTextBox  
**Expected Usage:** `SetTextBoxEditable(textBox, true);`

---

## 🔧 CONTROL METHODS

### Generic Control Update
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ‬⁫⁬⁪‭⁬‭‍‍‌‫‮⁮‭‌‭‫⁯‎⁪⁯‮‫‫‫‌‌‪‍⁫⁯‌⁪‏‏⁪‌⁮​⁪‮
Returns: Void
Parameters: (Control) [any WinForms control]
Static: True
```
**Purpose:** Generic control update/refresh operation  
**Expected Usage:** `RefreshControl(myControl);`

---

## 🎯 EVENT HANDLERS

### Generic Event Handlers (2 identified)
```
Handler 1:
  Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
  Method: ⁬⁭‭⁫⁯‮‪⁪‫‍‎⁫⁫‮⁭⁯‏⁪‍⁯⁪‪⁬‬‫‌⁫⁪​‬‌⁯‌‏‭‏‎​‬‪‮
  Returns: Void
  Parameters: (Object, EventArgs)
  Static: False

Handler 2:
  Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
  Method: ‪‌‫⁯‮‫⁪‭‏‭⁬‍‎​⁪⁭‫⁮‏⁮‬⁫‪‭‍‏‭​⁪⁪⁬​⁭​‏​⁬⁬‏‫‮
  Returns: Void
  Parameters: (Object, EventArgs)
  Static: False
```
**Purpose:** Handle UI event callbacks (click, hover, etc.)  
**Note:** Multiple other handlers detected with similar (Object, EventArgs) signature - likely representing:
- Click events
- Hover events
- Load events
- Paint events
- Resize events

**Expected Usage (via WinForms):**
```csharp
button.Click += (sender, e) => { /* Handler logic */ };
```

---

## 📊 FACTORY METHODS (Utility)

### Dialog Result Provider
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ⁬⁯‫⁫​‌⁬‍‮⁮‏‌​‮​‪‪‎⁮‭‫‎‍‫‬‎​⁪‍​‫⁪‮‎‫⁬⁮‬⁭⁭‮
Returns: DialogResult
Parameters: (String, String) [title, message]
Static: True
```

### String Resources
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ⁮‫‫​‎‪‭⁫‬‪‮⁪⁬‪⁬‍‭‎⁭‮‫‏‫⁬‬⁮‫‭⁯⁯‬⁪‭⁭‫‫⁮‮‫⁯‮
Returns: String
Parameters: (String) [resource key]
Static: True
```

### Process/System Methods
```
Process Spawner:
  Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
  Method: ‪‎‪‪⁬⁪⁮‮‏⁫‮⁭‮‮⁫‫⁪⁫‍‏‍⁪‎‏⁮⁬⁪​‎‫⁬‬‫⁮‬⁬‍‌⁯⁫‮
  Returns: Process
  Parameters: (String) [executable path]
  Static: True

AppDomain Accessor:
  Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
  Method: ⁯‫⁯‮‍‌‏‏‭‭⁪‬‎⁭‍‎‌⁬‌‏⁫‮‮‭⁬‍⁭⁯‮⁫⁪‮‏⁬‍‌​⁬‪⁮‮
  Returns: AppDomain
  Parameters: ()
  Static: True
```

### Network/Web
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ‭⁪⁪⁮‭⁫‏⁮⁮⁫‬⁭‭‮‪⁭‭‏⁯‍‍⁯‏‬‏⁫⁯‍⁪⁪‏⁫‏​​‬⁭⁬‏‎‮
Returns: WebClient
Parameters: ()
Static: True
```

### Randomization
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ‫‍⁬‏‬‫⁫⁭⁫​‮⁭‭‬⁭‌‫⁮⁭⁫​⁯‏⁭⁫⁭​‎‍‎‍‍‮‍‎⁯‫‍​⁭‮
Returns: Random
Parameters: (Int32) [seed]
Static: True
```

---

## 🔬 REFLECTION/TYPE INFORMATION

### ComponentResourceManager
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: ⁯​⁪‮⁯⁭​‭⁪‌‏⁯‭⁪‬⁯‬‏‍⁪⁫‬‍‫⁯‌‍⁬⁮⁫⁪‏⁪‏‎‬⁯‎⁮⁯‮
Returns: ComponentResourceManager
Parameters: (Type) [form type]
Static: True
```
**Purpose:** Manage embedded resources for UI forms

### Property Accessor
```
Class: D9B\+\]}FOz6OifCnUpI8ffY^W!
Method: get_AutoScaleBaseSize
Returns: Size
Parameters: ()
Static: False
```
**Purpose:** Get the auto-scaling base size for form layout

---

## Metrics Summary

| Category | Method Count | Priority | Purpose |
|----------|--------------|----------|---------|
| Audio | 2 | 🔴 Critical | Sound loading and playback |
| Animation | 2 | 🔴 Critical | Async frame timing |
| Form/UI | 5 | 🟡 High | UI initialization and control |
| Image/Render | 3 | 🟡 High | Image resource and display |
| Events | 2+ | 🟡 High | Event-driven functionality |
| Utilities | 6+ | 🟢 Medium | System integration |
| **TOTAL** | **23+** | | |

---

## Testing Strategy

### Immediate Priority (Audio/Animation)
1. Test audio loader with various resource paths
2. Test audio playback with loaded sounds
3. Test async animation with different delay values
4. Validate task completion handling

### Secondary Priority (UI/Rendering)
1. Test form initialization with main window
2. Test image loading from resources
3. Test image display in PictureBox
4. Validate control updates

### Integration Testing
1. Audio playback during animation
2. UI updates during async operations
3. Form state consistency
4. Resource cleanup and disposal

---

**Analysis Date:** 2026-03-28  
**Diagnostic Source:** PAIcom Runtime  
**Platform:** macOS ARM64  
**Framework:** .NET Framework / .NET Standard 2.0
