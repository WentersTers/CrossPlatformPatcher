using System.Reflection;
using System.Linq;
using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

[Collection(SequentialTestCollection.CollectionName)]
public sealed class ProgramCliTests
{
    [Fact]
    public void Help_And_Version_Are_Reachable_From_Terminal_Entry_Point()
    {
        var help = ProgramInvoker.Invoke(["--help"]);
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("Usage", help.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CrossPlatformPatcher", help.StdOut, StringComparison.OrdinalIgnoreCase);

        var version = ProgramInvoker.Invoke(["--version"]);
        Assert.Equal(0, version.ExitCode);
        Assert.Contains("CrossPlatformPatcher", version.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"\d+\.\d+(?:\.\d+)?", version.StdOut);
    }

    [Fact]
    public void MissingInput_File_Returns_Error()
    {
        var temp = new TempDirectory();
        var missingInput = System.IO.Path.Combine(temp.Path, "missing.dll");

        var result = ProgramInvoker.Invoke([missingInput, "--out", System.IO.Path.Combine(temp.Path, "out.dll")]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("File not found", result.StdErr);
    }

    [Fact]
    public void Analyze_Mode_Prints_Report_And_Writes_Nothing()
    {
        using var temp = new TempDirectory();
        var input = BuildNoOpFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "analyzed.dll");

        var result = ProgramInvoker.Invoke([input, "--analyze", "--out", output]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("TOP METHODS BY INSTRUCTION COUNT", result.StdOut);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void DryRun_Does_Not_Write_Output_Or_Backup()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "dryrun.dll");

        var result = ProgramInvoker.Invoke([input, "--dry-run", "--backup", "--out", output, "--oww-lock-ms", "2500"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[DRY-RUN]", result.StdOut);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(input + ".bak"));
    }

    [Fact]
    public void DryRun_Reports_Patch_Points_Without_Writing_Output()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "dryrun-report.dll");

        var result = ProgramInvoker.Invoke([input, "--dry-run", "--out", output]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[DRY-RUN]", result.StdOut);
        Assert.Contains("Patch points found:", result.StdOut);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Backup_Mode_Writes_Backup_And_Leaves_Input_Intact()
    {
        using var temp = new TempDirectory();
        var input = BuildNoOpFixture(temp.Path);
        var originalBytes = File.ReadAllBytes(input);
        var output = System.IO.Path.Combine(temp.Path, "backup.dll");

        var result = ProgramInvoker.Invoke([input, "--backup", "--out", output]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(input + ".bak"));
        Assert.True(File.Exists(output));
        Assert.Equal(originalBytes, File.ReadAllBytes(input + ".bak"));
    }

    [Fact]
    public void Unknown_Arguments_Are_Warned_About_But_Do_Not_Block_Patching()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "unknown.dll");

        var result = ProgramInvoker.Invoke([input, "--bogus-option", "--out", output, "--dry-run"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[WARN] Unknown argument: --bogus-option", result.StdErr);
    }

    [Fact]
    public void Unknown_Flags_Do_Not_Break_Patching()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "unknown-flags.dll");

        var result = ProgramInvoker.Invoke([input, "--unknown-flag-1", "--unknown-flag-2", "--out", output, "--dry-run"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[WARN] Unknown argument: --unknown-flag-1", result.StdErr);
        Assert.Contains("[WARN] Unknown argument: --unknown-flag-2", result.StdErr);
        Assert.Contains("Patch points found:", result.StdOut);
    }

    [Fact]
    public void Backup_Mode_Preserves_Original_Executable()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var originalBytes = File.ReadAllBytes(input);
        var output = System.IO.Path.Combine(temp.Path, "backup-preserve.dll");
        var backupPath = input + ".bak";

        var result = ProgramInvoker.Invoke([input, "--backup", "--out", output]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(backupPath), "Backup file should be created");
        Assert.True(File.Exists(output), "Output file should be created");
        Assert.True(File.Exists(input), "Original file should still exist");
        
        var backupBytes = File.ReadAllBytes(backupPath);
        Assert.True(originalBytes.SequenceEqual(backupBytes), "Backup should be identical to original");
    }

    [Fact]
    public void Migration_Mode_Stable_Is_Respected_In_Output()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "migration-stable.dll");

        var result = ProgramInvoker.Invoke([input, "--migration-mode", "stable", "--out", output, "--dry-run"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Migration Mode   : stable", result.StdOut);
    }

    [Fact]
    public void Migration_Mode_Probe_Is_Respected_In_Output()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "migration-probe.dll");

        var result = ProgramInvoker.Invoke([input, "--migration-mode", "probe", "--out", output, "--dry-run"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Migration Mode   : probe", result.StdOut);
    }

    [Fact]
    public void Migration_Mode_Full_Is_Respected_In_Output()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "migration-full.dll");

        var result = ProgramInvoker.Invoke([input, "--migration-mode", "full", "--out", output, "--dry-run"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Migration Mode   : full", result.StdOut);
    }

    [Fact]
    public void Invalid_Migration_Mode_Falls_Back_To_Stable()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "migration-invalid.dll");

        var result = ProgramInvoker.Invoke([input, "--migration-mode", "invalid", "--out", output, "--dry-run"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[WARN] Invalid migration mode: invalid. Using stable mode.", result.StdErr);
        Assert.Contains("Migration Mode   : stable", result.StdOut);
    }

    [Fact]
    public void Normal_Patch_Flow_Does_Not_Require_Windows_Speech_Program()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "no-speech-required.dll");

        var result = ProgramInvoker.Invoke([input, "--out", output, "--dry-run"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Patch points found:", result.StdOut);
        Assert.DoesNotContain("System.Speech", result.StdErr);
        Assert.DoesNotContain("speech program", result.StdErr);
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
