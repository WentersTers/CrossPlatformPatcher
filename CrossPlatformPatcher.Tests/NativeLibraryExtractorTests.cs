using CrossPlatformPatcher.Resources;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Exercises <see cref="NativeLibraryExtractor"/> name mapping, the default
/// embedded library list, and the directory-based extraction surface.
/// </summary>
public sealed class NativeLibraryExtractorTests
{
    [Theory]
    [InlineData("onnxruntime.managed.dll", "Microsoft.ML.OnnxRuntime.dll")]
    [InlineData("vosk.managed.dll", "Vosk.dll")]
    [InlineData("vosk.native.win-x64.dll", "libvosk.dll")]
    [InlineData("vosk.native.win-gcc.dll", "libgcc_s_seh-1.dll")]
    [InlineData("vosk.native.win-x86.dll", "libvosk.dll")]
    [InlineData("vosk.native.win-gcc-x86.dll", "libgcc_s_sjlj-1.dll")]
    [InlineData("vosk.native.win-stdc-x86.dll", "libstdc++-6.dll")]
    [InlineData("vosk.native.win-pthread-x86.dll", "libwinpthread-1.dll")]
    [InlineData("naudio.core.dll", "NAudio.Core.dll")]
    [InlineData("onnxruntime.dll", "onnxruntime.dll")]
    [InlineData("libonnxruntime.so", "libonnxruntime.so")]
    [InlineData("onnxruntime.dylib", "onnxruntime.dylib")]
    public void MapTargetName_Maps_Known_And_Unknown_Sources(string source, string expected)
    {
        Assert.Equal(expected, NativeLibraryExtractor.MapTargetName(source));
    }

    [Fact]
    public void DefaultEmbeddedDllNames_Is_NonEmpty_And_Contains_Expected_Entries()
    {
        Assert.NotEmpty(NativeLibraryExtractor.DefaultEmbeddedDllNames);
        Assert.Contains("PAIcom.OWW.dll", NativeLibraryExtractor.DefaultEmbeddedDllNames);
        Assert.Contains("onnxruntime.managed.dll", NativeLibraryExtractor.DefaultEmbeddedDllNames);
        Assert.Contains("vosk.managed.dll", NativeLibraryExtractor.DefaultEmbeddedDllNames);
        Assert.Contains("System.Memory.dll", NativeLibraryExtractor.DefaultEmbeddedDllNames);
    }

    [Fact]
    public void ExtractFromDirectory_Renames_Artifacts_And_Skips_Missing_Files()
    {        using var temp = new TempDirectory();
        File.WriteAllBytes(System.IO.Path.Combine(temp.Path, "onnxruntime.managed.dll"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(System.IO.Path.Combine(temp.Path, "vosk.native.win-x64.dll"), new byte[] { 4, 5, 6 });

        string[] names = ["onnxruntime.managed.dll", "vosk.native.win-x64.dll", "does.not.exist.dll"];
        var artifacts = NativeLibraryExtractor.ExtractFromDirectory(temp.Path, names);

        Assert.Equal(2, artifacts.Count);
        Assert.Equal("Microsoft.ML.OnnxRuntime.dll", artifacts[0].TargetName);
        Assert.Equal(new byte[] { 1, 2, 3 }, artifacts[0].Bytes);
        Assert.Equal("libvosk.dll", artifacts[1].TargetName);
        Assert.Equal(new byte[] { 4, 5, 6 }, artifacts[1].Bytes);
    }

    [Fact]
    public void BuildEmbeddedDllNames_Stable_X86_Selects_X86_Natives()
    {
        // The real-hardware v0.1.1 path: 32-bit game, Stable mode (no
        // CorFlags rewrite) must ship the x86 native set, not silently
        // skip it (which produced 0x8007007E on first boot).
        var names = NativeLibraryExtractor.BuildEmbeddedDllNames(
            shouldUse64BitNatives: false, isX86Target: true);
        Assert.Contains("vosk.native.win-x86.dll", names);
        Assert.Contains("vosk.native.win-gcc-x86.dll", names);
        Assert.Contains("vosk.native.win-stdc-x86.dll", names);
        Assert.Contains("vosk.native.win-pthread-x86.dll", names);
        Assert.DoesNotContain("vosk.native.win-x64.dll", names);
    }
}
