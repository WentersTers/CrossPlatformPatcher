using System.Threading.Tasks;

namespace CrossPlatformPatcher.Core.Animation;

/// <summary>
/// Platform-agnostic animation interface for cross-platform animation operations.
/// Handles frame timing and animation callbacks without Windows Forms dependencies.
/// </summary>
public interface IAnimationDispatcher
{
    /// <summary>
    /// Schedule an animation frame with async timing.
    /// </summary>
    /// <param name="delayMs">Delay in milliseconds before frame execution</param>
    Task AnimateFrame(int delayMs);
    
    /// <summary>
    /// Handle animation task completion.
    /// </summary>
    Task OnAnimationComplete(Task completedTask);
    
    /// <summary>
    /// Queue an action to be executed on the game's UI thread.
    /// </summary>
    bool TryQueueUiAction(Action action, out string error);
    
    /// <summary>
    /// Get the current animation state.
    /// </summary>
    AnimationState GetState();
}

/// <summary>
/// Animation dispatch state.
/// </summary>
public enum AnimationState
{
    Idle,
    Animating,
    Queued,
    Completed,
    Error
}

/// <summary>
/// Configuration for animation operations.
/// </summary>
public record AnimationConfig(
    int MinimumFrameTimeMs = 16,      // 60 FPS
    int MaximumFrameTimeMs = 1000,
    bool EnableFrameRateControl = true,
    int TargetFrameRate = 60
);
