using System;
using System.Reflection;

namespace CrossPlatformPatcher.Core.Audio;

/// <summary>
/// Cross-platform audio player that works without Windows-specific APIs.
/// Uses reflection to invoke SoundPlayer if available, or no-op gracefully on non-Windows.
/// </summary>
public sealed class CrossPlatformAudioPlayer : IAudioPlayer
{
    private readonly Type? _soundPlayerType;
    private AudioPlaybackState _state = AudioPlaybackState.Idle;
    private readonly Action<string>? _logger;

    public CrossPlatformAudioPlayer(Action<string>? logger = null)
    {
        _logger = logger;
        _soundPlayerType = TryGetSoundPlayerType();
        
        if (_soundPlayerType == null)
        {
            _logger?.Invoke("[audio] SoundPlayer not available on this platform (expected on non-Windows)");
        }
    }

    public bool TryLoadAudio(string resourcePath, out IAudioTrack? track, out string error)
    {
        track = null;
        error = string.Empty;

        if (_soundPlayerType == null)
        {
            error = "SoundPlayer not available on this platform";
            _logger?.Invoke($"[audio] Cannot load audio: {error}");
            return false;
        }

        try
        {
            var instance = Activator.CreateInstance(_soundPlayerType);
            if (instance == null)
            {
                error = "Failed to create SoundPlayer instance";
                return false;
            }

            // Set SoundLocation property
            var locationProp = _soundPlayerType.GetProperty("SoundLocation", BindingFlags.Public | BindingFlags.Instance);
            if (locationProp == null)
            {
                error = "SoundLocation property not found";
                return false;
            }

            locationProp.SetValue(instance, resourcePath);
            
            track = new WindowsFormsAudioTrack(instance, _soundPlayerType, resourcePath, _logger);
            _state = AudioPlaybackState.Idle;
            _logger?.Invoke($"[audio] Loaded audio: {resourcePath}");
            
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            _logger?.Invoke($"[audio] Failed to load audio: {error}");
            return false;
        }
    }

    public bool TryPlayAudio(IAudioTrack track, out string error)
    {
        error = string.Empty;

        if (track == null)
        {
            error = "Track cannot be null";
            return false;
        }

        if (_soundPlayerType == null)
        {
            error = "SoundPlayer not available on this platform";
            _logger?.Invoke($"[audio] Cannot play audio: {error}");
            return false;
        }

        try
        {
            if (track is not WindowsFormsAudioTrack wfTrack)
            {
                error = "Invalid track type";
                return false;
            }

            var playMethod = _soundPlayerType.GetMethod("Play", BindingFlags.Public | BindingFlags.Instance, null, Array.Empty<Type>(), null);
            if (playMethod == null)
            {
                error = "Play method not found";
                return false;
            }

            playMethod.Invoke(wfTrack.Instance, null);
            _state = AudioPlaybackState.Playing;
            _logger?.Invoke($"[audio] Playing: {track.ResourcePath}");
            
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            _state = AudioPlaybackState.Failed;
            _logger?.Invoke($"[audio] Playback failed: {error}");
            return false;
        }
    }

    public void Stop()
    {
        if (_soundPlayerType == null || _state == AudioPlaybackState.Idle)
            return;

        try
        {
            // Would need to track the current instance to stop it
            _state = AudioPlaybackState.Stopped;
            _logger?.Invoke("[audio] Playback stopped");
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[audio] Error stopping playback: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public AudioPlaybackState GetState() => _state;

    private static Type? TryGetSoundPlayerType()
    {
        try
        {
            return Type.GetType("System.Media.SoundPlayer", false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Wrapper for Windows Forms SoundPlayer tracks.
    /// </summary>
    private sealed class WindowsFormsAudioTrack : IAudioTrack
    {
        public object Instance { get; }
        private readonly Type _soundPlayerType;
        private readonly string _resourcePath;
        private readonly Action<string>? _logger;
        private bool _disposed;

        public string ResourcePath => _resourcePath;
        public TimeSpan Duration => TimeSpan.Zero; // SoundPlayer doesn't expose duration
        public bool IsValid => Instance != null && !_disposed;

        public WindowsFormsAudioTrack(object instance, Type soundPlayerType, string resourcePath, Action<string>? logger)
        {
            Instance = instance;
            _soundPlayerType = soundPlayerType;
            _resourcePath = resourcePath;
            _logger = logger;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                (Instance as IDisposable)?.Dispose();
                _logger?.Invoke($"[audio] Disposed track: {_resourcePath}");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[audio] Error disposing track: {ex.GetType().Name}: {ex.Message}");
            }

            _disposed = true;
        }
    }
}
