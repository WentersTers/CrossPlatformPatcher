using System;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Last-resort speech input through the host's own speech stack (tier 3).
///
/// Order of the input chain: Vosk with the user's model, then Vosk with the
/// default model, then this fallback — Windows speech, only if available.
/// netstandard2.0 has no System.Speech reference, so everything here is
/// reflection against assemblies the host already loaded (the product's own
/// speech path proves they exist on Windows). Non-Windows startup simply
/// yields null: the type is never found, fast and silent.
/// Uses synchronous <c>Recognize()</c> with bounded silence timeouts so no
/// event-delegate variance gymnastics are needed. Never throws: every
/// failure becomes a log line plus null.
/// </summary>
public static class SapiFallbackRecognizer
{
    private const string EngineTypeName = "System.Speech.Recognition.SpeechRecognitionEngine";
    private const string DictationTypeName = "System.Speech.Recognition.DictationGrammar";

    // Cooldown after a failed attempt: repeated wakes while Vosk is down must
    // not pile up slow attempts (each budget is seconds, not minutes).
    private static long _lastFailureUtcTicks;
    private const int FailureCooldownSeconds = 30;

    /// <summary>
    /// Resolve the SAPI engine type from already-loaded assemblies (never
    /// triggers an assembly load itself). Null when unavailable.
    /// </summary>
    internal static Type? FindEngineType()
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? candidate;
                try
                {
                    candidate = assembly.GetType(EngineTypeName, throwOnError: false, ignoreCase: false);
                }
                catch
                {
                    continue;
                }
                if (candidate != null)
                    return candidate;
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>
    /// One recognition attempt, bounded by <paramref name="timeoutMs"/>.
    /// Returns the recognized text, or null when unavailable/empty/failed.
    /// The whole attempt (including teardown: Dispose behind a stuck native
    /// call has been observed to block for minutes) runs on a worker with
    /// an outer wall-clock guard, so the caller never exceeds the budget no
    /// matter where the stack wedges. Abandoned workers are bounded by the
    /// caller's re-entry guard plus the failure cooldown below.
    /// </summary>
    public static string? TryRecognize(int timeoutMs, Action<string> log)
    {
        var cooldownCutoff = DateTime.UtcNow.AddSeconds(-FailureCooldownSeconds).Ticks;
        if (Interlocked.Read(ref _lastFailureUtcTicks) > cooldownCutoff)
        {
            log("[sapi-fallback] cooling down after recent failure; skipping.");
            return null;
        }

        string? text = null;
        try
        {
            var task = System.Threading.Tasks.Task.Run(() => Attempt(timeoutMs, log));
            var budget = timeoutMs + 2000;
            if (!task.Wait(budget))
            {
                NoteFailure();
                log($"[sapi-fallback] attempt exceeded {budget}ms budget; abandoning.");
                return null;
            }
            text = task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            NoteFailure();
            log($"[sapi-fallback] attempt task failed ({ex.GetType().Name}); treating as no-speech.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            log("[sapi-fallback] no speech recognized.");
            return null;
        }
        log($"[sapi-fallback] recognized {text.Length} chars.");
        return text.Trim();
    }

    private static string? Attempt(int timeoutMs, Action<string> log)
    {
        var engineType = FindEngineType();
        if (engineType == null)
        {
            NoteFailure();
            log("[sapi-fallback] unavailable (no System.Speech in host); skipping.");
            return null;
        }

        object? engine = null;
        try
        {
            log("[sapi-fallback] attempting recognition on default input device...");
            engine = Activator.CreateInstance(engineType);
            if (engine == null)
            {
                NoteFailure();
                log("[sapi-fallback] engine construction returned null.");
                return null;
            }

            InvokeBestEffort(engine, engineType, "SetInputToDefaultAudioDevice", null, log);

            var grammarType = engineType.Assembly.GetType(DictationTypeName, throwOnError: false, ignoreCase: false);
            if (grammarType == null)
            {
                NoteFailure();
                log("[sapi-fallback] DictationGrammar type missing; aborting.");
                return null;
            }
            var grammar = Activator.CreateInstance(grammarType);
            InvokeBestEffort(engine, engineType, "LoadGrammar", new[] { grammar }, log);

            // Silence timeouts bound the blocking call below; the outer
            // TryRecognize guard abandons the whole worker if anything
            // (call or teardown) still wedges.
            var per = TimeSpan.FromMilliseconds(Math.Max(500, timeoutMs / 3));
            SetPropertyBestEffort(engine, engineType, "InitialSilenceTimeout", per, log);
            SetPropertyBestEffort(engine, engineType, "BabbleTimeout", per, log);
            SetPropertyBestEffort(engine, engineType, "EndSilenceTimeout", TimeSpan.FromMilliseconds(1000), log);

            var recognize = engineType.GetMethod("Recognize", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (recognize == null)
            {
                NoteFailure();
                log("[sapi-fallback] Recognize() overload missing; aborting.");
                return null;
            }

            object? result = null;
            try
            {
                result = recognize.Invoke(engine, null);
            }
            catch (TargetInvocationException tie)
            {
                log($"[sapi-fallback] Recognize threw {(tie.InnerException ?? tie).GetType().Name}; treating as no-speech.");
                return null;
            }

            var text = ReadResultText(result);
            if (string.IsNullOrWhiteSpace(text))
            {
                log("[sapi-fallback] no speech recognized.");
                return null;
            }
            log($"[sapi-fallback] recognized {text.Length} chars.");
            return text.Trim();
        }
        catch (Exception ex)
        {
            NoteFailure();
            log($"[sapi-fallback] failed ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
        finally
        {
            try
            {
                if (engine is IDisposable disposable)
                    disposable.Dispose();
            }
            catch
            {
            }
        }
    }

    private static void NoteFailure()
    {
        Interlocked.Exchange(ref _lastFailureUtcTicks, DateTime.UtcNow.Ticks);
    }

    internal static string? ReadResultText(object? recognitionResult)
    {
        if (recognitionResult == null)
            return null;
        try
        {
            var textProp = recognitionResult.GetType().GetProperty("Text", BindingFlags.Public | BindingFlags.Instance);
            return textProp?.GetValue(recognitionResult) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void InvokeBestEffort(object? target, Type engineType, string name, object?[]? args, Action<string>? log)
    {
        if (target == null)
            return;
        try
        {
            var method = engineType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            method?.Invoke(target, args);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[sapi-fallback] {name} threw {ex.GetType().Name}; continuing.");
        }
    }

    private static void SetPropertyBestEffort(object? target, Type engineType, string name, object value, Action<string> log)
    {
        if (target == null)
            return;
        try
        {
            var prop = engineType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
                prop.SetValue(target, value);
        }
        catch (Exception ex)
        {
            log($"[sapi-fallback] {name} unsettable ({ex.GetType().Name}); continuing.");
        }
    }
}
