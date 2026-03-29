using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace CrossPlatformPatcher.Core.Animation;

/// <summary>
/// Cross-platform animation dispatcher that works without Windows Forms.
/// Uses reflection to invoke animation methods on any target, with proper timing.
/// </summary>
public sealed class CrossPlatformAnimationDispatcher : IAnimationDispatcher
{
    private readonly object? _uiThreadTarget;
    private readonly MethodInfo? _uiDispatchMethod;
    private readonly AnimationConfig _config;
    private AnimationState _state = AnimationState.Idle;
    private DateTime _lastFrameTime = DateTime.MinValue;
    private readonly Action<string>? _logger;

    public CrossPlatformAnimationDispatcher(AnimationConfig? config = null, Action<string>? logger = null)
    {
        _config = config ?? new AnimationConfig();
        _logger = logger;
        
        // Try to find the UI thread dispatcher if it exists
        _uiThreadTarget = TryGetUiThreadTarget();
        _uiDispatchMethod = TryGetUiDispatchMethod();
    }

    public Task AnimateFrame(int delayMs)
    {
        // Clamp to acceptable range
        var clampedDelay = Math.Max(_config.MinimumFrameTimeMs, Math.Min(delayMs, _config.MaximumFrameTimeMs));
        
        _state = AnimationState.Animating;
        _logger?.Invoke($"[animation] Frame scheduled with {clampedDelay}ms delay");
        
        // Return a task that completes after the specified delay
        return Task.Delay(clampedDelay).ContinueWith(_ =>
        {
            _state = AnimationState.Completed;
            _lastFrameTime = DateTime.UtcNow;
        });
    }

    public Task OnAnimationComplete(Task completedTask)
    {
        if (completedTask == null)
            return Task.CompletedTask;
        
        try
        {
            _logger?.Invoke($"[animation] Task completion handler: state={completedTask.Status}");
            
            if (completedTask.IsCompletedSuccessfully)
            {
                _state = AnimationState.Idle;
                return Task.CompletedTask;
            }
            
            if (completedTask.IsFaulted)
            {
                _state = AnimationState.Error;
                _logger?.Invoke($"[animation] Task failed: {completedTask.Exception?.InnerException?.Message}");
            }
            
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[animation] Error in completion handler: {ex.GetType().Name}: {ex.Message}");
            _state = AnimationState.Error;
            return Task.FromException(ex);
        }
    }

    public bool TryQueueUiAction(Action action, out string error)
    {
        error = string.Empty;
        
        if (action == null)
        {
            error = "Action cannot be null";
            return false;
        }

        try
        {
            if (_uiThreadTarget != null && _uiDispatchMethod != null)
            {
                _uiDispatchMethod.Invoke(_uiThreadTarget, new object[] { action });
                _logger?.Invoke("[animation] Action queued on UI thread");
                return true;
            }
            
            // Fallback: execute directly if no UI dispatcher
            action.Invoke();
            _logger?.Invoke("[animation] Action executed directly (no UI dispatcher)");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            _logger?.Invoke($"[animation] Failed to queue action: {error}");
            return false;
        }
    }

    public AnimationState GetState() => _state;

    private static object? TryGetUiThreadTarget()
    {
        // Try to find the WinForms SynchronizationContext or WPF Dispatcher
        var contextType = Type.GetType("System.Windows.Forms.WindowsFormsSynchronizationContext", false);
        if (contextType != null)
        {
            try
            {
                var currentProp = contextType.GetProperty("Current", BindingFlags.Static | BindingFlags.Public);
                if (currentProp?.GetValue(null) is var ctx && ctx != null)
                    return ctx;
            }
            catch { }
        }

        // Try WPF
        try
        {
            var dispatcherType = Type.GetType("System.Windows.Threading.Dispatcher", false);
            if (dispatcherType != null)
            {
                var currentProp = dispatcherType.GetProperty("CurrentDispatcher", BindingFlags.Static | BindingFlags.Public);
                if (currentProp?.GetValue(null) is var dispatcher && dispatcher != null)
                    return dispatcher;
            }
        }
        catch { }

        return null;
    }

    private static MethodInfo? TryGetUiDispatchMethod()
    {
        var contextType = Type.GetType("System.Windows.Forms.WindowsFormsSynchronizationContext", false);
        if (contextType != null)
        {
            var method = contextType.GetMethod("Post", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(SendOrPostCallback), typeof(object) }, null);
            if (method != null)
                return method;
        }

        return null;
    }
}
