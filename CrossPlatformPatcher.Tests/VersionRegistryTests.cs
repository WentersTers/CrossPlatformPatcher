using System.Security.Cryptography;
using dnlib.DotNet;
using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Phase 1 version registry: classification, refusal, registry query, and the
/// post-patch verification rules. The real-binary happy path is exercised by
/// the CLI validation on the rig; these tests cover the pure logic.
/// </summary>
public class VersionRegistryTests
{
    private static byte[] PadManagedAssemblyTo(long size)
    {
        var source = File.ReadAllBytes(typeof(VersionRegistryTests).Assembly.Location);
        var bytes = new byte[size];
        Array.Copy(source, bytes, Math.Min(source.Length, bytes.Length));
        // Pad with non-zero bytes so the tail is not mistaken for an empty file.
        for (var i = source.Length; i < bytes.Length; i += 4096)
            bytes[i] = 0x5A;
        return bytes;
    }

    [Fact]
    public void RegistryHasCertifiedBaselineWithFullStrategy()
    {
        var entry = VersionRegistry.Find(VersionRegistry.CertifiedBaselineSha);

        Assert.NotNull(entry);
        Assert.Equal(VersionSupport.Certified, entry!.Support);
        Assert.Equal(PatchFeatures.Full, entry.Features);
        Assert.Contains(VersionRegistry.CertifiedBaselineSha, VersionRegistry.Supported.Select(e => e.InputSha256));
    }

    [Fact]
    public void RegistryListsCurrentSteamBuildAsCertifiedWithHookSkip()
    {
        var entry = VersionRegistry.Find(VersionRegistry.SteamCurrentSha);

        Assert.NotNull(entry);
        Assert.Equal(VersionSupport.Certified, entry!.Support);
        Assert.True(entry.IsRunnable);
        Assert.Equal(PatchFeatures.Full, entry.Features);
        Assert.True(entry.SkipModuleLevelHooks);
        Assert.Contains(VersionRegistry.SteamCurrentSha, VersionRegistry.Supported.Select(e => e.InputSha256));
    }

    [Fact]
    public void RegistryIsQueryable()
    {
        var supported = VersionRegistry.DescribeSupported();
        var all = VersionRegistry.DescribeAll();

        Assert.Contains(VersionRegistry.CertifiedBaselineSha[..8], supported);
        Assert.Contains(VersionRegistry.SteamCurrentSha[..8], all);
        Assert.Contains("materialization", VersionRegistry.DescribeFeatures(PatchFeatures.Full));
        Assert.Contains("watchdog", VersionRegistry.DescribeFeatures(PatchFeatures.Full));
        Assert.Contains("speech", VersionRegistry.DescribeFeatures(PatchFeatures.Full));
        Assert.Contains("oww", VersionRegistry.DescribeFeatures(PatchFeatures.Full));
        Assert.Contains("certified on:", all);
    }

    [Fact]
    public void CertifiedBaselineListsItsOperatingSystems()
    {
        var entry = VersionRegistry.Find(VersionRegistry.CertifiedBaselineSha)!;
        var os = entry.CertifiedOs ?? Array.Empty<string>();

        Assert.Contains(os, s => s.StartsWith("Ubuntu 24.04"));
        Assert.Contains(os, s => s.StartsWith("Fedora 41"));
        Assert.Contains(os, s => s.StartsWith("Fedora 43"));
        Assert.Contains(os, s => s.StartsWith("Fedora 44"));
        Assert.Contains(os, s => s.StartsWith("Arch"));
    }

    [Fact]
    public void SteamBuildCertifiesUbuntuWithProtonExperimentalOnly()
    {
        var entry = VersionRegistry.Find(VersionRegistry.SteamCurrentSha)!;
        var os = entry.CertifiedOs ?? Array.Empty<string>();

        Assert.Single(os);
        Assert.Equal("Ubuntu 24.04 (Steam Proton Experimental)", os[0]);
    }

    [Fact]
    public void SmallFileIsNotAPAIcomExecutable()
    {
        var bytes = new byte[1024];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';

        var gate = VersionGate.Classify(bytes, "deadbeef");

        Assert.Equal(VersionGateKind.NotPaiCom, gate.Kind);
        Assert.Equal(VersionGate.ExitNotPaiCom, gate.ExitCode);
        Assert.Contains(gate.Message, l => l.Contains("does not appear to be a PAIcom executable"));
        Assert.Contains(gate.Message, l => l.Contains("No files were changed"));
    }

    [Fact]
    public void UnknownButPlausibleGameIsRefusedAsUnsupported()
    {
        var bytes = PadManagedAssemblyTo(VersionGate.MinimumPaiComBytes + 1024 * 1024);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var gate = VersionGate.Classify(bytes, sha);

        Assert.Equal(VersionGateKind.Unsupported, gate.Kind);
        Assert.Equal(VersionGate.ExitUnsupported, gate.ExitCode);
        Assert.Contains(gate.Message, l => l.Contains("not yet supported"));
        Assert.Contains(gate.Message, l => l.Contains("No files were changed"));
        Assert.Contains(gate.Message, l => l.Contains(sha[..8]));
        Assert.Contains(gate.Message, l => l.Contains(VersionRegistry.CertifiedBaselineSha[..8]));
    }

    [Fact]
    public void DecoyOrWrongExecutableIsNotAPAIcomExecutable()
    {
        // A wrong-but-managed file of game size would be "unsupported"; the
        // decoy is tiny, so it must land in the structural refusal instead.
        var decoyPath = Path.Combine("PAIcom Test Folder", "PAIcom [LAUNCH HERE].exe");
        if (!File.Exists(decoyPath))
            return; // fixture not present in this checkout

        var bytes = File.ReadAllBytes(decoyPath);
        var gate = VersionGate.Classify(bytes, Convert.ToHexString(SHA256.HashData(bytes)));

        Assert.Equal(VersionGateKind.NotPaiCom, gate.Kind);
        Assert.Contains(gate.Message, l => l.Contains("does not appear to be a PAIcom executable"));
    }

    [Fact]
    public void VerificationCatchesMissingResources()
    {
        var entry = VersionRegistry.Find(VersionRegistry.CertifiedBaselineSha)!;

        using var module = new ModuleDefUser("verify-fixture");
        module.Cor20HeaderFlags = (dnlib.DotNet.MD.ComImageFlags)0x1;
        foreach (var name in entry.RequiredOutputResources)
            module.Resources.Add(new EmbeddedResource(name, new byte[] { 1, 2, 3 }, ManifestResourceAttributes.Public));

        Assert.Empty(PatchVerifier.Verify(module, entry));

        module.Resources.Remove(module.Resources.First(r => r.Name == entry.RequiredOutputResources[0]));

        var failures = PatchVerifier.Verify(module, entry);
        Assert.Contains(failures, f => f.Contains("missing required resource"));
    }

    [Fact]
    public void VerificationCatchesPairingDrift()
    {
        var entry = VersionRegistry.Find(VersionRegistry.CertifiedBaselineSha)!;

        using var module = new ModuleDefUser("verify-fixture");
        module.Cor20HeaderFlags = (dnlib.DotNet.MD.ComImageFlags)0x1;
        foreach (var name in entry.RequiredOutputResources)
        {
            module.Resources.Add(new EmbeddedResource(name, new byte[] { 1 }, ManifestResourceAttributes.Public));
            module.Resources.Add(new EmbeddedResource(name, new byte[] { 2 }, ManifestResourceAttributes.Public));
        }

        var failures = PatchVerifier.Verify(module, entry);
        Assert.Contains(failures, f => f.Contains("pairing drift"));
    }

    [Fact]
    public void TestOverrideIsActive()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("PAICOM_PATCHER_ALLOW_UNKNOWN"));
    }

    [Fact]
    public void FixturePassesStructuralCheck()
    {
        using var temp = new TempDirectory();
        var path = FixtureAssemblyBuilder.Build(
            "public static class FxDiag { public static void Main() { } }",
            "fx-diag",
            temp.Path);
        var bytes = File.ReadAllBytes(path);

        var ok = VersionGate.LooksLikeManagedPaiCom(bytes, out var reason, minimumBytes: 0);

        Assert.True(ok, reason);
    }
}
