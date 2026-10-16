using CrossPlatformPatcher.Core;

namespace CrossPlatformPatcher.Resources;

/// <summary>
/// The data required to render a native-library manifest.
/// </summary>
public sealed record NativeManifestData(
    string TargetMachine,
    string TargetArchitecture,
    string? OnnxNativeResource,
    bool ProbeCorFlagsAttempted,
    bool ProbeCorFlagsApplied,
    string? ProbeCorFlagsReason,
    MigrationMode MigrationMode,
    IReadOnlyList<string> ExtractedNativeLibraries);

/// <summary>
/// Writes the human-readable <c>NATIVES_MANIFEST.txt</c> verification file that
/// accompanies the extracted native libraries.  This is a single-responsibility
/// writer: it takes the fully-prepared <see cref="NativeManifestData"/> and
/// renders it to disk, swallowing I/O failures with a warning so an un-writable
/// manifest never aborts the patch.
/// </summary>
public static class NativeManifestWriter
{
    /// <summary>
    /// Writes the manifest resource to <paramref name="outputPath"/>.  Any
    /// failure is logged as a warning rather than thrown, matching the historical
    /// behaviour of the orchestrator.
    /// </summary>
    public static void Write(string outputPath, NativeManifestData data)
    {
        try
        {
            var manifestLines = new List<string>
            {
                "PAIcom Cross-Platform Patcher - Native Library Manifest",
                "======================================",
                $"Generated: {DateTime.UtcNow:O}",
                $"Effective Runtime Architecture: {data.TargetArchitecture}",
                $"Migration Mode: {MigrationModeParser.ToCliString(data.MigrationMode)}",
                $"",
                "Extracted Native Libraries:",
            };

            foreach (var lib in data.ExtractedNativeLibraries)
                manifestLines.Add($"  {lib}");

            manifestLines.AddRange(
            [
                "",
                "ONNX Runtime Bundle:",
                $"  {data.OnnxNativeResource ?? "(not selected)"}",
                "",
                "Architecture Selection:",
                $"  PE Machine Type: {data.TargetMachine}",
                $"  Effective Architecture: {data.TargetArchitecture}",
                $"  Migration Mode: {MigrationModeParser.ToCliString(data.MigrationMode)}",
                $"  Note: In Probe/Full modes with x86 PE, x64 natives are extracted",
                $"        because CorFlags rewriting allows the process to run as 64-bit.",
                "",
                "CorFlags Probe Details:",
                $"  Probe Attempted: {data.ProbeCorFlagsAttempted}",
                $"  Probe Applied: {data.ProbeCorFlagsApplied}",
                $"  Reason: {data.ProbeCorFlagsReason ?? "(none)"}",
                "",
                "Verification:",
                "  Run the launcher script with verbose logging enabled.",
                "  Check /launcher-runtime.log for [diag] and [native-load] entries.",
            ]);

            File.WriteAllLines(outputPath, manifestLines);
            Console.WriteLine($"  [manifest] Wrote manifest to {outputPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] Failed to write natives manifest: {ex.Message}");
        }
    }
}
