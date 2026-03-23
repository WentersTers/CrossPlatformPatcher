# Core/ManagedLibraries/

These files are already populated automatically by the build setup.
Do **not** replace them unless you are upgrading versions.

## Files bundled

| File | Version | Notes |
|---|---|---|
| `Vosk.dll` | 0.3.38 | Managed wrapper for the Vosk C API |
| `NAudio.Core.dll` | 2.2.1 | Audio abstraction layer |
| `NAudio.WinMM.dll` | 2.2.1 | WinMM microphone capture (works via Wine) |
| `Newtonsoft.Json.dll` | 13.0.3 | JSON parser (Vosk result parsing) |

## To upgrade versions

```sh
mkdir tmp && cd tmp
dotnet new console -f net48
dotnet add package Vosk --version NEW_VOSK_VERSION
dotnet add package NAudio --version NEW_NAUDIO_VERSION
dotnet add package Newtonsoft.Json --version NEW_JSON_VERSION
dotnet publish -r win-x64 --self-contained false -o out

cp out/Vosk.dll            ../Core/ManagedLibraries/
cp out/NAudio.Core.dll     ../Core/ManagedLibraries/
cp out/NAudio.WinMM.dll    ../Core/ManagedLibraries/
cp out/Newtonsoft.Json.dll ../Core/ManagedLibraries/
```

Also update the native libs in `Core/NativeLibraries/` to the same Vosk version.
