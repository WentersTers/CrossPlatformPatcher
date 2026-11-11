using System.Security.Cryptography;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

public class MaterializationResourceFixerTests
{
    private const string SevenStem = "7nbx}a5?U:!^=d#]\"~1pIQ;X"; // 24 chars
    private const string LinkedEntry = "7nbx}a5?U:!^=d#\\]\"~1pIQ;X\\&.resources";

    // A fixture with real patch points (Process.Start on an audio handler) so the
    // writer takes the IL path (not the byte-for-byte short circuit) and therefore
    // persists added resources.
    private const string PatchableSource = """
        using System;
        using System.Diagnostics;

        public static class FixturePatchable
        {
            public static void Main()
            {
                Console.WriteLine("fixture-patchable");
            }

            public static void AudioChunkHandler(byte[] audioChunk)
            {
                Process.Start("fixture-child.exe");
                Console.WriteLine(audioChunk.Length);
            }
        }
        """;

    private static string BuildPatchableFixture(string directory)
        => FixtureAssemblyBuilder.Build(PatchableSource, $"fixture-materialize-{Guid.NewGuid():N}", directory);

    private static byte[] ReadData(EmbeddedResource r)
    {
        using var s = r.CreateReader().AsStream();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string BuildSevenNbxInput(string directory)
    {
        var built = BuildPatchableFixture(directory);
        byte[] bytes = File.ReadAllBytes(built);
        using var module = ModuleDefMD.Load(bytes);
        var asmRef = new AssemblyRefUser("VWcBYJrsSukNCehHXpGQbXaXAvkQ", new Version(0, 0, 0, 0));
        module.Resources.Add(new AssemblyLinkedResource(
            LinkedEntry, asmRef, ManifestResourceAttributes.Public));
        var input = System.IO.Path.Combine(directory, $"seven-{Guid.NewGuid():N}.exe");
        module.Write(input, new ModuleWriterOptions(module));
        return input;
    }

    // ── Out of scope: no 7nbx linked entry → skipped, nothing added ──────
    [Fact]
    public void SkipsWhenNoSevenNbxLinkedEntry()
    {
        using var temp = new TempDirectory();
        using var module = ModuleDefMD.Load(BuildPatchableFixture(temp.Path));

        int added = MaterializationResourceFixer.EnsureMaterializationResources(module);

        Assert.Equal(0, added);
        Assert.DoesNotContain(module.Resources,
            r => r.Name.String == MaterializationResourceFixer.DialogPinnedName);
        Assert.DoesNotContain(module.Resources,
            r => r.Name.String == MaterializationResourceFixer.TypeBasedName);
    }

    // ── In scope: 7nbx linked entry present → both ADDs applied ─────────
    [Fact]
    public void AddsBothResourcesWhenSevenNbxEntryPresent()
    {
        using var temp = new TempDirectory();
        using var module = ModuleDefMD.Load(BuildSevenNbxInput(temp.Path));

        int added = MaterializationResourceFixer.EnsureMaterializationResources(module);

        Assert.Equal(2, added);
        var dialog = module.Resources.OfType<EmbeddedResource>()
            .Single(r => r.Name.String == MaterializationResourceFixer.DialogPinnedName);
        var typeBased = module.Resources.OfType<EmbeddedResource>()
            .Single(r => r.Name.String == MaterializationResourceFixer.TypeBasedName);
        Assert.Equal(180, ReadData(dialog).Length);
        Assert.Equal(180, ReadData(typeBased).Length);
        Assert.Equal(ReadData(dialog), ReadData(typeBased));
    }

    // ── Idempotence guard: re-run refuses on drift ─────────────────────
    [Fact]
    public void RefusesWhenResourcesAlreadyPresent()
    {
        using var temp = new TempDirectory();
        using var module = ModuleDefMD.Load(BuildSevenNbxInput(temp.Path));

        MaterializationResourceFixer.EnsureMaterializationResources(module);

        var ex = Assert.Throws<InvalidOperationException>(
            () => MaterializationResourceFixer.EnsureMaterializationResources(module));
        Assert.Contains("drift", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── The blob is a valid .resources container with the pinned hash ──
    [Fact]
    public void NeutralBlobIsValidResourcesContainerWithPinnedHash()
    {
        Assert.Equal(
            "e13ed2c59366d0eea74863fd71a81f0cb977cce1edfde304fc538690a4f6ac89",
            MaterializationResourceFixer.NeutralBlobSha256);

        // BEEFCACE magic (.NET resources) and a non-trivial header.
        var blob = MaterializationResourceFixer.NeutralBlobBytesForTests;
        Assert.Equal(180, blob.Length);
        Assert.Equal(0xCE, blob[0]);
        Assert.Equal(0xCA, blob[1]);
        Assert.Equal(0xEF, blob[2]);
        Assert.Equal(0xBE, blob[3]);
    }

    // ── Names match the pinned dialog/metadata-type pairing ────────────
    [Fact]
    public void ResourceNamesMatchPinnedPairing()
    {
        Assert.Equal("7nbx}a5?U:!^=d#]\"~1pIQ;X.resources",
            MaterializationResourceFixer.DialogPinnedName);
        Assert.Equal("7nbx}a5?U:!^=d#]\"~1pIQ;X&.resources",
            MaterializationResourceFixer.TypeBasedName);
    }

    // ── End-to-end: official Patch() writes both ADDs into the output ──
    [Fact]
    public void PatchPipelineWritesBothResourcesIntoOutput()
    {
        using var temp = new TempDirectory();
        var input = BuildSevenNbxInput(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "patched-seven.exe");

        var patcher = new AssemblyPatcher(verbose: false);
        var result = patcher.Patch(input, output, dryRun: false);

        Assert.True(File.Exists(output));
        Assert.Empty(result.Errors);
        Assert.True(result.PatchPointsApplied > 0,
            "fixture must take the IL write path for added resources to persist");

        using var patched = ModuleDefMD.Load(output);
        var dialog = patched.Resources.OfType<EmbeddedResource>()
            .SingleOrDefault(r => r.Name.String == MaterializationResourceFixer.DialogPinnedName);
        var typeBased = patched.Resources.OfType<EmbeddedResource>()
            .SingleOrDefault(r => r.Name.String == MaterializationResourceFixer.TypeBasedName);

        Assert.NotNull(dialog);
        Assert.NotNull(typeBased);
        Assert.Equal(180, ReadData(dialog!).Length);
        Assert.Equal(180, ReadData(typeBased!).Length);
    }
}
