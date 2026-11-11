using System;
using System.IO;
using System.Threading;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Startup check for Steam Proton 9.
///
/// Proton 9.0-4f has been observed to bring the menu up without the Start
/// button, so the app cannot be started from that menu. The exact defect is
/// not yet identified: the same button renders under Proton 9 in later runs,
/// and the failure looks like a startup stall (partial menu, dead clicks)
/// rather than a layout difference. Until it is pinned down, this check warns
/// when Proton 9 is detected and points at the working versions.
///
/// The warning is shown from a background thread a few seconds after startup
/// so the app's own window construction is never blocked (the D-INIT-NOBLOCK
/// rule). Every failure path is non-fatal: if the environment cannot be read
/// or the dialog cannot be created (session-0), the app continues.
/// </summary>
public static class ProtonWarning
{
    public const string Caption = "PAIcom - Proton 9 warning";

    private static int _shown;

    /// <summary>
    /// Detect Proton 9 and, if found, arrange the warning popup. Safe to call
    /// once per process; never throws.
    /// </summary>
    public static void CheckAndWarnAtStartup(Action<string>? log)
    {
        try
        {
            var compatData = Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH");
            var tools = Environment.GetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS");
            var direct = Environment.GetEnvironmentVariable("PROTON_VERSION");

            if (string.IsNullOrEmpty(compatData) && string.IsNullOrEmpty(tools) && string.IsNullOrEmpty(direct))
                return; // not running under Proton

            log?.Invoke($"[proton] env PROTON_VERSION='{direct ?? ""}' STEAM_COMPAT_TOOL_PATHS='{tools ?? ""}'");

            if (!TryGetProtonVersion(out var version))
            {
                log?.Invoke("[proton] running under Proton but version could not be read; no warning");
                return;
            }

            log?.Invoke($"[proton] detected version '{version}'");
            if (!IsProton9(version))
                return;

            if (Interlocked.Exchange(ref _shown, 1) != 0)
                return;

            log?.Invoke("[proton] Proton 9 detected - showing Start-button warning");
            var t = new Thread(() =>
            {
                try
                {
                    // Let the app paint its first window, then warn over it.
                    Thread.Sleep(5000);
                    ShowWarning(version);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[proton] warning not shown ({ex.GetType().Name}); continuing");
                }
            })
            {
                IsBackground = true,
                Name = "proton9-warning",
            };
            t.Start();
        }
        catch (Exception ex)
        {
            log?.Invoke($"[proton] check failed ({ex.GetType().Name}); continuing");
        }
    }

    /// <summary>Resolve the running Proton version, if Steam exposed it.</summary>
    public static bool TryGetProtonVersion(out string version)
    {
        version = "";

        var direct = Environment.GetEnvironmentVariable("PROTON_VERSION");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            version = direct!.Trim();
            return true;
        }

        var tools = Environment.GetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS");
        if (!string.IsNullOrWhiteSpace(tools))
        {
            // Fast path: a single tool directory. This also covers Windows
            // drive-letter paths (C:\...) that the ':' split would break;
            // Proton itself only runs on Linux, where ':' is the separator.
            var single = ReadVersionFile(tools!.Trim());
            if (single != null)
            {
                version = single;
                return true;
            }

            foreach (var dir in tools!.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var v = ReadVersionFile(dir.Trim());
                if (v != null)
                {
                    version = v;
                    return true;
                }
            }
        }

        // Proton writes the version it last used into the compatdata directory
        // (for example "9.0-203"). STEAM_COMPAT_DATA_PATH is always present for
        // a Proton-launched process, including a manual `proton run`.
        var compatData = Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH");
        if (!string.IsNullOrWhiteSpace(compatData))
        {
            var v = ReadVersionFile(compatData!.Trim());
            if (v != null)
            {
                version = v;
                return true;
            }
        }

        return false;
    }

    /// <summary>Read a Proton <c>version</c> file (for example <c>proton-9.0-4f</c>).</summary>
    public static string? ReadVersionFile(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory))
                return null;
            var path = Path.Combine(directory, "version");
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True only for Proton major version 9 (e.g. <c>proton-9.0-4f</c>).</summary>
    public static bool IsProton9(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var v = version!.Trim().ToLowerInvariant();
        if (v.StartsWith("proton-", StringComparison.Ordinal))
            v = v.Substring("proton-".Length);

        var dot = v.IndexOf('.');
        if (dot <= 0)
            return false;

        return int.TryParse(v.Substring(0, dot), out var major) && major == 9;
    }

    /// <summary>The exact user-facing text, exposed for tests and reuse.</summary>
    public static string BuildMessage(string version) =>
        "PAIcom detected Steam Proton 9 (" + version + ")." + Environment.NewLine + Environment.NewLine +
        "On Proton 9 the PAIcom menu has been seen to come up without the Start button: " +
        "Help, Discord and Patreon appear and the Start position stays blank. The menu hides " +
        "Start when PAIcom's own startup network check does not complete." + Environment.NewLine + Environment.NewLine +
        "If your menu is missing the Start button: check the internet connection, then switch Proton " +
        "and relaunch - in Steam, open PAIcom's Properties, then Compatibility, and choose Proton " +
        "Experimental (or Proton 8.0). If the Start button is present, nothing needs changing." + Environment.NewLine + Environment.NewLine +
        "This warning appears only when Proton 9 is in use.";

    private static void ShowWarning(string version)
    {
        var message = BuildMessage(version);

        var messageBoxType = Type.GetType("System.Windows.Forms.MessageBox, System.Windows.Forms", throwOnError: true)!;
        var buttonsType = Type.GetType("System.Windows.Forms.MessageBoxButtons, System.Windows.Forms", throwOnError: true)!;
        var iconType = Type.GetType("System.Windows.Forms.MessageBoxIcon, System.Windows.Forms", throwOnError: true)!;
        var ok = Enum.Parse(buttonsType, "OK");
        var icon = Enum.Parse(iconType, "Warning");
        var show = messageBoxType.GetMethod("Show", new[] { typeof(string), typeof(string), buttonsType, iconType });
        if (show == null)
            throw new MissingMethodException("MessageBox.Show overload not found.");
        show.Invoke(null, new object?[] { message, Caption, ok, icon });
    }
}
