# Release Notes

## Fixes

- Wake word now works on Linux. The audio matcher depended on host-OS assembly resolution, so only 2 of 21 hook points were injected while the patcher still reported success. Now 20, matching Windows.
- Vosk model flattening and the full-mode default now apply on Linux, so offline speech recognition actually starts instead of silently staying off.

Instructions are in `README.md` inside the download.
