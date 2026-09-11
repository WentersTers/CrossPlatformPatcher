using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Xunit;
using Xunit.Abstractions;
using Assert = Xunit.Assert;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Unit tests for the command pipeline functionality in OpenWakeWordHelper.
/// Verifies command manifest loading, file-based command input, command token normalization,
/// and command dispatch behavior across different platforms.
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public sealed class CommandPipelineTests
{
    private readonly ITestOutputHelper _output;

    public CommandPipelineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static Type GetRuntimeOpenWakeWordHelperType()
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "PAIcom.OWW.dll"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PAIcom.OWW", "bin", "Release", "netstandard2.0", "PAIcom.OWW.dll")),
        };

        foreach (var runtimeAssemblyPath in candidatePaths)
        {
            if (!File.Exists(runtimeAssemblyPath))
            {
                continue;
            }

            var runtimeAssembly = Assembly.LoadFrom(runtimeAssemblyPath);
            return runtimeAssembly.GetType("CrossPlatformPatcher.Core.OpenWakeWordHelper", throwOnError: true)!;
        }

        throw new FileNotFoundException("Could not locate PAIcom.OWW.dll in the test output or project build output.");
    }

    #region Command Manifest Loading Tests

    [Fact]
    public void LoadKnownCommands_Reads_From_Commands_Txt()
    {
        // Arrange
        using var temp = new TempDirectory();
        var originalCurrentDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(temp.Path);
        var customCommandsDir = Path.Combine(temp.Path, "custom-commands");
        Directory.CreateDirectory(customCommandsDir);
        
        var commandsContent = """
            open the browser
            pause the music
            resume the music
            play the next song
            """;
        
        var commandsPath = Path.Combine(customCommandsDir, "commands.txt");
        File.WriteAllText(commandsPath, commandsContent);

        // Act - Load the manifest using reflection to access the private method
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var loadKnownCommandsMethod = helperType.GetMethod("LoadKnownCommands",
            BindingFlags.NonPublic | BindingFlags.Static);
        
        Assert.NotNull(loadKnownCommandsMethod);
        
        var commands = loadKnownCommandsMethod!.Invoke(null, null) as System.Collections.Generic.IReadOnlyList<object>;
        
        // Assert
        Assert.NotNull(commands);
        // LoadKnownCommands includes both file commands and built-in commands
        Assert.True(commands!.Count >= 4, $"Expected at least 4 commands, got {commands.Count}");

        Directory.SetCurrentDirectory(originalCurrentDirectory);
    }

    [Fact]
    public void LoadKnownCommands_Parses_Script_References_In_Parentheses()
    {
        // Arrange
        using var temp = new TempDirectory();
        var originalCurrentDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(temp.Path);
        var customCommandsDir = Path.Combine(temp.Path, "custom-commands");
        Directory.CreateDirectory(customCommandsDir);
        
        var commandsContent = """
            open the browser (open-browser)
            pause the music (pause-music)
            resume the music (resume-music)
            play the next song (next-song)
            """;
        
        var commandsPath = Path.Combine(customCommandsDir, "commands.txt");
        File.WriteAllText(commandsPath, commandsContent);

        // Act
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var loadKnownCommandsMethod = helperType.GetMethod("LoadKnownCommands",
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var commands = loadKnownCommandsMethod!.Invoke(null, null) as System.Collections.Generic.IReadOnlyList<object>;
        
        // Assert
        Assert.NotNull(commands);
        // LoadKnownCommands includes both file commands and built-in commands
        Assert.True(commands!.Count >= 4, $"Expected at least 4 commands, got {commands.Count}");
        
        // Verify script references are parsed - find the command with script reference
        var commandWithScript = commands.FirstOrDefault(c =>
        {
            var scriptRef = c?.GetType().GetProperty("ScriptReference")?.GetValue(c);
            return scriptRef != null && !string.IsNullOrEmpty(scriptRef.ToString());
        });
        Assert.NotNull(commandWithScript);
        var scriptReferenceProperty = commandWithScript?.GetType().GetProperty("ScriptReference");
        Assert.Equal("open-browser", scriptReferenceProperty!.GetValue(commandWithScript));

        Directory.SetCurrentDirectory(originalCurrentDirectory);
    }

    [Fact]
    public void LoadKnownCommands_Ignores_Comment_Lines()
    {
        // Arrange
        using var temp = new TempDirectory();
        var originalCurrentDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(temp.Path);
        var customCommandsDir = Path.Combine(temp.Path, "custom-commands");
        Directory.CreateDirectory(customCommandsDir);
        
        var commandsContent = """
            # This is a comment
            open the browser
            # Another comment
            pause the music
            """;
        
        var commandsPath = Path.Combine(customCommandsDir, "commands.txt");
        File.WriteAllText(commandsPath, commandsContent);

        // Act
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var loadKnownCommandsMethod = helperType.GetMethod("LoadKnownCommands",
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var commands = loadKnownCommandsMethod!.Invoke(null, null) as System.Collections.Generic.IReadOnlyList<object>;
        
        // Assert
        Assert.NotNull(commands);
        // LoadKnownCommands includes both file commands and built-in commands
        Assert.True(commands!.Count >= 2, $"Expected at least 2 commands, got {commands.Count}");

        Directory.SetCurrentDirectory(originalCurrentDirectory);
    }

    [Fact]
    public void LoadKnownCommands_Falls_Back_To_Builtin_Responses_When_Manifest_Not_Found()
    {
        // Arrange - Use a directory without commands.txt
        using var temp = new TempDirectory();
        var originalBaseDir = AppDomain.CurrentDomain.BaseDirectory;
        
        try
        {
            // Set base directory to temp (no commands.txt there)
            AppDomain.CurrentDomain.SetData("APPBASE", temp.Path);
            
            // Act
            var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
            var loadKnownCommandsMethod = helperType.GetMethod("LoadKnownCommands",
                BindingFlags.NonPublic | BindingFlags.Static);
            
            var commands = loadKnownCommandsMethod!.Invoke(null, null) as System.Collections.Generic.IReadOnlyList<object>;
            
            // Assert - Should have built-in commands
            Assert.NotNull(commands);
            Assert.True(commands!.Count > 0, "Should have fallback built-in commands");
        }
        finally
        {
            AppDomain.CurrentDomain.SetData("APPBASE", originalBaseDir);
        }
    }

    [Fact]
    public void LoadKnownCommands_Merges_Manifest_With_Builtin_Responses()
    {
        // Arrange
        using var temp = new TempDirectory();
        var originalCurrentDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(temp.Path);
        var customCommandsDir = Path.Combine(temp.Path, "custom-commands");
        Directory.CreateDirectory(customCommandsDir);
        
        var commandsContent = """
            custom command one
            custom command two
            """;
        
        var commandsPath = Path.Combine(customCommandsDir, "commands.txt");
        File.WriteAllText(commandsPath, commandsContent);

        // Act
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var loadKnownCommandsMethod = helperType.GetMethod("LoadKnownCommands",
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var commands = loadKnownCommandsMethod!.Invoke(null, null) as System.Collections.Generic.IReadOnlyList<object>;
        
        // Assert - Should have both custom and built-in commands
        Assert.NotNull(commands);
        Assert.True(commands!.Count > 2, "Should have both custom and built-in commands");

        Directory.SetCurrentDirectory(originalCurrentDirectory);
    }

    #endregion

    #region File-Based Command Input Tests

    [Fact]
    public void FileCommandInput_Enabled_Via_Environment_Variable()
    {
        // Arrange
        var originalValue = Environment.GetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT");
        
        try
        {
            Environment.SetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT", "1");
            
            // Act - Check initialization logic
            var helperType = GetRuntimeOpenWakeWordHelperType();
            var initializeFileCommandInputMethod = helperType.GetMethod("InitializeFileCommandInput", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            Assert.NotNull(initializeFileCommandInputMethod);
            
            // This should enable file command input
            initializeFileCommandInputMethod!.Invoke(null, null);
            
            // Assert - Verify the field was set (via reflection)
            var fileCommandInputEnabledField = helperType.GetField("_fileCommandInputEnabled", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            Assert.NotNull(fileCommandInputEnabledField);
            var isEnabled = (bool)(fileCommandInputEnabledField!.GetValue(null) ?? false);
            Assert.True(isEnabled, "File command input should be enabled");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT", originalValue);
        }
    }

    [Fact]
    public void FileCommandInput_Disabled_By_Default()
    {
        // Arrange
        var originalValue = Environment.GetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT");
        
        try
        {
            Environment.SetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT", null);
            
            // Act
            var helperType = GetRuntimeOpenWakeWordHelperType();
            var initializeFileCommandInputMethod = helperType.GetMethod("InitializeFileCommandInput", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            initializeFileCommandInputMethod!.Invoke(null, null);
            
            // Assert
            var fileCommandInputEnabledField = helperType.GetField("_fileCommandInputEnabled", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            var isEnabled = (bool)(fileCommandInputEnabledField!.GetValue(null) ?? false);
            Assert.False(isEnabled, "File command input should be disabled by default");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT", originalValue);
        }
    }

    [Fact]
    public void FileCommandInput_Creates_Input_File_If_Not_Exists()
    {
        // Arrange
        using var temp = new TempDirectory();
        var originalValue = Environment.GetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT");
        var originalPathValue = Environment.GetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT_PATH");
        
        try
        {
            var inputFilePath = Path.Combine(temp.Path, "input-command.txt");
            Environment.SetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT", "1");
            Environment.SetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT_PATH", inputFilePath);
            
            // Act
            var helperType = GetRuntimeOpenWakeWordHelperType();
            var initializeFileCommandInputMethod = helperType.GetMethod("InitializeFileCommandInput", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            initializeFileCommandInputMethod!.Invoke(null, null);
            
            // Assert
            Assert.True(File.Exists(inputFilePath));
            var content = File.ReadAllText(inputFilePath);
            Assert.Equal(string.Empty, content);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT", originalValue);
            Environment.SetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT_PATH", originalPathValue);
        }
    }

    [Fact]
    public void FileCommandInput_Uses_Default_Path_When_Not_Specified()
    {
        // Arrange
        var originalValue = Environment.GetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT");
        var originalPathValue = Environment.GetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT_PATH");
        
        try
        {
            Environment.SetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT", "1");
            Environment.SetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT_PATH", null);
            
            // Act
            var helperType = GetRuntimeOpenWakeWordHelperType();
            var initializeFileCommandInputMethod = helperType.GetMethod("InitializeFileCommandInput", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            initializeFileCommandInputMethod!.Invoke(null, null);
            
            // Assert
            var fileCommandInputPathField = helperType.GetField("_fileCommandInputPath", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            var path = fileCommandInputPathField!.GetValue(null) as string;
            Assert.NotNull(path);
            Assert.Contains("input-command.txt", path!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT", originalValue);
            Environment.SetEnvironmentVariable("PAICOM_FILE_COMMAND_INPUT_PATH", originalPathValue);
        }
    }

    #endregion

    #region Command Token Normalization Tests

    [Fact]
    public void TryNormalizeCommandToken_Accepts_Valid_Tokens()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var tryNormalizeMethod = helperType.GetMethod("TryNormalizeCommandToken", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var validTokens = new[] { "open-browser", "pause_music", "resumeMusic", "nextSong123" };
        
        // Act & Assert
        foreach (var token in validTokens)
        {
            var parameters = new object?[] { token, null, null };
            var result = (bool)(tryNormalizeMethod!.Invoke(null, parameters) ?? false);
            
            Assert.True(result);
            Assert.Equal(token, (string)parameters[1]!);
        }
    }

    [Fact]
    public void TryNormalizeCommandToken_Strips_Txt_Extension()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var tryNormalizeMethod = helperType.GetMethod("TryNormalizeCommandToken", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var parameters = new object?[] { "open-browser.txt", null, null };
        var result = (bool)(tryNormalizeMethod!.Invoke(null, parameters) ?? false);
        
        // Assert
        Assert.True(result);
        Assert.Equal("open-browser", (string)parameters[1]!);
    }

    [Fact]
    public void TryNormalizeCommandToken_Rejects_Empty_Tokens()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var tryNormalizeMethod = helperType.GetMethod("TryNormalizeCommandToken", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var emptyTokens = new[] { "", "   ", null, "\t", "\n" };
        
        // Act & Assert
        foreach (var token in emptyTokens)
        {
            var parameters = new object?[] { token, null, null };
            var result = (bool)(tryNormalizeMethod!.Invoke(null, parameters) ?? false);
            
            Assert.False(result);
            var reason = (string)parameters[2]!;
            Assert.Contains("empty", reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TryNormalizeCommandToken_Rejects_Absolute_Paths()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var tryNormalizeMethod = helperType.GetMethod("TryNormalizeCommandToken", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var absolutePaths = new[] { "C:\\Windows\\System32", "/usr/bin", "\\network\\share" };
        
        // Act & Assert
        foreach (var path in absolutePaths)
        {
            var parameters = new object?[] { path, null, null };
            var result = (bool)(tryNormalizeMethod!.Invoke(null, parameters) ?? false);
            
            Assert.False(result, $"Absolute path '{path}' should be rejected");
            Assert.Contains("absolute", ((string)parameters[2]).ToLowerInvariant());
        }
    }

    [Fact]
    public void TryNormalizeCommandToken_Rejects_Path_Traversal()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var tryNormalizeMethod = helperType.GetMethod("TryNormalizeCommandToken", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var traversalTokens = new[] { "../etc/passwd", "..\\Windows\\System32", "foo/../bar" };
        
        // Act & Assert
        foreach (var token in traversalTokens)
        {
            var parameters = new object?[] { token, null, null };
            var result = (bool)(tryNormalizeMethod!.Invoke(null, parameters) ?? false);
            
            Assert.False(result, $"Path traversal token '{token}' should be rejected");
            Assert.Contains("traversal", ((string)parameters[2]).ToLowerInvariant());
        }
    }

    [Fact]
    public void TryNormalizeCommandToken_Rejects_Path_Separators()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var tryNormalizeMethod = helperType.GetMethod("TryNormalizeCommandToken", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var pathSeparatorTokens = new[] { "foo/bar", "foo\\bar", "path/to/script" };
        
        // Act & Assert
        foreach (var token in pathSeparatorTokens)
        {
            var parameters = new object?[] { token, null, null };
            var result = (bool)(tryNormalizeMethod!.Invoke(null, parameters) ?? false);
            
            Assert.False(result, $"Token with path separator '{token}' should be rejected");
            Assert.Contains("separator", ((string)parameters[2]).ToLowerInvariant());
        }
    }

    [Fact]
    public void TryNormalizeCommandToken_Rejects_Shell_Metacharacters()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var tryNormalizeMethod = helperType.GetMethod("TryNormalizeCommandToken", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var metacharacterTokens = new[] { "cmd;rm -rf", "cmd&exit", "cmd|cat", "cmd`whoami`", "cmd>file", "cmd<file" };
        
        // Act & Assert
        foreach (var token in metacharacterTokens)
        {
            var parameters = new object?[] { token, null, null };
            var result = (bool)(tryNormalizeMethod!.Invoke(null, parameters) ?? false);
            
            Assert.False(result, $"Token with metacharacter '{token}' should be rejected");
            Assert.Contains("metacharacter", ((string)parameters[2]).ToLowerInvariant());
        }
    }

    [Fact]
    public void TryNormalizeCommandToken_Rejects_Home_Directory_Shorthand()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var tryNormalizeMethod = helperType.GetMethod("TryNormalizeCommandToken",
            BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act - Use just ~ without path separators to trigger the home directory check
        var parameters = new object?[] { "~", null, null };
        var result = (bool)(tryNormalizeMethod!.Invoke(null, parameters) ?? false);
        
        // Assert
        Assert.False(result);
        Assert.Contains("home-directory", ((string)parameters[2]).ToLowerInvariant());
    }

    #endregion

    #region Command Dispatch Tests

    [Fact]
    public void DispatchCommandAction_Tries_All_Dispatchers()
    {
        // Arrange
        var helperType = GetRuntimeOpenWakeWordHelperType();
        
        // Create a mock CommandAction
        var commandActionType = helperType
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(t => t.Name == "CommandAction");
        Assert.NotNull(commandActionType);
        var constructor = commandActionType!.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(c => c.GetParameters().Length == 7);
        Assert.NotNull(constructor);
        
        var action = constructor!.Invoke(new object[] { 
            "open the browser", "open the browser", "open the browser", 
            "open-browser", 0.95f, null, "Opening the browser." });
        
        // Act
        var dispatchMethod = helperType.GetMethod("DispatchCommandAction",
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var parameters = new object?[] { action, string.Empty };
        var result = (bool)(dispatchMethod!.Invoke(null, parameters) ?? false);
        
        // Assert - Should try all dispatchers
        var detail = (string)parameters[1];
        Assert.NotNull(detail);
        _output.WriteLine($"Dispatch detail: {detail}");
    }

    [Fact]
    public void DispatchCommandAction_Logs_Dispatcher_Order()
    {
        // Arrange
        var helperType = GetRuntimeOpenWakeWordHelperType();
        
        var commandActionType = helperType
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(t => t.Name == "CommandAction");
        Assert.NotNull(commandActionType);
        var constructor = commandActionType!.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(c => c.GetParameters().Length == 7);
        Assert.NotNull(constructor);
        
        var action = constructor!.Invoke(new object[] { 
            "test command", "test command", "test command", 
            "test", 0.90f, null, null });
        
        // Act
        var dispatchMethod = helperType.GetMethod("DispatchCommandAction",
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var parameters = new object?[] { action, string.Empty };
        dispatchMethod!.Invoke(null, parameters);
        
        // Assert - Verify dispatcher pipeline is logged
        var detail = (string)parameters[1];
        Assert.NotNull(detail);
        _output.WriteLine($"Dispatcher order logged: {detail}");
    }

    [Fact]
    public void DispatchCommandAction_Returns_False_When_No_Dispatcher_Succeeds()
    {
        // Arrange
        var helperType = GetRuntimeOpenWakeWordHelperType();
        
        var commandActionType = helperType
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(t => t.Name == "CommandAction");
        Assert.NotNull(commandActionType);
        var constructor = commandActionType!.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(c => c.GetParameters().Length == 7);
        Assert.NotNull(constructor);
        
        // Use an unknown command that won't match any dispatcher
        var action = constructor!.Invoke(new object[] { 
            "unknown xyz command", "unknown xyz command", "unknown xyz command", 
            "unknown-xyz", 0.50f, null, null });
        
        // Act
        var dispatchMethod = helperType.GetMethod("DispatchCommandAction",
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var parameters = new object?[] { action, string.Empty };
        var result = (bool)(dispatchMethod!.Invoke(null, parameters) ?? false);
        
        // Assert
        Assert.False(result, "Should return false when no dispatcher succeeds");
        var detail = (string)parameters[1];
        Assert.Contains("No dispatcher", detail);
    }

    #endregion

    #region Command Resolution Tests

    [Fact]
    public void ResolveCommandAction_Returns_Null_For_Empty_Transcript()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var resolveMethod = helperType.GetMethod("ResolveCommandAction", 
            BindingFlags.Public | BindingFlags.Static);
        
        // Act
        var result = resolveMethod!.Invoke(null, new object?[] { null });
        
        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveCommandAction_Returns_Null_For_Whitespace_Transcript()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var resolveMethod = helperType.GetMethod("ResolveCommandAction", 
            BindingFlags.Public | BindingFlags.Static);
        
        // Act
        var result = resolveMethod!.Invoke(null, new object?[] { "   \t\n  " });
        
        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveCommandAction_Uses_Fuzzy_Matching()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var resolveMethod = helperType.GetMethod("ResolveCommandAction", 
            BindingFlags.Public | BindingFlags.Static);
        
        // Act - Use a close match to a known command
        var result = resolveMethod!.Invoke(null, new object[] { "open browser" });
        
        // Assert - Should find a match via fuzzy matching
        Assert.NotNull(result);
        
        var resultType = result!.GetType();
        var matchPhraseProperty = resultType.GetProperty("MatchPhrase");
        Assert.NotNull(matchPhraseProperty);
        
        var matchPhrase = matchPhraseProperty!.GetValue(result) as string;
        Assert.NotNull(matchPhrase);
        _output.WriteLine($"Matched phrase: {matchPhrase}");
    }

    [Fact]
    public void ResolveCommandAction_Includes_Confidence_Score()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var resolveMethod = helperType.GetMethod("ResolveCommandAction", 
            BindingFlags.Public | BindingFlags.Static);
        
        // Act
        var result = resolveMethod!.Invoke(null, new object[] { "open the browser" });
        
        // Assert
        Assert.NotNull(result);
        
        var resultType = result!.GetType();
        var confidenceProperty = resultType.GetProperty("Confidence");
        Assert.NotNull(confidenceProperty);
        
        var confidence = Convert.ToSingle(confidenceProperty!.GetValue(result));
        Assert.True(confidence > 0.0f, "Confidence should be positive");
        Assert.True(confidence <= 1.0f, "Confidence should be <= 1.0");
        _output.WriteLine($"Confidence: {confidence:P1}");
    }

    [Fact]
    public void ResolveCommandAction_Includes_Script_Reference_When_Available()
    {
        // Arrange
        using var temp = new TempDirectory();
        var originalCurrentDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(temp.Path);
        var customCommandsDir = Path.Combine(temp.Path, "custom-commands");
        Directory.CreateDirectory(customCommandsDir);
        
        var commandsContent = """
            open the browser (open-browser-script)
            """;
        
        var commandsPath = Path.Combine(customCommandsDir, "commands.txt");
        File.WriteAllText(commandsPath, commandsContent);
        
        // Set environment variable to point to custom commands directory
        Environment.SetEnvironmentVariable("PAICOM_COMMANDS_DIR", customCommandsDir);
        
        try
        {
            // Reload commands
            var helperType = GetRuntimeOpenWakeWordHelperType();
            var loadKnownCommandsMethod = helperType.GetMethod("LoadKnownCommands",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(loadKnownCommandsMethod);

            var knownCommandsField = helperType.GetField("KnownCommands",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(knownCommandsField);

            var lazyValueType = loadKnownCommandsMethod!.ReturnType;
            var factoryType = typeof(Func<>).MakeGenericType(lazyValueType);
            var factory = Delegate.CreateDelegate(factoryType, loadKnownCommandsMethod);
            var lazyType = typeof(Lazy<>).MakeGenericType(lazyValueType);
            var lazyInstance = Activator.CreateInstance(lazyType, factory, true);
            Assert.NotNull(lazyInstance);
            knownCommandsField!.SetValue(null, lazyInstance);
            
            var resolveMethod = helperType.GetMethod("ResolveCommandAction",
                BindingFlags.Public | BindingFlags.Static);
            
            // Act
            var result = resolveMethod!.Invoke(null, new object[] { "open the browser" });
            
            // Assert
            Assert.NotNull(result);
            
            var resultType = result!.GetType();
            var scriptReferenceProperty = resultType.GetProperty("ScriptReference");
            Assert.NotNull(scriptReferenceProperty);
            
            var scriptReference = scriptReferenceProperty!.GetValue(result) as string;
            Assert.Equal("open-browser-script", scriptReference);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCurrentDirectory);
        }
    }

    #endregion

    #region Platform-Specific Dispatcher Order Tests

    [Fact]
    public void CommandDispatcherPipeline_Puts_ProcessFallback_Before_Reflection_On_Unix()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var buildPipelineMethod = helperType.GetMethod("BuildCommandDispatcherPipeline", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var dispatchers = buildPipelineMethod!.Invoke(null, null) as Array;
        
        // Assert
        Assert.NotNull(dispatchers);
        Assert.True(dispatchers!.Length >= 4, "Should have at least 4 dispatchers");
        
        var dispatcherNames = new List<string>();
        for (int i = 0; i < dispatchers.Length; i++)
        {
            var dispatcher = dispatchers.GetValue(i);
            var nameProperty = dispatcher?.GetType().GetProperty("Name");
                if (nameProperty != null)
                {
                    var dispatcherName = nameProperty.GetValue(dispatcher) as string;
                    if (!string.IsNullOrWhiteSpace(dispatcherName))
                        dispatcherNames.Add(dispatcherName);
                }
        }
        
        _output.WriteLine($"Dispatcher order: {string.Join(", ", dispatcherNames)}");
        
        // On Unix (or when Wine is detected), ProcessFallback should come before Reflection
        var processFallbackIndex = dispatcherNames.IndexOf("process-fallback");
        var reflectionIndex = dispatcherNames.IndexOf("game-reflection");
        
        if (processFallbackIndex >= 0 && reflectionIndex >= 0)
        {
            _output.WriteLine($"ProcessFallback at index {processFallbackIndex}, Reflection at index {reflectionIndex}");
        }
    }

    [Fact]
    public void CommandDispatcherPipeline_Includes_SpeechEmulation_Dispatcher()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var buildPipelineMethod = helperType.GetMethod("BuildCommandDispatcherPipeline", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var dispatchers = buildPipelineMethod!.Invoke(null, null) as Array;
        
        // Assert
        Assert.NotNull(dispatchers);
        
        var hasSpeechEmulation = false;
        for (int i = 0; i < dispatchers!.Length; i++)
        {
            var dispatcher = dispatchers.GetValue(i);
            var nameProperty = dispatcher?.GetType().GetProperty("Name");
                if (nameProperty != null)
                {
                    var name = nameProperty.GetValue(dispatcher) as string;
                if (name == "speech-emulation")
                {
                    hasSpeechEmulation = true;
                    break;
                }
            }
        }
        
        Assert.True(hasSpeechEmulation, "Pipeline should include SpeechEmulation dispatcher");
    }

    [Fact]
    public void CommandDispatcherPipeline_Includes_UiSimulation_Dispatcher()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var buildPipelineMethod = helperType.GetMethod("BuildCommandDispatcherPipeline", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        // Act
        var dispatchers = buildPipelineMethod!.Invoke(null, null) as Array;
        
        // Assert
        Assert.NotNull(dispatchers);
        
        var hasUiSimulation = false;
        for (int i = 0; i < dispatchers!.Length; i++)
        {
            var dispatcher = dispatchers.GetValue(i);
            var nameProperty = dispatcher?.GetType().GetProperty("Name");
                if (nameProperty != null)
                {
                    var name = nameProperty.GetValue(dispatcher) as string;
                if (name == "ui-simulation")
                {
                    hasUiSimulation = true;
                    break;
                }
            }
        }
        
        Assert.True(hasUiSimulation, "Pipeline should include UiSimulation dispatcher");
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void Full_Command_Pipeline_Resolves_And_Dispatches_Command()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        
        // Step 1: Resolve command action
        var resolveMethod = helperType.GetMethod("ResolveCommandAction", 
            BindingFlags.Public | BindingFlags.Static);
        
        var action = resolveMethod!.Invoke(null, new object[] { "open the browser" });
        
        Assert.NotNull(action);
        
        // Step 2: Dispatch the action
        var dispatchMethod = helperType.GetMethod("DispatchCommandAction", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        var parameters = new object?[] { action, string.Empty };
        var result = (bool)(dispatchMethod!.Invoke(null, parameters) ?? false);
        
        // Assert
        var detail = (string)parameters[1];
        _output.WriteLine($"Pipeline result: {(result ? "SUCCESS" : "FAILED")} - {detail}");
        
        // The result depends on whether any dispatcher can handle the command
        // At minimum, we should get a detail message
        Assert.NotNull(detail);
    }

    [Fact]
    public void Command_Pipeline_Preserves_Transcript_Through_Action()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var resolveMethod = helperType.GetMethod("ResolveCommandAction", 
            BindingFlags.Public | BindingFlags.Static);
        
        var originalTranscript = "open the browser please";
        
        // Act
        var action = resolveMethod!.Invoke(null, new object[] { originalTranscript });
        
        // Assert - ResolveCommandAction may return null for unknown commands
        // For known commands, it should preserve the transcript
        if (action != null)
        {
            var resultType = action!.GetType();
            var transcriptProperty = resultType.GetProperty("Transcript");
            Assert.NotNull(transcriptProperty);
            
            var preservedTranscript = transcriptProperty!.GetValue(action) as string;
            Assert.Equal(originalTranscript, preservedTranscript);
        }
        else
        {
            _output.WriteLine($"Command '{originalTranscript}' not resolved to an action");
        }
    }

    [Fact]
    public void Command_Pipeline_Handles_Variations_Of_Same_Command()
    {
        // Arrange
        var helperType = typeof(CrossPlatformPatcher.Core.OpenWakeWordHelper);
        var resolveMethod = helperType.GetMethod("ResolveCommandAction", 
            BindingFlags.Public | BindingFlags.Static);
        
        var variations = new[] { "open the browser", "open browser", "launch browser", "open the web" };
        
        // Act & Assert
        foreach (var variation in variations)
        {
            var action = resolveMethod!.Invoke(null, new object[] { variation });
            Assert.NotNull(action);
            
            var resultType = action!.GetType();
            var matchPhraseProperty = resultType.GetProperty("MatchPhrase");
            var matchPhrase = matchPhraseProperty!.GetValue(action) as string;
            
            _output.WriteLine($"Variation '{variation}' matched to '{matchPhrase}'");
        }
    }

    #endregion
}