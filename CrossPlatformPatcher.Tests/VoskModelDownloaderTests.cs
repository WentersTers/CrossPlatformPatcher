using System.IO.Compression;
using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

[Collection(SequentialTestCollection.CollectionName)]
public sealed class VoskModelDownloaderTests
{

    [Fact]
    public void ResolveDownloadUrl_Defaults_To_Measured_Model()
    {
        var prior = Environment.GetEnvironmentVariable(VoskModelDownloader.UrlOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(VoskModelDownloader.UrlOverrideVariable, null);
            Assert.Equal(VoskModelDownloader.DefaultModelUrl, VoskModelDownloader.ResolveDownloadUrl());
            Assert.Contains("vosk-model-small-en-us-0.15.zip", VoskModelDownloader.DefaultModelUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VoskModelDownloader.UrlOverrideVariable, prior);
        }
    }

    [Fact]
    public void ResolveDownloadUrl_Empty_Means_Disabled_And_Garbage_Rejected()
    {
        var prior = Environment.GetEnvironmentVariable(VoskModelDownloader.UrlOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(VoskModelDownloader.UrlOverrideVariable, "   ");
            Assert.Null(VoskModelDownloader.ResolveDownloadUrl());
            Environment.SetEnvironmentVariable(VoskModelDownloader.UrlOverrideVariable, "not-a-url");
            Assert.Null(VoskModelDownloader.ResolveDownloadUrl());
            Environment.SetEnvironmentVariable(VoskModelDownloader.UrlOverrideVariable,
                "https://example.com/custom-model.zip");
            Assert.Equal("https://example.com/custom-model.zip", VoskModelDownloader.ResolveDownloadUrl());
        }
        finally
        {
            Environment.SetEnvironmentVariable(VoskModelDownloader.UrlOverrideVariable, prior);
        }
    }

    [Fact]
    public void HomeModelsRoot_Ends_With_Paicom_Models()
    {
        var root = VoskModelDownloader.HomeModelsRoot();
        Assert.EndsWith(Path.Combine(".paicom", "models"), root, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("vosk-model-small-en-us-0.15/am/final.mdl")]
    [InlineData("am/final.mdl")]
    public void SanitizeEntryPath_Accepts_Nested_Files(string entry)
    {
        using var temp = new TempDirectory();
        var safe = VoskModelDownloader.SanitizeEntryPath(temp.Path, entry);
        Assert.NotNull(safe);
        Assert.StartsWith(temp.Path, safe, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("model/../../evil.dll")]
    [InlineData("/absolute/evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("")]
    [InlineData(null)]
    public void SanitizeEntryPath_Rejects_Traversal_And_Absolute(string? entry)
    {
        using var temp = new TempDirectory();
        Assert.Null(VoskModelDownloader.SanitizeEntryPath(temp.Path, entry));
    }

    [Fact]
    public void ExtractArchive_Skips_Evil_Entry_And_Finds_Model()
    {
        using var temp = new TempDirectory();
        var zipPath = Path.Combine(temp.Path, "model.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[]
                     {
                         "vosk-model-small-en-us-0.15/am/final.mdl",
                         "vosk-model-small-en-us-0.15/conf/model.conf",
                         "vosk-model-small-en-us-0.15/graph/HCLG.fst",
                         "../evil.dll",
                     })
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open());
                w.Write("x");
            }
        }

        var destRoot = Path.Combine(temp.Path, "models");
        Directory.CreateDirectory(destRoot);
        var skipped = new List<string>();
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            VoskModelDownloader.ExtractArchive(archive, destRoot, m => skipped.Add(m));
        }

        Assert.False(File.Exists(Path.Combine(temp.Path, "evil.dll")));
        Assert.True(Directory.Exists(Path.Combine(destRoot, "vosk-model-small-en-us-0.15", "am")));
        Assert.Contains(skipped, m => m.Contains("../evil.dll"));
    }

    [Fact]
    public void Staged_X86_Natives_Are_I386_And_X64_Are_Amd64()
    {
        var dir = FindNativeLibrariesDir();
        Assert.True(Directory.Exists(dir), "Core/NativeLibraries must exist beside the checkout.");
        var expectations = new Dictionary<string, ushort>
        {
            ["vosk-win-x86.dll"] = 0x014c,
            ["vosk-win-gcc-x86.dll"] = 0x014c,
            ["vosk-win-stdc-x86.dll"] = 0x014c,
            ["vosk-win-pthread-x86.dll"] = 0x014c,
            ["vosk-win-x64.dll"] = 0x8664,
            ["vosk-win-gcc.dll"] = 0x8664,
            ["vosk-win-stdc.dll"] = 0x8664,
            ["vosk-win-pthread.dll"] = 0x8664,
        };
        foreach (var (file, expected) in expectations)
        {
            var path = Path.Combine(dir, file);
            Assert.True(File.Exists(path), $"Missing staged native: {file}");
            var bytes = File.ReadAllBytes(path);
            var e_lfanew = BitConverter.ToInt32(bytes, 0x3C);
            var machine = BitConverter.ToUInt16(bytes, e_lfanew + 4);
            Assert.Equal(expected, machine);
        }
    }

    private static string FindNativeLibrariesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Core", "NativeLibraries");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return "<missing>";
    }
}
