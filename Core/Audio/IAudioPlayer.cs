namespace CrossPlatformPatcher.Core.Audio;

/// <summary>
/// Platform-agnostic audio interface for cross-platform audio operations.
/// Allows audio playback without depending on Windows-specific APIs.
/// </summary>
public interface IAudioPlayer
{
    /// <summary>Load audio from a resource path.</summary>
    bool TryLoadAudio(string resourcePath, out IAudioTrack? track, out string error);
    
    /// <summary>Play an audio track.</summary>
    bool TryPlayAudio(IAudioTrack track, out string error);
    
    /// <summary>Stop current playback.</summary>
    void Stop();
    
    /// <summary>Get playback status.</summary>
    AudioPlaybackState GetState();
}

/// <summary>
/// Represents a loaded audio track.
/// </summary>
public interface IAudioTrack : IDisposable
{
    string ResourcePath { get; }
    TimeSpan Duration { get; }
    bool IsValid { get; }
}

/// <summary>
/// Audio playback state.
/// </summary>
public enum AudioPlaybackState
{
    Idle,
    Playing,
    Paused,
    Stopped,
    Failed
}
