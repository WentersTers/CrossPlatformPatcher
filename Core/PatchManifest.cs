using System.Text.Json;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Traceability record for one patched output. Written next to the output as
/// <c>&lt;output&gt;.patch.json</c> and echoed to the log, so every deployment
/// can be identified afterwards.
/// </summary>
public sealed record PatchManifest(
    string Schema,
    string InputSha256,
    string OutputSha256,
    string Strategy,
    string StrategyFeatures,
    string PatcherVersion,
    string RegistryVersion,
    string TimestampUtc,
    string VersionDisplayName,
    string Certification,
    IReadOnlyList<string> Verification,
    IReadOnlyDictionary<string, string>? StrategyOptions = null,
    IReadOnlyList<string>? CertifiedOs = null);

public static class PatchManifestWriter
{
    public const string SchemaId = "paicom-patch-manifest/v1";

    public static string ManifestPathFor(string outputPath) => outputPath + ".patch.json";

    /// <summary>Write the manifest. Never throws: a missing manifest must not
    /// fail an otherwise good patch.</summary>
    public static void Write(string outputPath, PatchManifest manifest, Action<string>? log = null)
    {
        var path = ManifestPathFor(outputPath);
        try
        {
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            File.WriteAllText(path, json);
            log?.Invoke($"[patch-manifest] {path}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[patch-manifest] could not be written ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>Build a manifest for a successful run.</summary>
    public static PatchManifest ForSuccess(
        string inputSha256,
        string outputSha256,
        VersionEntry entry,
        IReadOnlyList<string> verification)
    {
        return new PatchManifest(
            Schema: SchemaId,
            InputSha256: inputSha256,
            OutputSha256: outputSha256,
            Strategy: entry.DisplayName,
            StrategyFeatures: VersionRegistry.DescribeFeatures(entry.Features),
            PatcherVersion: PatcherInfo.Version,
            RegistryVersion: VersionRegistry.RegistryVersion,
            TimestampUtc: DateTime.UtcNow.ToString("O"),
            VersionDisplayName: entry.DisplayName,
            Certification: entry.Certification,
            Verification: verification,
            StrategyOptions: entry.Options,
            CertifiedOs: entry.CertifiedOs);
    }
}
