# Core/NativeLibraries/

These files are already populated automatically by the build setup.
Do **not** replace them unless you are upgrading the Vosk version.

## Files bundled (Vosk 0.3.38)

| File | Platform | Notes |
|---|---|---|
| `vosk-win-x64.dll` | Windows x64 + Wine | Main Vosk library |
| `vosk-win-gcc.dll` | Windows x64 + Wine | GCC runtime (libgcc_s_seh-1) |
| `vosk-win-stdc.dll` | Windows x64 + Wine | C++ stdlib (libstdc++-6) |
| `vosk-win-pthread.dll` | Windows x64 + Wine | pthread runtime (libwinpthread-1) |
| `libvosk-linux-x64.so` | Linux x64 | |
| `libvosk-osx-universal.dylib` | macOS (Intel + Apple Silicon) | Universal binary |

Linux ARM64 is not included in the Vosk 0.3.38 NuGet package. The feature
will degrade gracefully to file-based command dispatch on that platform.

## To upgrade Vosk version

```sh
# Update the version in the temp project, then copy from NuGet cache:
mkdir tmp && cd tmp
dotnet new console -f net48
dotnet add package Vosk --version NEW_VERSION
dotnet publish -r win-x64 --self-contained false -o out

cp out/libvosk.dll         ../Core/NativeLibraries/vosk-win-x64.dll
cp out/libgcc_s_seh-1.dll  ../Core/NativeLibraries/vosk-win-gcc.dll
cp out/libstdc++-6.dll     ../Core/NativeLibraries/vosk-win-stdc.dll
cp out/libwinpthread-1.dll ../Core/NativeLibraries/vosk-win-pthread.dll

# From NuGet cache (~/.nuget/packages/vosk/NEW_VERSION/build/lib/)
cp linux-x64/libvosk.so        ../Core/NativeLibraries/libvosk-linux-x64.so
cp osx-universal/libvosk.dylib ../Core/NativeLibraries/libvosk-osx-universal.dylib
```

Also update `Vosk.dll` in `Core/ManagedLibraries/` to the same version.
