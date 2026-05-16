using System.Runtime.InteropServices;
using CrossPlatformPatcher.Core;
using dnlib.DotNet;
using Xunit;

namespace CrossPlatformPatcher.Tests;

[Collection(SequentialTestCollection.CollectionName)]
public sealed class AssemblyPatcherIntegrationTests
{
    [Fact]
    public void NoOp_Assembly_Is_Written_Byte_For_Byte_And_Launchers_Are_Generated()
    {
        using var temp = new TempDirectory();
        var input = BuildNoOpFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "patched-noop.exe");
        var inputBytes = File.ReadAllBytes(input);

        var patcher = new AssemblyPatcher(verbose: false);
        var result = patcher.Patch(input, output, dryRun: false);

        Assert.True(File.Exists(output));
        Assert.Equal(inputBytes, File.ReadAllBytes(output));
        Assert.Equal(0, result.PatchPointsApplied);

        AssertGeneratedLaunchers(temp.Path, Path.GetFileName(output));
        ArtifactAssertions.AssertNoSourceArtifacts(temp.Path);
    }

    [Fact]
    public void Patchable_Assembly_Produces_Modified_Output_And_No_Source_File_Leakage()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "patched-fixture.exe");
        var inputBytes = File.ReadAllBytes(input);

        var patcher = new AssemblyPatcher(verbose: false);
        var result = patcher.Patch(input, output, dryRun: false);

        Assert.True(File.Exists(output));
        Assert.NotEqual(inputBytes, File.ReadAllBytes(output));
        Assert.True(result.PatchPointsApplied > 0);

        var module = ModuleDefMD.Load(output);
        Assert.NotNull(module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW"));

        AssertGeneratedLaunchers(temp.Path, Path.GetFileName(output));
        ArtifactAssertions.AssertNoSourceArtifacts(temp.Path);

        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(temp.Path, "run.sh"), Path.GetFileName(output), "launcher-runtime.log");
        ArtifactAssertions.AssertFileContains(
            System.IO.Path.Combine(temp.Path, "run.bat"),
            Path.GetFileName(output),
            "PAICOM_RUNTIME_HOST_OS",
            "PAICOM_VOSK_MODEL_PATH",
            "PAICOM_FILE_COMMAND_INPUT_PATH",
            "SetupWizard.exe");
        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(temp.Path, "setup-wizard.sh"), Path.GetFileName(output), "run.sh");
        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(temp.Path, "launch.command"), "run.sh");
        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(temp.Path, "setup.command"), "setup-wizard.sh");
    }

    [Fact]
    public void Generated_Shell_Launchers_Are_Executable_On_Unix()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "patched-unix.exe");

        var patcher = new AssemblyPatcher(verbose: false);
        patcher.Patch(input, output, dryRun: false);

        if (OperatingSystem.IsWindows())
            return;

        foreach (var script in new[] { "run.sh", "launch.command", "setup-wizard.sh", "setup.command" })
        {
            var path = System.IO.Path.Combine(temp.Path, script);
            Assert.True(File.Exists(path));

            var mode = File.GetUnixFileMode(path);
            Assert.True((mode & UnixFileMode.UserExecute) != 0, $"{script} should be executable.");
        }
    }

    private static void AssertGeneratedLaunchers(string directory, string exeName)
    {
        var expected = new[]
        {
            "run.sh",
            "launch.command",
            "setup-wizard.sh",
            "setup.command",
            "run.bat",
            "SETUP_LINUX.md",
            "SETUP_MAC.md"
        };

        foreach (var name in expected)
            Assert.True(File.Exists(System.IO.Path.Combine(directory, name)), $"Missing generated file: {name}");

        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(directory, "run.sh"), exeName, "launcher-runtime.log");
        ArtifactAssertions.AssertFileContains(
            System.IO.Path.Combine(directory, "run.bat"),
            exeName,
            "PAICOM_RUNTIME_HOST_OS",
            "PAICOM_VOSK_MODEL_PATH",
            "PAICOM_FILE_COMMAND_INPUT_PATH",
            "SetupWizard.exe");
        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(directory, "launch.command"), "run.sh");
        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(directory, "setup.command"), "setup-wizard.sh");
    }

    private static string BuildNoOpFixture(string directory)
    {
        var source = """
            using System;

            public static class FixtureNoOp
            {
                public static void Main()
                {
                    Console.WriteLine("fixture-no-op");
                }

                public static int Sum(int left, int right)
                {
                    return left + right;
                }
            }
            """;

        return FixtureAssemblyBuilder.Build(source, $"fixture-no-op-{Guid.NewGuid():N}", directory);
    }

    private static string BuildPatchableFixture(string directory)
    {
        var source = """
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

        return FixtureAssemblyBuilder.Build(source, $"fixture-patchable-{Guid.NewGuid():N}", directory);
    }
}
