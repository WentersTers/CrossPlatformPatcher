using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CrossPlatformPatcher.Core;
using CrossPlatformPatcher.Core.Animation;
using CrossPlatformPatcher.Core.Audio;
using Xunit;
using Xunit.Abstractions;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Unit tests for AnimationScriptExecutor.
/// Verifies that animation scripts are loaded from .txt files, commands are executed correctly,
/// cross-platform animation dispatcher is used when available, and fallback behavior works safely.
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public sealed class AnimationScriptExecutorTests
{
    private readonly ITestOutputHelper _output;

    public AnimationScriptExecutorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #region Script Loading Tests

    [Fact]
    public async Task Loads_Script_From_Txt_File()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            WAIT 100
            SHOW 1
            HIDE 1
            """;

        var scriptPath = Path.Combine(animationsDir, "test_script.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("test_script");

        // Assert
        Assert.True(result.Success);
        Assert.Contains("test_script", result.Detail);
        // WAIT doesn't queue actions, only SHOW and HIDE do
        Assert.Equal(2, mockDispatcher.QueuedActions.Count);
    }

    [Fact]
    public async Task Loads_Script_With_Extension_Provided()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "WAIT 50";
        var scriptPath = Path.Combine(animationsDir, "with_extension.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("with_extension.txt");

        // Assert
        Assert.True(result.Success);
        // WAIT doesn't queue actions
        Assert.Equal(0, mockDispatcher.QueuedActions.Count);
    }

    [Fact]
    public async Task Returns_Failure_When_Script_Not_Found()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("nonexistent_script");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Detail);
    }

    [Fact]
    public async Task Returns_Failure_When_Script_Name_Is_Empty()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("empty", result.Detail);
    }

    [Fact]
    public async Task Returns_Failure_When_Script_Name_Is_Null()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync(null!);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("empty", result.Detail);
    }

    #endregion

    #region SHOW Command Tests

    [Fact]
    public async Task Executes_SHOW_Command_With_Valid_Frame_Number()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "SHOW 5";
        var scriptPath = Path.Combine(animationsDir, "show_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("show_test");

        // Assert
        Assert.True(result.Success);
        Assert.Single(mockDispatcher.QueuedActions);
    }

    [Fact]
    public async Task Executes_Multiple_SHOW_Commands()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            SHOW 1
            SHOW 2
            SHOW 3
            """;

        var scriptPath = Path.Combine(animationsDir, "multiple_show.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("multiple_show");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(3, mockDispatcher.QueuedActions.Count);
    }

    [Fact]
    public async Task Logs_Error_When_SHOW_Command_Has_Invalid_Frame_Number()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "SHOW invalid";
        var scriptPath = Path.Combine(animationsDir, "show_invalid.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("show_invalid");

        // Assert
        Assert.True(result.Success); // Script executes, but command is logged as invalid
        Assert.Empty(mockDispatcher.QueuedActions);
    }

    [Fact]
    public async Task Logs_Error_When_SHOW_Command_Missing_Frame_Number()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "SHOW";
        var scriptPath = Path.Combine(animationsDir, "show_missing.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("show_missing");

        // Assert
        Assert.True(result.Success);
        Assert.Empty(mockDispatcher.QueuedActions);
    }

    #endregion

    #region HIDE Command Tests

    [Fact]
    public async Task Executes_HIDE_Command_With_Valid_Frame_Number()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "HIDE 3";
        var scriptPath = Path.Combine(animationsDir, "hide_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("hide_test");

        // Assert
        Assert.True(result.Success);
        Assert.Single(mockDispatcher.QueuedActions);
    }

    [Fact]
    public async Task Executes_Multiple_HIDE_Commands()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            HIDE 1
            HIDE 2
            HIDE 3
            """;

        var scriptPath = Path.Combine(animationsDir, "multiple_hide.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("multiple_hide");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(3, mockDispatcher.QueuedActions.Count);
    }

    [Fact]
    public async Task Logs_Error_When_HIDE_Command_Has_Invalid_Frame_Number()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "HIDE invalid";
        var scriptPath = Path.Combine(animationsDir, "hide_invalid.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("hide_invalid");

        // Assert
        Assert.True(result.Success);
        Assert.Empty(mockDispatcher.QueuedActions);
    }

    #endregion

    #region WAIT Command Tests

    [Fact]
    public async Task Executes_WAIT_Command_With_Valid_Delay()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "WAIT 100";
        var scriptPath = Path.Combine(animationsDir, "wait_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("wait_test");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, mockDispatcher.WaitCalls.Count);
        Assert.Equal(100, mockDispatcher.WaitCalls[0]);
    }

    [Fact]
    public async Task Executes_Multiple_WAIT_Commands()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            WAIT 50
            WAIT 100
            WAIT 150
            """;

        var scriptPath = Path.Combine(animationsDir, "multiple_wait.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("multiple_wait");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(3, mockDispatcher.WaitCalls.Count);
        Assert.Equal(50, mockDispatcher.WaitCalls[0]);
        Assert.Equal(100, mockDispatcher.WaitCalls[1]);
        Assert.Equal(150, mockDispatcher.WaitCalls[2]);
    }

    [Fact]
    public async Task Logs_Error_When_WAIT_Command_Has_Invalid_Delay()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "WAIT invalid";
        var scriptPath = Path.Combine(animationsDir, "wait_invalid.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("wait_invalid");

        // Assert
        Assert.True(result.Success);
        Assert.Empty(mockDispatcher.WaitCalls);
    }

    #endregion

    #region PLAY_AUDIO Command Tests

    [Fact]
    public async Task Executes_PLAY_AUDIO_Command_With_Valid_File()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "PLAY_AUDIO test_audio.wav";
        var scriptPath = Path.Combine(animationsDir, "play_audio_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var audioFilePath = Path.Combine(audioDir, "test_audio.wav");
        File.WriteAllText(audioFilePath, "fake audio data");

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("play_audio_test");

        // Assert
        Assert.True(result.Success);
        Assert.Single(mockPlayer.LoadedAudioFiles);
        Assert.Contains("test_audio.wav", mockPlayer.LoadedAudioFiles[0]);
        Assert.Single(mockPlayer.PlayedAudioFiles);
    }

    [Fact]
    public async Task Executes_PLAY_AUDIO_Command_With_File_Path_Containing_Spaces()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "PLAY_AUDIO my audio file.wav";
        var scriptPath = Path.Combine(animationsDir, "play_audio_spaces.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var audioFilePath = Path.Combine(audioDir, "my audio file.wav");
        File.WriteAllText(audioFilePath, "fake audio data");

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("play_audio_spaces");

        // Assert
        Assert.True(result.Success);
        Assert.Single(mockPlayer.LoadedAudioFiles);
        Assert.Contains("my audio file.wav", mockPlayer.LoadedAudioFiles[0]);
    }

    [Fact]
    public async Task Executes_PLAY_AUDIO_Command_With_Mp3_Extension()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "PLAY_AUDIO test_audio";
        var scriptPath = Path.Combine(animationsDir, "play_audio_mp3.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var audioFilePath = Path.Combine(audioDir, "test_audio.mp3");
        File.WriteAllText(audioFilePath, "fake audio data");

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("play_audio_mp3");

        // Assert
        Assert.True(result.Success);
        Assert.Single(mockPlayer.LoadedAudioFiles);
        Assert.Contains("test_audio.mp3", mockPlayer.LoadedAudioFiles[0]);
    }

    [Fact]
    public async Task Logs_Error_When_PLAY_AUDIO_Command_Missing_File_Name()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "PLAY_AUDIO";
        var scriptPath = Path.Combine(animationsDir, "play_audio_missing.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("play_audio_missing");

        // Assert
        Assert.True(result.Success);
        Assert.Empty(mockPlayer.LoadedAudioFiles);
    }

    #endregion

    #region OPEN_URL Command Tests

    [Fact]
    public async Task Executes_OPEN_URL_Command_With_Valid_Url()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "OPEN_URL https://example.com";
        var scriptPath = Path.Combine(animationsDir, "open_url_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("open_url_test");

        // Assert
        Assert.True(result.Success);
        // OPEN_URL command is executed but we can't easily verify Process.Start in tests
        // The important thing is that it doesn't crash
    }

    [Fact]
    public async Task Executes_OPEN_URL_Command_With_Url_Containing_Spaces()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "OPEN_URL https://example.com/path with spaces";
        var scriptPath = Path.Combine(animationsDir, "open_url_spaces.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("open_url_spaces");

        // Assert
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Logs_Error_When_OPEN_URL_Command_Missing_Url()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "OPEN_URL";
        var scriptPath = Path.Combine(animationsDir, "open_url_missing.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("open_url_missing");

        // Assert
        Assert.True(result.Success);
    }

    #endregion

    #region HIDE_ALL Command Tests

    [Fact]
    public async Task Executes_HIDE_ALL_Command()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "HIDE_ALL";
        var scriptPath = Path.Combine(animationsDir, "hide_all_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("hide_all_test");

        // Assert
        Assert.True(result.Success);
        Assert.Single(mockDispatcher.QueuedActions);
    }

    #endregion

    #region Cross-Platform Animation Dispatcher Tests

    [Fact]
    public async Task Uses_Cross_Platform_Animation_Dispatcher_When_Present()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            SHOW 1
            HIDE 1
            HIDE_ALL
            """;

        var scriptPath = Path.Combine(animationsDir, "dispatcher_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("dispatcher_test");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(3, mockDispatcher.QueuedActions.Count);
    }

    [Fact]
    public async Task Uses_AnimateFrame_For_WAIT_When_Dispatcher_Present()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = "WAIT 200";
        var scriptPath = Path.Combine(animationsDir, "animate_frame_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("animate_frame_test");

        // Assert
        Assert.True(result.Success);
        Assert.Single(mockDispatcher.WaitCalls);
        Assert.Equal(200, mockDispatcher.WaitCalls[0]);
    }

    #endregion

    #region Fallback Behavior Tests

    [Fact]
    public async Task Falls_Back_Safely_When_Dispatcher_Unavailable()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            SHOW 1
            HIDE 1
            HIDE_ALL
            WAIT 100
            """;

        var scriptPath = Path.Combine(animationsDir, "no_dispatcher_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            null, // No dispatcher
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("no_dispatcher_test");

        // Assert
        Assert.True(result.Success);
        // Script should complete without crashing even without dispatcher
        // SHOW, HIDE, HIDE_ALL commands are logged but not executed
        // WAIT command should still work via Task.Delay fallback
    }

    [Fact]
    public async Task Falls_Back_Safely_When_Audio_Player_Unavailable()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            SHOW 1
            PLAY_AUDIO test.wav
            WAIT 50
            """;

        var scriptPath = Path.Combine(animationsDir, "no_audio_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            null, // No audio player
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("no_audio_test");

        // Assert
        Assert.True(result.Success);
        Assert.Single(mockDispatcher.QueuedActions);
        // PLAY_AUDIO should be logged but not crash
    }

    [Fact]
    public async Task Falls_Back_Safely_When_Both_Dispatcher_And_Audio_Player_Unavailable()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            SHOW 1
            HIDE 1
            PLAY_AUDIO test.wav
            WAIT 50
            OPEN_URL https://example.com
            """;

        var scriptPath = Path.Combine(animationsDir, "no_components_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            null, // No dispatcher
            null, // No audio player
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("no_components_test");

        // Assert
        Assert.True(result.Success);
        // Script should complete without crashing
        // WAIT should work via Task.Delay fallback
        // OPEN_URL should still work (doesn't depend on dispatcher or audio player)
    }

    #endregion

    #region Malformed Command Tests

    [Fact]
    public async Task Ignores_Malformed_Commands_Without_Crashing()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            SHOW 1
            INVALID_COMMAND
            HIDE 1
            ANOTHER_INVALID
            WAIT 50
            """;

        var scriptPath = Path.Combine(animationsDir, "malformed_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("malformed_test");

        // Assert
        Assert.True(result.Success);
        // Valid commands should still execute (SHOW and HIDE queue actions, WAIT doesn't)
        Assert.Equal(2, mockDispatcher.QueuedActions.Count);
        Assert.Equal(1, mockDispatcher.WaitCalls.Count);
    }

    [Fact]
    public async Task Ignores_Empty_Lines()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            SHOW 1

            HIDE 1

            WAIT 50

            """;

        var scriptPath = Path.Combine(animationsDir, "empty_lines_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("empty_lines_test");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, mockDispatcher.QueuedActions.Count);
        Assert.Single(mockDispatcher.WaitCalls);
    }

    [Fact]
    public async Task Ignores_Comment_Lines()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            # This is a comment
            SHOW 1
            # Another comment
            HIDE 1
            # Final comment
            WAIT 50
            """;

        var scriptPath = Path.Combine(animationsDir, "comments_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("comments_test");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, mockDispatcher.QueuedActions.Count);
        Assert.Single(mockDispatcher.WaitCalls);
    }

    [Fact]
    public async Task Ignores_Whitespace_Only_Lines()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            SHOW 1
             
            HIDE 1
            \t
            WAIT 50
            """;

        var scriptPath = Path.Combine(animationsDir, "whitespace_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("whitespace_test");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, mockDispatcher.QueuedActions.Count);
        Assert.Single(mockDispatcher.WaitCalls);
    }

    [Fact]
    public async Task Handles_Commands_With_Extra_Whitespace()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
              SHOW   1  
              HIDE   2  
              WAIT   100  
            """;

        var scriptPath = Path.Combine(animationsDir, "extra_whitespace_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("extra_whitespace_test");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, mockDispatcher.QueuedActions.Count);
        Assert.Single(mockDispatcher.WaitCalls);
    }

    [Fact]
    public async Task Handles_Case_Insensitive_Commands()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            show 1
            hide 1
            wait 50
            hide_all
            """;

        var scriptPath = Path.Combine(animationsDir, "case_insensitive_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("case_insensitive_test");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(3, mockDispatcher.QueuedActions.Count);
        Assert.Single(mockDispatcher.WaitCalls);
    }

    #endregion

    #region Complex Script Tests

    [Fact]
    public async Task Executes_Complex_Script_With_All_Command_Types()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            # Intro animation
            SHOW 1
            WAIT 100
            SHOW 2
            WAIT 100
            
            # Play sound
            PLAY_AUDIO intro.wav
            
            # More animation
            SHOW 3
            WAIT 150
            HIDE 1
            WAIT 50
            HIDE 2
            WAIT 50
            HIDE 3
            
            # Cleanup
            HIDE_ALL
            """;

        var scriptPath = Path.Combine(animationsDir, "complex_script.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var audioFilePath = Path.Combine(audioDir, "intro.wav");
        File.WriteAllText(audioFilePath, "fake audio data");

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("complex_script");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(7, mockDispatcher.QueuedActions.Count);
        Assert.Equal(5, mockDispatcher.WaitCalls.Count);
        Assert.Single(mockPlayer.LoadedAudioFiles);
    }

    [Fact]
    public async Task Handles_Script_With_Mixed_Valid_And_Invalid_Commands()
    {
        // Arrange
        using var temp = new TempDirectory();
        var animationsDir = Path.Combine(temp.Path, "animations");
        var audioDir = Path.Combine(temp.Path, "audio");
        Directory.CreateDirectory(animationsDir);
        Directory.CreateDirectory(audioDir);

        var scriptContent = """
            SHOW 1
            INVALID
            WAIT 50
            SHOW invalid
            HIDE 2
            PLAY_AUDIO
            WAIT 100
            """;

        var scriptPath = Path.Combine(animationsDir, "mixed_commands_test.txt");
        File.WriteAllText(scriptPath, scriptContent);

        var mockDispatcher = new MockAnimationDispatcher();
        var mockPlayer = new MockAudioPlayer();
        var executor = new AnimationScriptExecutor(
            animationsDir,
            audioDir,
            mockDispatcher,
            mockPlayer,
            _output.WriteLine);

        // Act
        var result = await executor.ExecuteScriptAsync("mixed_commands_test");

        // Assert
        Assert.True(result.Success);
        // Only valid commands should execute
        Assert.Equal(2, mockDispatcher.QueuedActions.Count);
        Assert.Equal(2, mockDispatcher.WaitCalls.Count);
    }

    #endregion

    #region Mock Classes

    private sealed class MockAnimationDispatcher : IAnimationDispatcher
    {
        public List<QueuedAction> QueuedActions { get; } = new();
        public List<int> WaitCalls { get; } = new();

        public Task AnimateFrame(int delayMs)
        {
            WaitCalls.Add(delayMs);
            return Task.CompletedTask;
        }

        public Task OnAnimationComplete(Task completedTask)
        {
            return Task.CompletedTask;
        }

        public bool TryQueueUiAction(Action action, out string error)
        {
            error = string.Empty;
            QueuedActions.Add(new QueuedAction(action));
            action();
            return true;
        }

        public AnimationState GetState()
        {
            return AnimationState.Idle;
        }
    }

    private sealed class MockAudioPlayer : IAudioPlayer
    {
        public List<string> LoadedAudioFiles { get; } = new();
        public List<IAudioTrack> PlayedAudioFiles { get; } = new();

        public bool TryLoadAudio(string resourcePath, out IAudioTrack? track, out string error)
        {
            error = string.Empty;
            track = new MockAudioTrack(resourcePath);
            LoadedAudioFiles.Add(resourcePath);
            return true;
        }

        public bool TryPlayAudio(IAudioTrack track, out string error)
        {
            error = string.Empty;
            PlayedAudioFiles.Add(track);
            return true;
        }

        public void Stop()
        {
        }

        public AudioPlaybackState GetState()
        {
            return AudioPlaybackState.Idle;
        }
    }

    private sealed class MockAudioTrack : IAudioTrack
    {
        public string ResourcePath { get; }
        public TimeSpan Duration => TimeSpan.FromSeconds(1);
        public bool IsValid => true;

        public MockAudioTrack(string resourcePath)
        {
            ResourcePath = resourcePath;
        }

        public void Dispose()
        {
        }
    }

    private sealed record QueuedAction(Action Action, string Command = "");

    #endregion
}