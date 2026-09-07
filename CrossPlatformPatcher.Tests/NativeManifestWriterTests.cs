using CrossPlatformPatcher.Core;
using CrossPlatformPatcher.Resources;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Verifies that <see cref="NativeManifestWriter"/> renders the manifest with
/// the expected extracted-library, architecture, mode, and CorFlags content.
/// </summary>
public sealed class NativeManifestWriterTests
{
    [Fact]
    public void Write_Writes_Manifest_With_Extracted_Libraries()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "NATIVES_MANIFEST.txt");
        var data = new NativeManifestData(
            TargetMachine: "0x014c",
            TargetArchitecture: "x86",
            OnnxNativeResource: "onnxruntime.native.win-x86.dll",
            ProbeCorFlagsAttempted: true,
            ProbeCorFlagsApplied: true,
            ProbeCorFlagsReason: "APPLIED",
            MigrationMode: MigrationMode.Probe,
            ExtractedNativeLibraries: ["PAIcom.OWW.dll", "libvosk.dll"]);

        NativeManifestWriter.Write(path, data);

        var content = File.ReadAllText(path);
        Assert.Contains("PAIcom Cross-Platform Patcher - Native Library Manifest", content);
        Assert.Contains("PAIcom.OWW.dll", content);
        Assert.Contains("libvosk.dll", content);
    }

    [Fact]
    public void Write_Includes_Architecture_Mode_Onnx_And_CorFlags_Details()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "NATIVES_MANIFEST.txt");
        var data = new NativeManifestData(
            TargetMachine: "0x8664",
            TargetArchitecture: "x64",
            OnnxNativeResource: "onnxruntime.native.win-x64.dll",
            ProbeCorFlagsAttempted: true,
            ProbeCorFlagsApplied: false,
            ProbeCorFlagsReason: "CLI_HEADER_NOT_FOUND",
            MigrationMode: MigrationMode.Full,
            ExtractedNativeLibraries: ["libvosk.dll"]);

        NativeManifestWriter.Write(path, data);

        var content = File.ReadAllText(path);
        Assert.Contains("PE Machine Type: 0x8664", content);
        Assert.Contains("Effective Runtime Architecture: x64", content);
        Assert.Contains("Migration Mode: full", content);
        Assert.Contains("onnxruntime.native.win-x64.dll", content);
        Assert.Contains("Probe Attempted: True", content);
        Assert.Contains("Probe Applied: False", content);
        Assert.Contains("Reason: CLI_HEADER_NOT_FOUND", content);
    }
}
