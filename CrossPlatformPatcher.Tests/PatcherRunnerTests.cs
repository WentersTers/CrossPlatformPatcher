using System.Security.Cryptography;
using CrossPlatformPatcher.Cli;
using Xunit;

namespace CrossPlatformPatcher.Tests;

[Collection(SequentialTestCollection.CollectionName)]
public sealed class PatcherRunnerTests
{
    [Fact]
    public void DryRun_Returns_Skipped_Outcome_With_No_Output_Or_Backup()
    {
        using var temp = new TempDirectory();
        var input = BuildNoOpFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "dryrun.dll");

        var options = CliOptions.Parse([input, "--dry-run", "--out", output]);
        var outcome = PatcherRunner.Run(options, _ => { });

        Assert.True(outcome.SkippedDueToDryRun);
        Assert.Equal(0, outcome.ExitCode);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(input + ".bak"));
    }

    [Fact]
    public void MissingInput_Returns_Error_With_FileNotFound_ExitCode()
    {
        using var temp = new TempDirectory();
        var missingInput = System.IO.Path.Combine(temp.Path, "missing.dll");
        var output = System.IO.Path.Combine(temp.Path, "out.dll");

        var options = CliOptions.Parse([missingInput, "--out", output]);
        var outcome = PatcherRunner.Run(options, _ => { });

        Assert.NotEmpty(outcome.Errors);
        Assert.Contains("File not found", outcome.Errors[0], StringComparison.Ordinal);
        Assert.Equal(1, outcome.ExitCode);
    }

    [Fact]
    public void Backup_On_Write_Overwrites_PreExisting_Backup_With_Original_Bytes()
    {
        using var temp = new TempDirectory();
        var input = BuildNoOpFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "backup.dll");
        var backupPath = input + ".bak";
        var originalBytes = File.ReadAllBytes(input);

        // A pre-existing (stale) backup should be overwritten by the run.
        File.WriteAllBytes(backupPath, [0xDE, 0xAD, 0xBE, 0xEF]);

        var options = CliOptions.Parse([input, "--backup", "--out", output]);
        var outcome = PatcherRunner.Run(options, _ => { });

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(File.Exists(backupPath), "Backup file should exist after run.");
        Assert.Equal(originalBytes, File.ReadAllBytes(backupPath));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Sha256_Matches_Independently_Computed_Hash_Of_Input()
    {
        using var temp = new TempDirectory();
        var input = BuildNoOpFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "hash.dll");

        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));

        var options = CliOptions.Parse([input, "--dry-run", "--out", output]);
        var outcome = PatcherRunner.Run(options, _ => { });

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(expected, outcome.Sha256);
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
}
