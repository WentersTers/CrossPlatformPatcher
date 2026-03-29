using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CrossPlatformPatcher.Core.Audio;
using CrossPlatformPatcher.Core.Animation;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Executes animation scripts from text files.
/// Scripts contain commands like WAIT, SHOW, HIDE, PLAY_AUDIO, HIDE_ALL.
/// </summary>
public sealed class AnimationScriptExecutor
{
    private readonly string _animationsRootPath;
    private readonly string _audioRootPath;
    private readonly IAnimationDispatcher? _animationDispatcher;
    private readonly IAudioPlayer? _audioPlayer;
    private readonly Action<string>? _logger;

    public AnimationScriptExecutor(
        string animationsRootPath,
        string audioRootPath,
        IAnimationDispatcher? animationDispatcher,
        IAudioPlayer? audioPlayer,
        Action<string>? logger = null)
    {
        _animationsRootPath = animationsRootPath ?? throw new ArgumentNullException(nameof(animationsRootPath));
        _audioRootPath = audioRootPath ?? throw new ArgumentNullException(nameof(audioRootPath));
        _animationDispatcher = animationDispatcher;
        _audioPlayer = audioPlayer;
        _logger = logger;
    }

    /// <summary>
    /// Execute an animation script by name (without .txt extension).
    /// Returns (success, detail).
    /// </summary>
    public async Task<(bool Success, string Detail)> ExecuteScriptAsync(string scriptName)
    {
        if (string.IsNullOrWhiteSpace(scriptName))
        {
            return (false, "Script name cannot be empty");
        }

        // Remove extension if provided
        var baseName = Path.GetFileNameWithoutExtension(scriptName);
        var scriptPath = Path.Combine(_animationsRootPath, baseName + ".txt");

        if (!File.Exists(scriptPath))
        {
            _logger?.Invoke($"[animation-script] Script not found: {scriptPath}");
            return (false, $"Animation script not found: {scriptPath}");
        }

        try
        {
            var lines = File.ReadAllLines(scriptPath);
            _logger?.Invoke($"[animation-script] Loaded script: {scriptPath} ({lines.Length} lines)");

            await ExecuteCommandsAsync(lines);
            return (true, $"Animation script executed: {scriptPath}");
        }
        catch (Exception ex)
        {
            var detail = $"Error executing animation script: {ex.GetType().Name}: {ex.Message}";
            _logger?.Invoke($"[animation-script-error] {detail}");
            return (false, detail);
        }
    }

    private async Task ExecuteCommandsAsync(string[] lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                continue;

            try
            {
                await ExecuteCommandAsync(line);
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[animation-script] Error executing line '{line}': {ex.Message}");
            }
        }
    }

    private async Task ExecuteCommandAsync(string line)
    {
        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        var command = parts[0].ToUpperInvariant();

        switch (command)
        {
            case "HIDE_ALL":
                ExecuteHideAll();
                break;

            case "SHOW":
                if (parts.Length >= 2 && int.TryParse(parts[1], out var showFrame))
                {
                    ExecuteShow(showFrame);
                }
                else
                {
                    _logger?.Invoke($"[animation-script] Invalid SHOW command: {line}");
                }
                break;

            case "HIDE":
                if (parts.Length >= 2 && int.TryParse(parts[1], out var hideFrame))
                {
                    ExecuteHide(hideFrame);
                }
                else
                {
                    _logger?.Invoke($"[animation-script] Invalid HIDE command: {line}");
                }
                break;

            case "WAIT":
                if (parts.Length >= 2 && int.TryParse(parts[1], out var delayMs))
                {
                    await ExecuteWaitAsync(delayMs);
                }
                else
                {
                    _logger?.Invoke($"[animation-script] Invalid WAIT command: {line}");
                }
                break;

            case "PLAY_AUDIO":
                if (parts.Length >= 2)
                {
                    var audioFile = string.Join(" ", parts, 1, parts.Length - 1);
                    ExecutePlayAudio(audioFile);
                }
                else
                {
                    _logger?.Invoke($"[animation-script] Invalid PLAY_AUDIO command: {line}");
                }
                break;

            case "OPEN_URL":
                if (parts.Length >= 2)
                {
                    var url = string.Join(" ", parts, 1, parts.Length - 1);
                    ExecuteOpenUrl(url);
                }
                else
                {
                    _logger?.Invoke($"[animation-script] Invalid OPEN_URL command: {line}");
                }
                break;

            default:
                _logger?.Invoke($"[animation-script] Unknown command: {command}");
                break;
        }
    }

    private void ExecuteHideAll()
    {
        _logger?.Invoke("[animation-script] HIDE_ALL");
        
        // Queue action to hide all frames
        if (_animationDispatcher != null)
        {
            _animationDispatcher.TryQueueUiAction(() =>
            {
                _logger?.Invoke("[animation-script-action] Hiding all frames");
            }, out var error);
            
            if (!string.IsNullOrEmpty(error))
                _logger?.Invoke($"[animation-script] Failed to queue HIDE_ALL: {error}");
        }
    }

    private void ExecuteShow(int frameNumber)
    {
        _logger?.Invoke($"[animation-script] SHOW {frameNumber}");
        
        // Queue action to show frame
        if (_animationDispatcher != null)
        {
            _animationDispatcher.TryQueueUiAction(() =>
            {
                _logger?.Invoke($"[animation-script-action] Showing frame {frameNumber}");
            }, out var error);
            
            if (!string.IsNullOrEmpty(error))
                _logger?.Invoke($"[animation-script] Failed to queue SHOW {frameNumber}: {error}");
        }
    }

    private void ExecuteHide(int frameNumber)
    {
        _logger?.Invoke($"[animation-script] HIDE {frameNumber}");
        
        // Queue action to hide frame
        if (_animationDispatcher != null)
        {
            _animationDispatcher.TryQueueUiAction(() =>
            {
                _logger?.Invoke($"[animation-script-action] Hiding frame {frameNumber}");
            }, out var error);
            
            if (!string.IsNullOrEmpty(error))
                _logger?.Invoke($"[animation-script] Failed to queue HIDE {frameNumber}: {error}");
        }
    }

    private async Task ExecuteWaitAsync(int delayMs)
    {
        _logger?.Invoke($"[animation-script] WAIT {delayMs}");
        
        if (_animationDispatcher != null)
        {
            try
            {
                await _animationDispatcher.AnimateFrame(delayMs);
                _logger?.Invoke($"[animation-script-action] Waited {delayMs}ms");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[animation-script] Error during WAIT: {ex.Message}");
            }
        }
        else
        {
            // Fallback to Task.Delay if dispatcher unavailable
            await Task.Delay(delayMs);
        }
    }

    private void ExecutePlayAudio(string audioFile)
    {
        _logger?.Invoke($"[animation-script] PLAY_AUDIO {audioFile}");
        
        if (_audioPlayer == null)
        {
            _logger?.Invoke("[animation-script] Audio player not available");
            return;
        }

        try
        {
            // Resolve audio file path
            var audioPath = ResolveAudioPath(audioFile);
            if (string.IsNullOrEmpty(audioPath))
            {
                _logger?.Invoke($"[animation-script] Audio file not found: {audioFile}");
                return;
            }

            if (_audioPlayer.TryLoadAudio(audioPath, out var track, out var loadError))
            {
                if (track == null)
                {
                    _logger?.Invoke($"[animation-script] Audio track is null after load");
                    return;
                }

                if (!_audioPlayer.TryPlayAudio(track, out var playError))
                {
                    _logger?.Invoke($"[animation-script] Failed to play audio: {playError}");
                    track.Dispose();
                    return;
                }

                _logger?.Invoke($"[animation-script-action] Playing audio: {audioPath}");
            }
            else
            {
                _logger?.Invoke($"[animation-script] Failed to load audio: {loadError}");
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[animation-script] Error playing audio: {ex.Message}");
        }
    }

    private string? ResolveAudioPath(string audioFile)
    {
        // Try direct path first
        var directPath = Path.Combine(_audioRootPath, audioFile);
        if (File.Exists(directPath))
            return directPath;

        // Try with .wav extension
        var wavPath = Path.Combine(_audioRootPath, Path.GetFileNameWithoutExtension(audioFile) + ".wav");
        if (File.Exists(wavPath))
            return wavPath;

        // Try with .mp3 extension
        var mp3Path = Path.Combine(_audioRootPath, Path.GetFileNameWithoutExtension(audioFile) + ".mp3");
        if (File.Exists(mp3Path))
            return mp3Path;

        return null;
    }

    private void ExecuteOpenUrl(string url)
    {
        _logger?.Invoke($"[animation-script] OPEN_URL {url}");

        try
        {
            // Use Process.Start to open URL in default browser, platform-agnostic
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            _logger?.Invoke($"[animation-script-action] Opened URL: {url}");
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[animation-script] Error opening URL: {ex.Message}");
        }
    }
}
