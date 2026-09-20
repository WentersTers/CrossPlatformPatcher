using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// First-run model acquisition for the offline speech path (v0.1.2).
///
/// Search precedence is owned by <c>VoskSpeechRecognizer</c> and always wins:
/// an already-present model (user-placed, bundled, or <c>PAICOM_VOSK_MODEL_PATH</c>)
/// is never re-downloaded. This type only runs when search found nothing.
/// The exact model all censuses measured; override/disabled via environment.
/// </summary>
public static class VoskModelDownloader
{
    public const string ModelDirName = "vosk-model-small-en-us-0.15";
    public const string DefaultModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";
    public const string UrlOverrideVariable = "PAICOM_VOSK_MODEL_URL";

    /// <summary>
    /// Opt-out for flatten-on-resolve: when set to <c>1</c>/<c>true</c>, a model
    /// found in a subdirectory is used in place (legacy behavior) instead of
    /// being materialized flat into the searched root.
    /// </summary>
    public const string FlattenOptOutVariable = "PAICOM_VOSK_NO_FLATTEN";

    /// <summary>
    /// Startup settle before any model fetch may run (seconds since OWW
    /// load). First-use <c>HttpClient</c> during the host's own network-stack
    /// init poisons the process-global state (the boot-crash class: a later
    /// phone-home dies with an SSL/TLS failure it did not cause). The fetch
    /// therefore waits until the host stack is long settled; SAPI covers
    /// every wake until then. Pure decision in
    /// <see cref="ShouldAttemptFetch"/>.
    /// </summary>
    public const long FetchSettleSeconds = 120;

    /// <summary>
    /// Pure fetch gate: attempt only once ever, and only once the startup
    /// has settled. Never throws.
    /// </summary>
    public static bool ShouldAttemptFetch(bool alreadyAttempted, long startupAgeSeconds)
    {
        try
        {
            if (alreadyAttempted)
                return false;
            return startupAgeSeconds >= FetchSettleSeconds;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolve the download URL, or null when acquisition is disabled
    /// (<c>PAICOM_VOSK_MODEL_URL</c> set-but-empty) or misconfigured.
    /// </summary>
    public static string? ResolveDownloadUrl()
    {
        var raw = Environment.GetEnvironmentVariable(UrlOverrideVariable);
        if (raw != null && raw.Trim().Length == 0)
            return null;
        var url = string.IsNullOrWhiteSpace(raw) ? DefaultModelUrl : raw.Trim();
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return null;
        return url;
    }

    /// <summary>Home models root: <c>%USERPROFILE%\.paicom\models</c>.</summary>
    public static string HomeModelsRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".paicom", "models");
    }

    /// <summary>
    /// Guard a zip entry path: returns the safe destination under
    /// <paramref name="destDir"/>, or null for absolute/traversal entries.
    /// </summary>
    public static string? SanitizeEntryPath(string destDir, string? entryName)
    {
        if (string.IsNullOrEmpty(entryName))
            return null;
        var slashed = entryName.Replace('\\', '/');
        if (slashed.StartsWith("/", StringComparison.Ordinal))
            return null;
        var parts = slashed.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part == "." || part == "..")
                return null;
        }
        if (parts.Length == 0)
            return null;
        var combined = destDir;
        foreach (var part in parts)
            combined = Path.Combine(combined, part);
        string fullDest;
        string fullRoot;
        try
        {
            fullDest = Path.GetFullPath(combined);
            fullRoot = Path.GetFullPath(destDir);
        }
        catch
        {
            return null;
        }
        if (!fullDest.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullDest, fullRoot, StringComparison.OrdinalIgnoreCase))
            return null;
        return fullDest;
    }

    /// <summary>
    /// Download the model zip and extract it under <paramref name="destRoot"/>
    /// (creates <c>destRoot/ModelDirName</c>). Returns the model directory when
    /// it validates (am/conf/graph present), else null. Best-effort cleanup
    /// of the partial zip on failure. All failures are logged, never thrown.
    /// </summary>
    public static string? DownloadAndExtract(string url, string destRoot, Action<string> log)
    {
        var destDir = Path.Combine(destRoot, ModelDirName);
        try
        {
            if (LooksLikeModelDirectory(destDir))
            {
                log("[vosk-speech] model fetch: already present, skipping download.");
                return destDir;
            }
        }
        catch (Exception ex)
        {
            log($"[vosk-speech] model fetch pre-check failed ({ex.GetType().Name}); continuing.");
        }

        var tmpZip = Path.Combine(destRoot, "." + ModelDirName + ".zip.part");
        try
        {
            Directory.CreateDirectory(destRoot);
            log($"[vosk-speech] model fetch: downloading {url} ...");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var response = http.GetAsync(url).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using (var remote = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var local = new FileStream(tmpZip, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    remote.CopyTo(local);
                }
            }
            var bytes = new FileInfo(tmpZip).Length;
            log($"[vosk-speech] model fetch: downloaded {bytes} bytes, extracting ...");

            using (var archive = new ZipArchive(new FileStream(tmpZip, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read))
            {
                ExtractArchive(archive, destRoot, log);
            }

            var resolved = FindModelDirectory(destRoot);
            if (resolved != null)
            {
                log($"[vosk-speech] model fetch: ready at {resolved}.");
                return resolved;
            }
            log("[vosk-speech] model fetch: extracted but no valid model directory found.");
            return null;
        }
        catch (Exception ex)
        {
            log($"[vosk-speech] model fetch failed ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
        }
    }

    internal static void ExtractArchive(ZipArchive archive, string destRoot, Action<string> log)
    {
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                continue;
            var safe = SanitizeEntryPath(destRoot, entry.FullName);
            if (safe == null)
            {
                log($"[vosk-speech] model fetch: skipping unsafe entry '{entry.FullName}'.");
                continue;
            }
            var parent = Path.GetDirectoryName(safe);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            using (var src = entry.Open())
            using (var dst = new FileStream(safe, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                src.CopyTo(dst);
            }
        }
    }

    internal static bool LooksLikeModelDirectory(string path)
    {
        return Directory.Exists(Path.Combine(path, "am")) &&
               Directory.Exists(Path.Combine(path, "conf")) &&
               Directory.Exists(Path.Combine(path, "graph"));
    }

    internal static string? FindModelDirectory(string root)
    {
        if (!Directory.Exists(root))
            return null;
        if (LooksLikeModelDirectory(root))
            return root;
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return null;
        }
        Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (LooksLikeModelDirectory(directory))
                return directory;
        }
        return null;
    }

    /// <summary>
    /// Flatten-on-resolve: when the resolved model lives in a subdirectory of
    /// the searched <paramref name="root"/>, copy its tree up into
    /// <paramref name="root"/> so the model runs flat from the root itself,
    /// and return <paramref name="root"/>. Copy (never move): the user's
    /// original subdirectory stays intact. Already-flat roots are returned
    /// untouched (no-op). Never throws: every failure (opt-out, read-only
    /// root, copy error, post-copy validation) falls back to the in-place
    /// <paramref name="resolvedModel"/> path, i.e. never worse than before.
    /// </summary>
    public static string EnsureFlatModelDirectory(string root, string resolvedModel, Action<string>? log)
    {
        void Log(string message)
        {
            try { log?.Invoke(message); } catch { }
        }

        try
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(resolvedModel))
                return resolvedModel;
            string fullRoot;
            string fullResolved;
            try
            {
                fullRoot = Path.GetFullPath(root);
                fullResolved = Path.GetFullPath(resolvedModel);
            }
            catch
            {
                return resolvedModel;
            }
            if (string.Equals(fullRoot, fullResolved, StringComparison.OrdinalIgnoreCase))
                return resolvedModel;
            if (!fullResolved.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Log($"[vosk-speech] model flatten: resolved path is outside the search root; using in place: {resolvedModel}");
                return resolvedModel;
            }
            if (LooksLikeModelDirectory(fullRoot))
                return fullRoot;
            if (IsFlattenOptedOut())
            {
                Log("[vosk-speech] model flatten: skipped by PAICOM_VOSK_NO_FLATTEN; using in place: " + resolvedModel);
                return resolvedModel;
            }
            if (!LooksLikeModelDirectory(fullResolved))
                return resolvedModel;

            Log($"[vosk-speech] model flatten: materializing '{fullResolved}' flat into '{fullRoot}' ...");
            var bytesCopied = CopyDirectoryTree(fullResolved, fullRoot);
            if (!LooksLikeModelDirectory(fullRoot))
            {
                Log("[vosk-speech] model flatten: root still not a valid model after copy; using in place: " + resolvedModel);
                return resolvedModel;
            }
            Log($"[vosk-speech] model flatten: ready at {fullRoot} ({bytesCopied} bytes materialized; originals kept in place).");
            return fullRoot;
        }
        catch (Exception ex)
        {
            try { log?.Invoke($"[vosk-speech] model flatten failed ({ex.GetType().Name}); using in place: {resolvedModel}"); } catch { }
            return resolvedModel;
        }
    }

    internal static bool IsFlattenOptedOut()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable(FlattenOptOutVariable);
            return string.Equals(raw?.Trim(), "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static long CopyDirectoryTree(string sourceDir, string destDir)
    {
        long bytesCopied = 0;
        Directory.CreateDirectory(destDir);
        // netstandard2.0 has no Path.GetRelativePath: derive it manually.
        var fullSource = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var fullFile = Path.GetFullPath(file);
            var relative = fullFile.StartsWith(fullSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? fullFile.Substring(fullSource.Length + 1)
                : Path.GetFileName(fullFile);
            var dest = Path.Combine(destDir, relative);
            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.Copy(file, dest, overwrite: true);
            try { bytesCopied += new FileInfo(dest).Length; } catch { }
        }
        return bytesCopied;
    }
}
