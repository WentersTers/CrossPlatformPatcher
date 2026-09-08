using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Unit tests for LauncherGenerator.
/// Verifies that launcher scripts are correctly generated for Windows patcher,
/// including batch files, shell scripts, and setup launchers.
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public sealed class LauncherGeneratorTests
{
    [Fact]
    public void Generates_Windows_Batch_Launcher_With_Expected_Exe_Target()
    {
        // Arrange
        using var temp = new TempDirectory();
        var exeFileName = "PAIcom.exe";
        var outputDir = temp.Path;

        // Act
        LauncherGenerator.WriteAll(outputDir, exeFileName, MigrationMode.Stable, "AMD64", false);

        // Assert
        var batPath = Path.Combine(outputDir, "run.bat");
        Assert.True(File.Exists(batPath), "run.bat should be created");

        var batContent = File.ReadAllText(batPath, System.Text.Encoding.ASCII);
        
        // Verify the batch file contains the expected executable target
        Assert.Contains($"start \"\" \"%~dp0{exeFileName}\"", batContent, StringComparison.Ordinal);
        
        // Verify Windows-specific environment variables are set
        Assert.Contains("PAICOM_RUNTIME_HOST_OS=windows", batContent, StringComparison.Ordinal);
        
        // Verify OpenWakeWord environment variables are present
        Assert.Contains("PAICOM_OWW_THRESHOLD=0.7", batContent, StringComparison.Ordinal);
        Assert.Contains("PAICOM_OWW_LOCK_MS=3000", batContent, StringComparison.Ordinal);
        Assert.Contains("PAICOM_OWW_AUDIO_CHUNK_SIZE=1024", batContent, StringComparison.Ordinal);
        
        // Verify Vosk model path configuration
        Assert.Contains("MODELS_DIR=%~dp0models", batContent, StringComparison.Ordinal);
        Assert.Contains("PAICOM_VOSK_MODEL_PATH=%MODELS_DIR%", batContent, StringComparison.Ordinal);
        
        // Verify Windows line endings (CRLF) are used for .bat compatibility
        Assert.Contains("\r\n", batContent);
    }

    [Fact]
    public void Preserves_MacOS_Shell_Launcher_File()
    {
        // Arrange
        using var temp = new TempDirectory();
        var exeFileName = "PAIcom.exe";
        var outputDir = temp.Path;

        // Act
        LauncherGenerator.WriteAll(outputDir, exeFileName, MigrationMode.Stable, "AMD64", false);

        // Assert
        var runShPath = Path.Combine(outputDir, "run.sh");
        Assert.True(File.Exists(runShPath), "run.sh should be created");

        var runShContent = File.ReadAllText(runShPath);
        
        // Verify the shell script is executable and has proper shebang
        Assert.StartsWith("#!/usr/bin/env sh", runShContent, StringComparison.Ordinal);
        
        // Verify the executable placeholder is replaced with the actual exe name
        Assert.Contains($"EXE=\"$SCRIPT_DIR/{exeFileName}\"", runShContent, StringComparison.Ordinal);
        
        // Verify migration mode is embedded
        Assert.Contains("DEFAULT_MIGRATION_MODE=\"stable\"", runShContent, StringComparison.Ordinal);
        
        // Verify target PE machine is embedded
        Assert.Contains("TARGET_PE_MACHINE=\"AMD64\"", runShContent, StringComparison.Ordinal);
        
        // Verify probe CorFlags flag is embedded (replaced with 0 or 1)
        Assert.Contains("PROBE_CORFLAGS_APPLIED=\"0\"", runShContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Preserves_MacOS_Finder_Launcher_File()
    {
        // Arrange
        using var temp = new TempDirectory();
        var exeFileName = "PAIcom.exe";
        var outputDir = temp.Path;

        // Act
        LauncherGenerator.WriteAll(outputDir, exeFileName, MigrationMode.Stable, "AMD64", false);

        // Assert
        var launchCommandPath = Path.Combine(outputDir, "launch.command");
        Assert.True(File.Exists(launchCommandPath), "launch.command should be created");

        var launchCommandContent = File.ReadAllText(launchCommandPath);
        
        // Verify the Finder launcher has proper shebang
        Assert.StartsWith("#!/usr/bin/env sh", launchCommandContent, StringComparison.Ordinal);
        
        // Verify it wraps run.sh
        Assert.Contains("exec sh \"$SCRIPT_DIR/run.sh\"", launchCommandContent, StringComparison.Ordinal);
        
        // Verify it includes comment about Mac Finder double-click
        Assert.Contains("Mac Finder double-click launcher", launchCommandContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Writes_Setup_Launcher_Hook_Used_By_Installer_Flow()
    {
        // Arrange
        using var temp = new TempDirectory();
        var exeFileName = "PAIcom.exe";
        var outputDir = temp.Path;

        // Act
        LauncherGenerator.WriteAll(outputDir, exeFileName, MigrationMode.Stable, "AMD64", false);

        // Assert - Windows setup launcher hook in run.bat
        var batPath = Path.Combine(outputDir, "run.bat");
        var batContent = File.ReadAllText(batPath, System.Text.Encoding.ASCII);
        
        // Verify --setup flag handling
        Assert.Contains("if /I \"%~1\"==\"--setup\"", batContent, StringComparison.Ordinal);
        Assert.Contains("SetupWizard.exe", batContent, StringComparison.Ordinal);
        Assert.Contains("start \"\" \"%~dp0SetupWizard.exe\"", batContent, StringComparison.Ordinal);
        
        // Assert - Shell setup wizard launcher
        var setupShPath = Path.Combine(outputDir, "setup-wizard.sh");
        Assert.True(File.Exists(setupShPath), "setup-wizard.sh should be created");

        var setupShContent = File.ReadAllText(setupShPath);
        
        // Verify setup wizard script has proper shebang
        Assert.StartsWith("#!/usr/bin/env sh", setupShContent, StringComparison.Ordinal);
        
        // Verify it includes GUI setup wizard logic
        Assert.Contains("GUI setup wizard", setupShContent, StringComparison.Ordinal);
        
        // Assert - Mac Finder setup launcher
        var setupCommandPath = Path.Combine(outputDir, "setup.command");
        Assert.True(File.Exists(setupCommandPath), "setup.command should be created");

        var setupCommandContent = File.ReadAllText(setupCommandPath);
        
        // Verify setup.command wraps setup-wizard.sh
        Assert.Contains("sh \"$SCRIPT_DIR/setup-wizard.sh\"", setupCommandContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_Launcher_Content_Aligned_With_Patched_Exe_Name()
    {
        // Arrange
        using var temp = new TempDirectory();
        var exeFileName = "MyGame.exe";
        var outputDir = temp.Path;

        // Act
        LauncherGenerator.WriteAll(outputDir, exeFileName, MigrationMode.Probe, "x86", true);

        // Assert - Windows batch launcher
        var batPath = Path.Combine(outputDir, "run.bat");
        var batContent = File.ReadAllText(batPath, System.Text.Encoding.ASCII);
        Assert.Contains($"start \"\" \"%~dp0{exeFileName}\"", batContent, StringComparison.Ordinal);
        
        // Assert - Shell launcher
        var runShPath = Path.Combine(outputDir, "run.sh");
        var runShContent = File.ReadAllText(runShPath);
        Assert.Contains($"EXE=\"$SCRIPT_DIR/{exeFileName}\"", runShContent, StringComparison.Ordinal);
        
        // Assert - Migration mode is correctly embedded
        Assert.Contains("DEFAULT_MIGRATION_MODE=\"probe\"", runShContent, StringComparison.Ordinal);
        
        // Assert - Target PE machine is correctly embedded
        Assert.Contains("TARGET_PE_MACHINE=\"x86\"", runShContent, StringComparison.Ordinal);
        
        // Assert - Probe CorFlags flag is correctly embedded (replaced with 0 or 1)
        Assert.Contains("PROBE_CORFLAGS_APPLIED=\"1\"", runShContent, StringComparison.Ordinal);
        
        // Assert - Setup documentation files exist
        var setupLinuxPath = Path.Combine(outputDir, "SETUP_LINUX.md");
        Assert.True(File.Exists(setupLinuxPath), "SETUP_LINUX.md should be created");
        
        var setupMacPath = Path.Combine(outputDir, "SETUP_MAC.md");
        Assert.True(File.Exists(setupMacPath), "SETUP_MAC.md should be created");
    }

    [Fact]
    public void Generates_All_Expected_Launcher_Files()
    {
        // Arrange
        using var temp = new TempDirectory();
        var exeFileName = "PAIcom.exe";
        var outputDir = temp.Path;

        // Act
        LauncherGenerator.WriteAll(outputDir, exeFileName, MigrationMode.Full, "AMD64", false);

        // Assert - Verify all expected launcher files are created
        var expectedFiles = new[]
        {
            "run.bat",           // Windows batch launcher
            "run.sh",            // Linux/Mac shell launcher
            "launch.command",    // Mac Finder double-click wrapper
            "setup-wizard.sh",   // GUI-first setup wizard launcher
            "setup.command",     // Mac Finder setup wrapper
            "SETUP_LINUX.md",    // Wine + audio setup instructions for Linux
            "SETUP_MAC.md"       // Wine + audio setup instructions for macOS
        };

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(outputDir, expectedFile);
            Assert.True(File.Exists(filePath), $"{expectedFile} should be created");
        }
    }

    [Fact]
    public void Windows_Batch_Launcher_Uses_ASCII_Encoding()
    {
        // Arrange
        using var temp = new TempDirectory();
        var exeFileName = "PAIcom.exe";
        var outputDir = temp.Path;

        // Act
        LauncherGenerator.WriteAll(outputDir, exeFileName, MigrationMode.Stable, "AMD64", false);

        // Assert
        var batPath = Path.Combine(outputDir, "run.bat");
        var batContent = File.ReadAllText(batPath, System.Text.Encoding.ASCII);
        
        // Verify the content can be read with ASCII encoding without loss
        Assert.NotNull(batContent);
        Assert.NotEmpty(batContent);
        
        // Verify key batch file commands are present
        Assert.Contains("@echo off", batContent, StringComparison.Ordinal);
        Assert.Contains("start \"\"", batContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_Launchers_Use_UTF8_Without_BOM()
    {
        // Arrange
        using var temp = new TempDirectory();
        var exeFileName = "PAIcom.exe";
        var outputDir = temp.Path;

        // Act
        LauncherGenerator.WriteAll(outputDir, exeFileName, MigrationMode.Stable, "AMD64", false);

        // Assert - run.sh
        var runShPath = Path.Combine(outputDir, "run.sh");
        var runShBytes = File.ReadAllBytes(runShPath);
        
        // Verify no BOM (UTF-8 BOM is EF BB BF)
        Assert.False(runShBytes.Length >= 3 && runShBytes[0] == 0xEF && runShBytes[1] == 0xBB && runShBytes[2] == 0xBF,
            "run.sh should not have UTF-8 BOM");
        
        // Assert - launch.command
        var launchCommandPath = Path.Combine(outputDir, "launch.command");
        var launchCommandBytes = File.ReadAllBytes(launchCommandPath);
        
        Assert.False(launchCommandBytes.Length >= 3 && launchCommandBytes[0] == 0xEF && launchCommandBytes[1] == 0xBB && launchCommandBytes[2] == 0xBF,
            "launch.command should not have UTF-8 BOM");
        
        // Assert - setup-wizard.sh
        var setupShPath = Path.Combine(outputDir, "setup-wizard.sh");
        var setupShBytes = File.ReadAllBytes(setupShPath);
        
        Assert.False(setupShBytes.Length >= 3 && setupShBytes[0] == 0xEF && setupShBytes[1] == 0xBB && setupShBytes[2] == 0xBF,
            "setup-wizard.sh should not have UTF-8 BOM");
    }

    [Fact]
    public void Windows_Batch_Launcher_Includes_File_Command_Input_Support()
    {
        // Arrange
        using var temp = new TempDirectory();
        var exeFileName = "PAIcom.exe";
        var outputDir = temp.Path;

        // Act
        LauncherGenerator.WriteAll(outputDir, exeFileName, MigrationMode.Stable, "AMD64", false);

        // Assert
        var batPath = Path.Combine(outputDir, "run.bat");
        var batContent = File.ReadAllText(batPath, System.Text.Encoding.ASCII);
        
        // Verify file command input environment variable check
        Assert.Contains("PAICOM_FILE_COMMAND_INPUT", batContent, StringComparison.Ordinal);
        Assert.Contains("PAICOM_FILE_COMMAND_INPUT_PATH", batContent, StringComparison.Ordinal);
        Assert.Contains("input-command.txt", batContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Generates_Launchers_With_Different_Migration_Modes()
    {
        // Arrange
        using var temp = new TempDirectory();
        var exeFileName = "PAIcom.exe";
        var outputDir = temp.Path;

        // Act & Assert - Stable mode
        var stableDir = Path.Combine(temp.Path, "stable");
        LauncherGenerator.WriteAll(stableDir, exeFileName, MigrationMode.Stable, "AMD64", false);
        var stableRunSh = File.ReadAllText(Path.Combine(stableDir, "run.sh"));
        Assert.Contains("DEFAULT_MIGRATION_MODE=\"stable\"", stableRunSh, StringComparison.Ordinal);

        // Act & Assert - Probe mode
        var probeDir = Path.Combine(temp.Path, "probe");
        LauncherGenerator.WriteAll(probeDir, exeFileName, MigrationMode.Probe, "AMD64", true);
        var probeRunSh = File.ReadAllText(Path.Combine(probeDir, "run.sh"));
        Assert.Contains("DEFAULT_MIGRATION_MODE=\"probe\"", probeRunSh, StringComparison.Ordinal);
        Assert.Contains("PROBE_CORFLAGS_APPLIED=\"1\"", probeRunSh, StringComparison.Ordinal);

        // Act & Assert - Full mode
        var fullDir = Path.Combine(temp.Path, "full");
        LauncherGenerator.WriteAll(fullDir, exeFileName, MigrationMode.Full, "AMD64", false);
        var fullRunSh = File.ReadAllText(Path.Combine(fullDir, "run.sh"));
        Assert.Contains("DEFAULT_MIGRATION_MODE=\"full\"", fullRunSh, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_Launchers_Use_LF_Only_Batch_Keeps_CRLF()
    {
        // Session-6: raw-string literals inherit this source file's CRLF and
        // broke run.sh on Linux (`set: -D: invalid option`, died pre-log).
        using var temp = new TempDirectory();
        LauncherGenerator.WriteAll(temp.Path, "PAIcom.exe", MigrationMode.Full, "AMD64", false);

        foreach (var name in new[] { "run.sh", "setup-wizard.sh", "setup.command", "launch.command" })
        {
            var text = File.ReadAllText(Path.Combine(temp.Path, name));
            Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        }

        var bat = File.ReadAllText(Path.Combine(temp.Path, "run.bat"), System.Text.Encoding.ASCII);
        Assert.Contains("\r\n", bat, StringComparison.Ordinal);
    }
}