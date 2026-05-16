# macOS Compatibility Patch vs. Windows Patcher Plan

## Purpose

This plan describes the difference between the macOS compatibility patch path and the Windows patcher path, how the Windows patcher should absorb the same compatibility behavior, which files own each responsibility, and which tests need to exist to keep the patcher reliable.

The goal is not to duplicate the macOS flow. The goal is to make the Windows patcher produce the same compatibility outcomes with Windows-native assumptions, while preserving the macOS launcher and Wine-oriented behavior that already exists.

The features that should be carried over are Vosk usage, command calling, animation playing, and text-based command input. Where possible, the Windows path should avoid Windows-only recognition or speech programs and stay on the cross-platform stack already used by the repo.

## What Is Different

### macOS compatibility patch path

The macOS-oriented path is primarily about running a Windows game under Wine or another compatibility layer. That means the patcher and launchers need to account for:

- shell-based startup on non-Windows hosts
- Wine or Whisky availability and bitness checks
- launcher scripts that can run from Finder or from the terminal
- runtime model discovery and native library extraction that work in a macOS filesystem layout
- compatibility diagnostics that explain why a Wine launch failed

The current repository already reflects that shape in the launcher and runtime compatibility logic.

### Windows patcher path

The Windows patcher path is different because it targets a native Windows launch flow. That means the patcher should focus on:

- direct executable launch behavior instead of shell wrappers
- Windows runtime assets and Windows-friendly extraction output
- preserving the normal Windows process model while still neutralizing fragile game behaviors
- keeping compatibility logic in the IL rewrite layer instead of relying on macOS-specific startup glue
- making the generated output usable from the patcher’s Windows batch launcher and installer flow
- keeping command dispatch, animation playback, and Vosk-based speech handling on the shared cross-platform runtime path
- avoiding Windows-only speech or recognition dependencies unless there is no viable cross-platform alternative

In short, macOS compatibility is mostly a launch-environment problem, while the Windows patcher is mostly an assembly-rewrite and asset-placement problem.

## How The Windows Patcher Should Achieve The Same Outcome

The Windows patcher should keep the compatibility behavior in the patcher core and let the launcher layer stay simple. The patcher should:

1. Identify fragile process-launch sites and rewrite them to safer forms.
2. Prefer Vosk-backed speech and command dispatch over Windows-only recognition programs.
3. Preserve command calling so spoken or scripted commands still reach the game runtime.
4. Keep animation playback working through the shared animation script system.
5. Keep text-based command input working through command `.txt` files and the existing helper pipeline.
6. Inject OpenWakeWord audio hooks where the game’s audio pipeline can be observed.
7. Embed and extract the runtime assets required by the patched game.
8. Generate Windows launch artifacts that point to the patched executable and any required support files.

The macOS path already proves the patcher can safely rewrite behavior. The Windows path should reuse the same patching intent, but keep the output aligned with a native Windows environment.

## File Responsibilities

### Core patcher orchestration

- [Core/AssemblyPatcher.cs](../Core/AssemblyPatcher.cs)
  - Owns the overall patch flow.
  - Loads the target module, runs the individual patchers, extracts embedded runtime files, and writes the final output.
  - Determines whether launcher generation and 64-bit probe behavior should be applied.

- [Program.cs](../inputs/Program.cs)
  - Owns the command-line entry point used during local patching runs.
  - Parses migration mode, dry-run behavior, backup behavior, and OpenWakeWord tuning.
  - Should remain the place where Windows users invoke the patcher from the command line.

### Compatibility rewrite layers

- [Core/ProcessStartCompatibilityPatcher.cs](../Core/ProcessStartCompatibilityPatcher.cs)
  - Rewrites fragile Process.Start call sites so external launch behavior is safer.
  - Needs to stay focused on assembly rewriting, not on any specific host UI.

- [Core/SpeechCompatibilityPatcher.cs](../Core/SpeechCompatibilityPatcher.cs)
  - Wraps System.Speech usage and adds safety logging for missing recognizers or unsupported startup paths.
  - This is the main guardrail for speech-related runtime failures.
  - If Windows-only recognition is not available, the patcher should fall back to the cross-platform Vosk path rather than introducing a new Windows speech dependency.

- [Core/OpenWakeWordCompatibilityPatcher.cs](../Core/OpenWakeWordCompatibilityPatcher.cs)
  - Injects audio hook logic into likely capture points.
  - Keeps wake-word integration active without requiring the caller to know platform-specific audio details.

- [Core/AnimationHandlerPatcher.cs](../Core/AnimationHandlerPatcher.cs)
  - Hooks the large command and animation handler so command phrases can still reach animation and command execution paths.
  - This is the patcher-side entry point for keeping animation and command calling aligned across platforms.

- [Core/AnimationScriptExecutor.cs](../Core/AnimationScriptExecutor.cs)
  - Executes animation scripts from `.txt` files.
  - Owns the shared animation playback behavior for commands like WAIT, SHOW, HIDE, PLAY_AUDIO, and OPEN_URL.

### Runtime assets and launcher output

- [Core/VoskResourceEmbedder.cs](../Core/VoskResourceEmbedder.cs)
  - Embeds Vosk, ONNX, and OpenWakeWord resources into the patcher output.
  - Ensures the patched game can load the runtime helper assets it needs.

- [Core/VoskSpeechRecognizer.cs](../Core/VoskSpeechRecognizer.cs)
  - Controls runtime model discovery and speech recognition startup.
  - Needs to remain compatible with both Windows and macOS launcher layouts.

- [PAIcom.OWW/OpenWakeWordHelper.cs](../PAIcom.OWW/OpenWakeWordHelper.cs)
  - Owns command dispatch, Vosk handoff, and the platform-aware command pipeline.
  - Also owns the file-based command input flow through `input-command.txt` and command manifest loading from `commands.txt`.

- [PAIcom.OWW/BatchFileTranslator.cs](../PAIcom.OWW/BatchFileTranslator.cs)
  - Translates command execution behavior into platform-specific actions.
  - Should remain cross-platform and avoid adding Windows-only command launch programs where a shared path already exists.

- [PAIcom.OWW/FuzzyMatcher.cs](../PAIcom.OWW/FuzzyMatcher.cs)
  - Handles command recognition and fuzzy matching.
  - Keeps spoken commands resilient even when exact recognition is imperfect.

- [Core/LauncherGenerator.cs](../Core/LauncherGenerator.cs)
  - Writes the output launch files.
  - On Windows, this is the place where the batch launcher and setup hook must remain stable.
  - On macOS, it preserves the shell and Finder launch flow.

### Test surface

- [CrossPlatformPatcher.Tests/AssemblyPatcherIntegrationTests.cs](../CrossPlatformPatcher.Tests/AssemblyPatcherIntegrationTests.cs)
  - Verifies output files, launcher generation, and no-op versus patchable behavior.

- [CrossPlatformPatcher.Tests/ProgramCliTests.cs](../CrossPlatformPatcher.Tests/ProgramCliTests.cs)
  - Verifies CLI entry point behavior, backup handling, dry-run handling, and unknown argument handling.

- [CrossPlatformPatcher.Tests/OpenWakeWordTests.cs](../CrossPlatformPatcher.Tests/OpenWakeWordTests.cs)
  - Verifies OpenWakeWord settings and runtime helper behavior.

## Tests That Need To Be Created

The repo already has useful integration coverage, but the Windows patcher needs a few targeted tests to cover the compatibility layer more explicitly.

### 1. Process start rewrite tests

Create tests that verify the process-launch patcher:

- rewrites Process.Start string calls
- rewrites ProcessStartInfo calls
- preserves call-site context information
- does not rewrite unrelated methods
- remains idempotent when a fixture is patched more than once

### 2. Speech wrapper tests

Create tests that verify the speech patcher:

- wraps methods that reference System.Speech
- adds event probes for speech recognition event arguments
- skips methods that already contain exception handling where wrapping would be unsafe
- avoids double-instrumenting an already patched fixture

### 3. OpenWakeWord audio hook tests

Create tests that verify the audio patcher:

- injects the helper initialization call
- injects the audio handoff call at the start of likely audio methods
- chooses the intended audio parameter when multiple parameters exist
- skips helper methods and already-instrumented methods
- leaves unrelated methods untouched

### 4. Command input and command calling tests

Create tests that verify the command pipeline:

- reads command manifests from `commands.txt`
- honors file-based command input from `input-command.txt`
- preserves command token normalization and dispatch behavior
- routes command calls through the shared command dispatch layer instead of a Windows-only helper
- still allows command output to reach animation, browser, and process actions when appropriate

### 5. Launcher generation tests

Create tests that verify launcher output for the Windows patcher:

- generates the Windows batch launcher with the expected executable target
- preserves the macOS shell and Finder launch files
- writes the setup launcher hook used by the installer flow
- keeps launcher content aligned with the patched executable name

### 6. Animation script execution tests

Create tests that verify animation playback:

- loads scripts from `.txt` files
- executes SHOW, HIDE, WAIT, PLAY_AUDIO, and OPEN_URL commands
- uses the cross-platform animation dispatcher when present
- falls back safely when the dispatcher or audio player is unavailable
- ignores malformed commands without crashing the patcher runtime

### 7. Assembly patch integration tests

Expand the integration tests so they verify:

- a no-op input remains byte-for-byte identical when no IL rewrite is needed
- a patchable input produces a modified output
- launcher files are generated alongside the patched executable
- the patched assembly contains the injected helper type when expected
- native library extraction paths remain valid on Windows output layouts
- command and animation fixtures still behave after patching

### 8. CLI behavior tests

Add or extend CLI tests to confirm:

- dry-run still reports patch points without writing output
- backup mode still preserves the original executable
- migration mode is respected in the patcher output path
- unknown flags do not break patching
- no Windows-only speech program is required for the normal patch flow

## Windows-Facing Differences To Preserve

The Windows patcher should continue to behave differently from the macOS launcher flow in a few deliberate ways:

- use the batch launcher as the primary Windows handoff
- avoid depending on Finder wrappers or shell-only startup behavior for the main Windows path
- keep asset extraction compatible with Windows file locking and executable naming rules
- keep compatibility diagnostics readable from standard console output
- treat the Windows patcher as the authoritative native patch flow rather than a Wine emulation flow
- prefer the existing cross-platform Vosk and command stack over Windows recognition programs when both are available

## Implementation Order

1. Lock down the patcher behavior with focused unit tests for process, speech, audio, and command rewrites.
2. Extend the integration tests for launcher generation, animation scripts, and output layout.
3. Confirm Windows patch outputs still produce the expected launcher files, extracted assets, and command inputs.
4. Keep the macOS launcher flow intact while the Windows patcher gets any required compatibility changes.
5. Add any missing documentation links after the behavior is covered by tests.

## Acceptance Criteria

- The Windows patcher performs the same compatibility rewrites that are needed for the game to start reliably.
- Vosk, command calling, animation playing, and command `.txt` input remain part of the carried-over feature set.
- macOS launcher behavior remains intact and separate from the Windows batch launcher flow.
- The plan names the exact files that own patching, runtime assets, launchers, and tests.
- The new tests cover process rewrites, speech wrappers, audio hooks, launcher generation, and integration behavior.
- The plan avoids introducing Windows-only recognition or speech programs unless there is no cross-platform alternative.
- No code examples are needed in this plan, only the implementation and validation steps.