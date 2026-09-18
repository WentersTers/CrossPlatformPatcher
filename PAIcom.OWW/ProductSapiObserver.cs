using System;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Product-SAPI observation + deferral decision (v0.1.2 insurance and
/// instrumentation behind the suppression trampoline).
/// The compat hook forwards whatever the product audio callback delivers
/// into <c>EnqueueAudio</c>, including SAPI result args. This unit reads
/// those (reflection-only: System.Speech may be absent on Linux/Wine, so it
/// is never linked statically) and decides whether the bridge should stand
/// down for an utterance the product demonstrably heard the same way.
/// Deferral is dormant by default (see PAICOM_PRODUCT_DEFER): with the
/// trampoline in place the product never executes, so an observed result is
/// only a receipt, never a handoff. Shared-sourced into the test project.
/// Never throws.
/// </summary>
public static class ProductSapiObserver
{
    public const string RecognizedArgsFullName = "System.Speech.Recognition.SpeechRecognizedEventArgs";
    public const string RecognitionResultFullName = "System.Speech.Recognition.RecognitionResult";

    /// <summary>How long an observed product result stays eligible for deferral.</summary>
    public const long ObservationFreshnessMs = 8000;

    /// <summary>
    /// Tries to read a result text + confidence out of a product-callback
    /// payload. Accepts a RecognitionResult or a SpeechRecognizedEventArgs
    /// (via its Result). Anything else returns false.
    /// </summary>
    public static bool TryExtractResult(object? arg, out string? text, out float confidence)
    {
        text = null;
        confidence = 0f;
        try
        {
            if (arg == null)
                return false;

            var type = arg.GetType();
            var fullName = type.FullName ?? string.Empty;

            object? carrier = null;
            if (string.Equals(fullName, RecognitionResultFullName, StringComparison.Ordinal))
            {
                carrier = arg;
            }
            else if (string.Equals(fullName, RecognizedArgsFullName, StringComparison.Ordinal))
            {
                carrier = type.GetProperty("Result")?.GetValue(arg);
            }
            else
            {
                return false;
            }

            if (carrier == null)
                return false;

            var carrierType = carrier.GetType();
            var textValue = carrierType.GetProperty("Text")?.GetValue(carrier) as string;
            if (string.IsNullOrWhiteSpace(textValue))
                return false;

            var confidenceValue = carrierType.GetProperty("Confidence")?.GetValue(carrier);
            if (confidenceValue is float f)
                confidence = f;
            else if (confidenceValue is double d)
                confidence = (float)d;

            text = textValue;
            return true;
        }
        catch
        {
            text = null;
            confidence = 0f;
            return false;
        }
    }

    /// <summary>
    /// Pure deferral decision: stand down only when a fresh product
    /// observation textually agrees with one of the bridge's own outgoing
    /// command texts (match phrase, dispatch phrase, or token).
    /// </summary>
    public static bool ShouldDeferToProduct(
        string?[]? bridgeCandidates,
        string? observedText,
        long observedTicks,
        long nowTicks,
        out string? reason)
    {
        reason = null;
        try
        {
            if (bridgeCandidates == null || bridgeCandidates.Length == 0)
                return false;
            if (string.IsNullOrWhiteSpace(observedText))
                return false;
            if (observedTicks <= 0 || nowTicks < observedTicks)
                return false;

            var ageMs = (nowTicks - observedTicks) / (double)TimeSpan.TicksPerMillisecond;
            if (ageMs > ObservationFreshnessMs)
                return false;

            var observed = observedText.Trim();
            foreach (var candidate in bridgeCandidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;
                if (string.Equals(candidate.Trim(), observed, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"product heard '{observed}' {ageMs:F0}ms ago; texts agree";
                    return true;
                }
            }

            return false;
        }
        catch
        {
            reason = null;
            return false;
        }
    }
}
