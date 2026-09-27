# PAIcom Voice v0.1.2-dev-hotfix — Release Notes

## What this is

A Linux fix for the v0.1.2 release. The voice layer looked patched on Linux
and did nothing; the wake word never fired and the offline recogniser never
started, while the patcher reported success the whole time. This release
fixes that, plus the model handling that was keeping the recogniser off.

Nothing about the patch format changed. If you are on Windows and v0.1.2
works for you, you do not need this.

## What's fixed

- **Wake word now works on Linux.** The audio matcher decided whether a
  method handled audio by loading the declaring assembly from the host
  operating system. `System.Speech` and `System.Windows.Forms` do not exist
  in the .NET Linux shared framework, so on Linux the check failed and only
  2 of 21 audio hook points were injected — silently, because the patcher
  reported success either way. It now finds 20 of 21 on Linux, matching
  Windows on the same input file.

- **A collapsed match can no longer report success.** If the patcher finds
  fewer than 10 injection points it now prints a `[WARN]` and says to treat
  the patch as failed, instead of exiting `[OK]`. This is the failure that
  hid the bug above, and it can hide the next one too.

- **The voice model is flattened before startup on Linux.** The model
  download can unpack the archive into a subdirectory named after itself,
  sometimes twice over. `run.bat` already moved such a model flat before
  startup; `run.sh` only pointed at `models/` and left it nested, so the
  recogniser resolved no model and speech recognition stayed off with no
  error shown. Both launchers now flatten first and prune the empty
  wrapper directories. Both the game folder and your home models folder are
  covered.

- **Linux launches in full mode by default,** matching Windows. In stable
  mode the offline recogniser is switched off by design, so Vosk never
  started on Linux even with the model sitting right there.

## How to run it

The Linux download is now a plain `.tar.gz` instead of an `.AppImage`.

```
tar xzf CrossPlatformPatcher-0.1.2-dev-hotfix-linux-x64.tar.gz
cd CrossPlatformPatcher-0.1.2-dev-hotfix-linux-x64
chmod +x CrossPlatformPatcher
```

Keep `CrossPlatformPatcher` and `libonnxruntime.so` in the same folder.

Then run the patcher from inside your PAIcom folder, naming the game file:

```
./CrossPlatformPatcher PAIcom.exe --out PAIcom_patched.exe
```

Then start the patched game with the generated launcher:

```
sh run.sh
```

**Run the patcher in your PAIcom folder, not an empty directory.** The game
reads its own asset folders (`files/`, `animations/`, `audio/`, `UI/`,
`custom-commands/`) relative to where it runs. Patch into an otherwise empty
directory and the game starts, then stops on a missing-file error such as
`files/yes.txt`. That is not a patcher fault and not a Wine fault — it does
the same on Windows.

## Why not an AppImage

The v0.1.2 `.AppImage` had to mount itself through FUSE before it could
start, and on some SteamOS setups that mount took many minutes with no
output at all. The runtime cannot be told to skip the mount from inside the
AppImage, because the mount happens before the launcher script runs.

This release drops the wrapper. The tarball has nothing to mount, so it
starts immediately — 52 ms measured cold on the test machine, against a
15-minute wait. If you want a single double-clickable file, AppImage is
still the right tool, but it needs a current runtime rather than the older
one v0.1.2 shipped.

## What we verified

Ubuntu 22.04, Wine 11.0, native ext4, no FUSE, against the current PAIcom
build:

```
wake word        model loaded, inference worker running,
                 microphone capture live, wake gate armed
speech           Vosk recogniser initialised, backend.active=vosk+onnx
injection pts    20 Linux / 21 Windows, same input file
patch points     182/182 Linux, 183/183 Windows
game             reaches the credits screen and continues past it
```

The full 309-test suite passes.

## Known issues (unchanged from v0.1.2)

The known-issues list in the v0.1.2 notes still applies in full. Nothing was
added and nothing was removed.

One entry is worth repeating because this release touches it: the
"startup error pop-up when the voice model sits in a subfolder" entry
described a `.NET` error about a secure channel and a missing start button.
The flattening change above strengthens the mitigation on both platforms,
including the doubly-nested case the v0.1.2 fix did not cover.

Still true, and unchanged: this release is unsigned. The Verify section
below shows how to check your download is genuine.

## What you need

- 64-bit Linux with PAIcom installed, plus Wine or Proton to play the
  patched game. The patcher itself does not need Wine.
- The patcher brings its own voice model and speech component. It does not
  install system software.
- Virtual audio cable is only needed if you play game audio back into the
  voice input. It is unchanged from v0.1.2 and is not part of this Linux
  download.

## Verify

```
CrossPlatformPatcher-0.1.2-dev-hotfix-linux-x64.tar.gz
SHA-256: D8B22DCD553CF838E5868B94B442662CD116287FA394F8783D83749D39677155
```

Linux:

```
sha256sum CrossPlatformPatcher-0.1.2-dev-hotfix-linux-x64.tar.gz
```

Windows:

```
certutil -hashfile CrossPlatformPatcher-0.1.2-dev-hotfix-linux-x64.tar.gz SHA256
```

```
CrossPlatformPatcher-0.1.2-dev-hotfix-win-x64.exe
SHA-256: 6D259CA682DC09B9AF6EDA6E5A5C86E81363597808C3FB94932119BAA465CE73
```

No bundled verifier is shipped — verify externally. The Windows build is
included for parity and for the launcher fix; it carries the same
corrections and no new behaviour.
