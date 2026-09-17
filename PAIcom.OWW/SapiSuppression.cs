using System;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Product speech-loop suppression decision (v0.1.2), pure and unit-tested.
/// Suppressed while Vosk is alive, or while our own SAPI fallback keeps
/// proving itself (each successful SAPI transcript refreshes a window);
/// when neither can dispatch, the product loop runs free as the ultimate
/// fallback. Shared-sourced into the test project like the other
/// decision units; the injected helper delegates to this.
/// </summary>
public static class SapiSuppression
{
    public const long ProofWindowSeconds = 120;

    public static bool ShouldSuppress(bool voskAlive, long proofTicks, long nowTicks)
    {
        if (voskAlive)
            return true;
        if (proofTicks <= 0)
            return false;
        return nowTicks - proofTicks < ProofWindowSeconds * TimeSpan.TicksPerSecond;
    }
}
