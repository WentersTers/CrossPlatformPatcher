using System;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Bridge defers to the product's own speech loop (Option A): the product's
/// SAPI engine fires first (its results only fire on grammar match, and it
/// acts on every match), so when its observed result resolves to the SAME
/// command our matcher resolved, our dispatch stands down instead of
/// executing twice. Agreement is on the matched command token, never on raw
/// transcript text (over-defer guard): a product result that maps nowhere,
/// or maps elsewhere, never suppresses our dispatch. Pure decision,
/// unit-tested; the injected helper owns observation and resolution.
/// </summary>
public static class ProductSpeechDeferral
{
    /// <summary>
    /// Minimum product-side confidence to treat its result as an action the
    /// product will take. Below this the observation is noted but ignored.
    /// </summary>
    public const double MinProductConfidence = 0.4;

    public static bool ShouldDefer(
        string? ourCommandToken,
        string? productCommandToken,
        double productConfidence)
    {
        if (string.IsNullOrWhiteSpace(ourCommandToken)
            || string.IsNullOrWhiteSpace(productCommandToken))
            return false;
        if (productConfidence < MinProductConfidence)
            return false;
        return string.Equals(
            ourCommandToken.Trim(),
            productCommandToken.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }
}
