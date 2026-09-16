using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Tamper-evident tripwire for patched assemblies (opt-in peace-of-mind signal,
/// not a security boundary).
///
/// Patch-time side (<see cref="Core.TripwireBaseline"/>) writes a
/// <c>&lt;exe&gt;.tripwire-baseline</c> file holding the SHA-256 of the patched
/// output. This side runs once per install inside the patched process: it
/// compares the live executable against the baseline and, on first boot only,
/// shows a dismissable popup. Semantics: warn-and-continue always; a
/// non-interactive start (session-0 MessageBox throw) defers instead of
/// consuming the one-time notice; deleting the <c>.tripwire-seen</c> flag
/// re-arms. Every failure path is non-fatal by construction.
/// </summary>
public static class TripwireCheck
{
    public const string BaselineSuffix = ".tripwire-baseline";
    public const string SeenSuffix = ".tripwire-seen";

    public enum TripwireVerdict
    {
        UnknownBaseline,
        Match,
        Mismatch,
    }

    /// <summary>Pure verdict computation; unit-tested via the test project.</summary>
    public static TripwireVerdict Evaluate(string? currentHash, string? baselineHash)
    {
        var cleanBaseline = CleanHash(baselineHash);
        if (cleanBaseline == null)
            return TripwireVerdict.UnknownBaseline;
        var cleanCurrent = CleanHash(currentHash);
        if (cleanCurrent == null)
            return TripwireVerdict.Mismatch;
        return string.Equals(cleanCurrent, cleanBaseline, StringComparison.OrdinalIgnoreCase)
            ? TripwireVerdict.Match
            : TripwireVerdict.Mismatch;
    }

    /// <summary>
    /// Parse a baseline file body. Format (written by the patcher):
    /// line 1 <c>sha256:&lt;HEX&gt;</c>, later lines ignored.
    /// </summary>
    public static bool TryParseBaseline(string? text, out string hash)
    {
        hash = "";
        if (string.IsNullOrEmpty(text))
            return false;
        var first = text!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (first.Length == 0)
            return false;
        var line = first[0].Trim();
        const string prefix = "sha256:";
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var clean = CleanHash(line.Substring(prefix.Length));
        if (clean == null)
            return false;
        hash = clean;
        return true;
    }

    /// <summary>SHA-256 of a file, streamed with read/write sharing (the host
    /// exe may be running). Null when unreadable.</summary>
    public static string? ComputeFileHash(string path)
    {
        try
        {
            using var sha = SHA256.Create();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = sha.ComputeHash(fs);
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Entry point for the host process: resolve exe path, run once.</summary>
    public static void RunOnceForCurrentProcess(Action<string> log)
    {
        string? exePath = null;
        try
        {
            using var proc = Process.GetCurrentProcess();
            exePath = proc.MainModule?.FileName;
        }
        catch
        {
            exePath = null;
        }
        if (string.IsNullOrEmpty(exePath))
        {
            log("[tripwire] skipped: host executable path unknown.");
            return;
        }
        RunOnce(exePath!, log);
    }

    /// <summary>
    /// One-time check for <paramref name="exePath"/>. Writes the seen-flag
    /// only after the notice was actually shown.
    /// </summary>
    public static void RunOnce(string exePath, Action<string> log)
    {
        var seenPath = exePath + SeenSuffix;
        try
        {
            if (File.Exists(seenPath))
                return;
        }
        catch
        {
            return;
        }

        string? baselineHash = null;
        try
        {
            var baselinePath = exePath + BaselineSuffix;
            if (File.Exists(baselinePath))
                TryParseBaseline(File.ReadAllText(baselinePath), out baselineHash);
        }
        catch (Exception ex)
        {
            log($"[tripwire] baseline unreadable ({ex.GetType().Name}); continuing.");
        }

        var currentHash = ComputeFileHash(exePath);
        var verdict = Evaluate(currentHash, baselineHash);
        log($"[tripwire] verdict={verdict} (baseline {(baselineHash == null ? "absent" : "present")}).");

        try
        {
            ShowPopup(verdict);
        }
        catch (Exception ex)
        {
            // Non-interactive start (session-0) lands here: defer, do NOT
            // consume the one-time notice.
            log($"[tripwire] notice deferred ({ex.GetType().Name}); continuing.");
            return;
        }

        try
        {
            File.WriteAllText(seenPath, "seen-utc:" + DateTime.UtcNow.ToString("o") + Environment.NewLine);
        }
        catch (Exception ex)
        {
            log($"[tripwire] seen-flag unwritable ({ex.GetType().Name}); notice may repeat.");
        }
    }

    private static string? CleanHash(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var clean = raw!.Trim();
        if (clean.Length != 64)
            return null;
        foreach (var ch in clean)
        {
            var hex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
            if (!hex)
                return null;
        }
        return clean;
    }

    /// <summary>
    /// WinForms MessageBox via reflection: netstandard2.0 has no WinForms
    /// reference, but the host app is WinForms-proven (TextBox+Button path).
    /// Throws on non-interactive desktops by design (caller defers).
    /// </summary>
    private static void ShowPopup(TripwireVerdict verdict)
    {
        var message = verdict switch
        {
            TripwireVerdict.Match =>
                "PAIcom integrity check: this copy matches its patch-time baseline." + Environment.NewLine +
                "Voice features start normally. This notice appears once and can be re-armed by deleting the .tripwire-seen file.",
            TripwireVerdict.Mismatch =>
                "PAIcom integrity check: this file changed since it was patched (hash mismatch)." + Environment.NewLine +
                "Voice features continue anyway. If the change was unexpected, re-patch from a clean copy.",
            _ =>
                "PAIcom integrity check: no patch-time baseline was found next to this file, so integrity is unknown." + Environment.NewLine +
                "Voice features continue anyway.",
        };
        var caption = verdict switch
        {
            TripwireVerdict.Match => "PAIcom - integrity OK",
            TripwireVerdict.Mismatch => "PAIcom - file changed",
            _ => "PAIcom - integrity unknown",
        };
        var iconName = verdict == TripwireVerdict.Mismatch ? "Warning" : "Information";

        var messageBoxType = Type.GetType("System.Windows.Forms.MessageBox, System.Windows.Forms", throwOnError: true)!;
        var buttonsType = Type.GetType("System.Windows.Forms.MessageBoxButtons, System.Windows.Forms", throwOnError: true)!;
        var iconType = Type.GetType("System.Windows.Forms.MessageBoxIcon, System.Windows.Forms", throwOnError: true)!;
        var ok = Enum.Parse(buttonsType, "OK");
        var icon = Enum.Parse(iconType, iconName);
        var show = messageBoxType.GetMethod("Show", new[] { typeof(string), typeof(string), buttonsType, iconType });
        if (show == null)
            throw new MissingMethodException("MessageBox.Show overload not found.");
        show.Invoke(null, new object?[] { message, caption, ok, icon });
    }
}
