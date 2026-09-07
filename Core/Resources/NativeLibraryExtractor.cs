namespace CrossPlatformPatcher.Resources;

/// <summary>
/// A single extracted native-library artifact: the deployment file name
/// (<see cref="TargetName"/>) and the raw file bytes.
/// </summary>
/// <param name="TargetName">The file name the artifact should be written as.</param>
/// <param name="Bytes">The raw bytes of the native library.</param>
public sealed record NativeLibraryArtifact(string TargetName, byte[] Bytes);

/// <summary>
/// Extracts and renames the native libraries that <c>AssemblyPatcher</c> embeds
/// alongside the patched executable.  This type owns the canonical list of
/// embedded library names and the source-name → deployment-name mapping so the
/// extraction logic is a single, testable unit.
/// </summary>
public static class NativeLibraryExtractor
{
    /// <summary>
    /// The canonical set of managed/native library resource names that are always
    /// embedded into the output directory, regardless of target architecture.
    /// </summary>
    public static IReadOnlyList<string> DefaultEmbeddedDllNames { get; } =
    [
        "PAIcom.OWW.dll",
        "System.Memory.dll",
        "System.Buffers.dll",
        "System.Numerics.Vectors.dll",
        "System.Runtime.CompilerServices.Unsafe.dll",
        "onnxruntime.managed.dll",
        "vosk.managed.dll",
        "naudio.core.dll",
        "naudio.winmm.dll",
    ];

    /// <summary>
    /// Returns the complete set of embedded library resource names for a given
    /// target, appending the architecture-specific Vosk natives to
    /// <see cref="DefaultEmbeddedDllNames"/>.
    /// </summary>
    /// <param name="shouldUse64BitNatives">
    /// When <c>true</c> (Probe/Full migration on an x86 PE) x64 Vosk natives are
    /// selected so they are compatible with the rewritten 64-bit process.
    /// </param>
    /// <param name="isX86Target">Whether the target PE machine type is x86.</param>
    public static IReadOnlyList<string> BuildEmbeddedDllNames(bool shouldUse64BitNatives, bool isX86Target)
    {
        var names = new List<string>(DefaultEmbeddedDllNames);

        if (shouldUse64BitNatives)
        {
            names.AddRange(
            [
                "vosk.native.win-x64.dll",
                "vosk.native.win-gcc.dll",
                "vosk.native.win-stdc.dll",
                "vosk.native.win-pthread.dll",
            ]);
        }
        else if (isX86Target)
        {
            names.AddRange(
            [
                "vosk.native.win-x86.dll",
                "vosk.native.win-gcc-x86.dll",
                "vosk.native.win-stdc-x86.dll",
                "vosk.native.win-pthread-x86.dll",
            ]);
        }
        else
        {
            names.AddRange(
            [
                "vosk.native.win-x64.dll",
                "vosk.native.win-gcc.dll",
                "vosk.native.win-stdc.dll",
                "vosk.native.win-pthread.dll",
            ]);
        }

        return names;
    }

    /// <summary>
    /// Maps an embedded resource/source file name to its deployment file name.
    /// Names without a mapping are returned unchanged.
    /// </summary>
    public static string MapTargetName(string sourceName) => sourceName switch
    {
        "onnxruntime.managed.dll" => "Microsoft.ML.OnnxRuntime.dll",
        "vosk.managed.dll" => "Vosk.dll",
        "vosk.native.win-x86.dll" => "libvosk.dll",
        "vosk.native.win-gcc-x86.dll" => "libgcc_s_sjlj-1.dll",
        "vosk.native.win-stdc-x86.dll" => "libstdc++-6.dll",
        "vosk.native.win-pthread-x86.dll" => "libwinpthread-1.dll",
        "vosk.native.win-x64.dll" => "libvosk.dll",
        "vosk.native.win-gcc.dll" => "libgcc_s_seh-1.dll",
        "vosk.native.win-stdc.dll" => "libstdc++-6.dll",
        "vosk.native.win-pthread.dll" => "libwinpthread-1.dll",
        "naudio.core.dll" => "NAudio.Core.dll",
        "naudio.winmm.dll" => "NAudio.WinMM.dll",
        _ => sourceName,
    };

    /// <summary>
    /// Reads each named source from its <see cref="Stream"/>, renames it via
    /// <see cref="MapTargetName"/>, and materialises the result as a collection
    /// of <see cref="NativeLibraryArtifact"/> values.  The provided streams are
    /// copied immediately and are not disposed by this method.
    /// </summary>
    public static IReadOnlyList<NativeLibraryArtifact> ExtractFromStreams(
        IEnumerable<(string SourceName, Stream Stream)> sources)
    {
        var artifacts = new List<NativeLibraryArtifact>();
        foreach (var (sourceName, stream) in sources)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            artifacts.Add(new NativeLibraryArtifact(MapTargetName(sourceName), buffer.ToArray()));
        }

        return artifacts;
    }

    /// <summary>
    /// Enumerates the named native-library artifacts from a directory, renames
    /// each via <see cref="MapTargetName"/>, and materialises them.  Source files
    /// that do not exist in <paramref name="directory"/> are skipped.
    /// </summary>
    public static IReadOnlyList<NativeLibraryArtifact> ExtractFromDirectory(
        string directory, IReadOnlyList<string> sourceNames)
    {
        var artifacts = new List<NativeLibraryArtifact>();
        foreach (var sourceName in sourceNames)
        {
            var path = Path.Combine(directory, sourceName);
            if (!File.Exists(path))
                continue;

            artifacts.Add(new NativeLibraryArtifact(MapTargetName(sourceName), File.ReadAllBytes(path)));
        }

        return artifacts;
    }
}
