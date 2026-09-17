using System;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Voice-dispatch deduplication (v0.1.2): the wake word can re-fire on the
/// tail of the same utterance (or a quick repeat), producing the same
/// command twice within seconds — observed as 2-3 browser windows from one
/// spoken command, with re-arms landing up to ~10s after the first dispatch.
/// When the same token dispatched successfully inside the window, the repeat
/// is suppressed with its own timing marker (not a failure). Only successful
/// dispatches arm suppression, so failed commands stay retryable; file-input
/// and test-queue paths bypass this (explicit invocations are intent, not echo).
/// Tunable via PAICOM_DISPATCH_DEDUP_MS (milliseconds, default 12000 —
/// user-measured re-arm max plus margin); zero or negative disables.
/// </summary>
public static class DispatchDedup
{
    public const string WindowMsVariable = "PAICOM_DISPATCH_DEDUP_MS";
    public const int DefaultWindowMs = 12000;

    private static readonly object _lock = new();
    private static string? _lastToken;
    private static long _lastTicks;

    public static int GetWindowMs()
    {
        var raw = Environment.GetEnvironmentVariable(WindowMsVariable);
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out var ms))
            return ms;
        return DefaultWindowMs;
    }

    /// <summary>
    /// True when <paramref name="token"/> dispatched successfully within the
    /// window. Never updates state: suppression does not extend suppression,
    /// so a deliberately repeated command executes again after the window.
    /// </summary>
    public static bool IsDuplicate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        var window = GetWindowMs();
        if (window <= 0)
            return false;
        lock (_lock)
        {
            if (_lastToken == null)
                return false;
            if (!string.Equals(_lastToken, token, StringComparison.OrdinalIgnoreCase))
                return false;
            var elapsedMs = (DateTime.UtcNow.Ticks - _lastTicks) / TimeSpan.TicksPerMillisecond;
            return elapsedMs < window;
        }
    }

    /// <summary>Arm suppression for <paramref name="token"/> from now.</summary>
    public static void Record(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;
        lock (_lock)
        {
            _lastToken = token;
            _lastTicks = DateTime.UtcNow.Ticks;
        }
    }

    internal static void Reset()
    {
        lock (_lock)
        {
            _lastToken = null;
            _lastTicks = 0;
        }
    }
}
