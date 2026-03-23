namespace CrossPlatformPatcher.Core;

/// <summary>
/// Copies ONNX Runtime native libraries from the local NuGet cache into
/// Core/NativeLibraries using the filenames expected by the project file.
/// </summary>
public static class OnnxNativeLibraryManager
{
    public static int PrepareFromNuGetCache(string repoRoot, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentException("repoRoot cannot be null or empty", nameof(repoRoot));

        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget",
            "packages",
            "microsoft.ml.onnxruntime");

        if (!Directory.Exists(packageRoot))
        {
            log?.Invoke($"[oww] ONNX Runtime package cache not found at: {packageRoot}");
            return 0;
        }

        var versionDir = Directory.GetDirectories(packageRoot)
            .OrderByDescending(Path.GetFileName)
            .FirstOrDefault();

        if (versionDir is null)
        {
            log?.Invoke("[oww] No ONNX Runtime package versions found in NuGet cache.");
            return 0;
        }

        var nativeOutDir = Path.Combine(repoRoot, "Core", "NativeLibraries");
        Directory.CreateDirectory(nativeOutDir);

        var mappings = new (string SourceRelative, string TargetName)[]
        {
            (Path.Combine("runtimes", "win-x86", "native", "onnxruntime.dll"), "onnxruntime-win-x86.dll"),
            (Path.Combine("runtimes", "win-x64", "native", "onnxruntime.dll"), "onnxruntime-win-x64.dll"),
            (Path.Combine("runtimes", "linux-x64", "native", "libonnxruntime.so"), "onnxruntime-linux-x64.so"),
            (Path.Combine("runtimes", "linux-arm64", "native", "libonnxruntime.so"), "onnxruntime-linux-arm64.so"),
            (Path.Combine("runtimes", "osx-x64", "native", "libonnxruntime.dylib"), "onnxruntime-osx-x64.dylib"),
            (Path.Combine("runtimes", "osx-arm64", "native", "libonnxruntime.dylib"), "onnxruntime-osx-arm64.dylib"),
        };

        var copied = 0;

        foreach (var (sourceRelative, targetName) in mappings)
        {
            var sourcePath = Path.Combine(versionDir, sourceRelative);
            var targetPath = Path.Combine(nativeOutDir, targetName);

            if (!File.Exists(sourcePath))
            {
                log?.Invoke($"[oww] Missing in cache: {sourceRelative}");
                continue;
            }

            File.Copy(sourcePath, targetPath, overwrite: true);
            copied++;
            log?.Invoke($"[oww] Copied {targetName}");
        }

        log?.Invoke($"[oww] ONNX native prep complete. Files copied: {copied}");
        return copied;
    }
}
