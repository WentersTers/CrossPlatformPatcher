using System.IO.Compression;

namespace SetupWizardCore.Services;

/// <summary>
/// Handles downloading and extracting Vosk models.
/// Port of Swift VoskModelDownloader actor.
/// Uses System.IO.Compression.ZipFile instead of /usr/bin/unzip for cross-platform support.
/// </summary>
public class ModelDownloader
{
    public class DownloadError : Exception
    {
        public DownloadError(string message) : base(message) { }
        public DownloadError(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Vosk model options matching the Swift VoskModel enum.
    /// </summary>
    public class VoskModelInfo
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string DownloadUrl { get; }

        private VoskModelInfo(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
            DownloadUrl = $"https://alphacephei.com/vosk/models/{id}.zip";
        }

        public static readonly VoskModelInfo SmallEnUs = new(
            "vosk-model-small-en-us-0.15",
            "vosk-model-small-en-us-0.15 (fast, lightweight)");

        public static readonly VoskModelInfo EnUs022 = new(
            "vosk-model-en-us-0.22",
            "vosk-model-en-us-0.22 (balanced)");

        public static readonly VoskModelInfo EnUs022Lgraph = new(
            "vosk-model-en-us-0.22-lgraph",
            "vosk-model-en-us-0.22-lgraph (large graph)");

        public static readonly VoskModelInfo EnUs042Gigaspeech = new(
            "vosk-model-en-us-0.42-gigaspeech",
            "vosk-model-en-us-0.42-gigaspeech (large, accurate)");

        public static IReadOnlyList<VoskModelInfo> All { get; } = new[]
        {
            SmallEnUs,
            EnUs022,
            EnUs022Lgraph,
            EnUs042Gigaspeech
        };
    }

    private readonly HttpClient _httpClient;

    public ModelDownloader()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CrossPlatformPatcher-SetupWizard/1.0");
        _httpClient.Timeout = TimeSpan.FromHours(1); // 1 hour for large models
    }

    /// <summary>
    /// Download a Vosk model from the Alphacephei CDN and extract to the target directory.
    /// Port of Swift downloadModel().
    /// </summary>
    public async Task DownloadModelAsync(
        VoskModelInfo model,
        string targetDir,
        IProgress<double>? progress = null)
    {
        var tempDir = Path.GetTempPath();
        var zipName = $"{model.Id}.zip";
        var tempZipPath = Path.Combine(tempDir, zipName);

        Console.WriteLine($"[ModelDownloader] Starting download: {model.Id}");
        Console.WriteLine($"[ModelDownloader] URL: {model.DownloadUrl}");
        Console.WriteLine($"[ModelDownloader] Temp path: {tempZipPath}");
        Console.WriteLine($"[ModelDownloader] Target dir: {targetDir}");

        // Download the model zip
        await DownloadFileAsync(model.DownloadUrl, tempZipPath, progress);

        Console.WriteLine($"[ModelDownloader] Download completed: {tempZipPath}");

        try
        {
            // Ensure target directory exists
            Directory.CreateDirectory(targetDir);

            Console.WriteLine($"[ModelDownloader] Target directory created: {targetDir}");

            // Extract the zip using System.IO.Compression (cross-platform)
            var extractDir = Path.Combine(targetDir, model.Id);
            ExtractZip(tempZipPath, extractDir);

            Console.WriteLine("[ModelDownloader] Extraction completed successfully");
        }
        finally
        {
            // Cleanup temp file
            try { File.Delete(tempZipPath); }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Download a custom model from a URL or extract a local file.
    /// Port of Swift downloadCustomModel().
    /// </summary>
    public async Task DownloadCustomModelAsync(
        string pathOrUrl,
        string modelName,
        string targetDir,
        IProgress<double>? progress = null)
    {
        if (pathOrUrl.StartsWith("http://") || pathOrUrl.StartsWith("https://"))
        {
            // It's a URL
            var tempDir = Path.GetTempPath();
            var zipName = $"{modelName}.zip";
            var tempZipPath = Path.Combine(tempDir, zipName);

            await DownloadFileAsync(pathOrUrl, tempZipPath, progress);

            try
            {
                Directory.CreateDirectory(targetDir);
                var extractDir = Path.Combine(targetDir, modelName);
                ExtractZip(tempZipPath, extractDir);
            }
            finally
            {
                try { File.Delete(tempZipPath); }
                catch { /* ignore */ }
            }
        }
        else if (File.Exists(pathOrUrl))
        {
            // Local file - extract directly
            Directory.CreateDirectory(targetDir);
            var extractDir = Path.Combine(targetDir, modelName);
            ExtractZip(pathOrUrl, extractDir);
        }
        else
        {
            throw new DownloadError($"File not found: {pathOrUrl}");
        }
    }

    /// <summary>
    /// Download a file from URL to a local path with progress reporting.
    /// </summary>
    private async Task DownloadFileAsync(string url, string destinationPath, IProgress<double>? progress)
    {
        Console.WriteLine($"[ModelDownloader] Starting download from: {url}");

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            var lastProgressReport = DateTime.MinValue;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    var pct = (double)totalRead / totalBytes;
                    var now = DateTime.UtcNow;
                    // Throttle progress updates to avoid excessive UI updates
                    if ((now - lastProgressReport).TotalMilliseconds > 500)
                    {
                        progress.Report(pct);
                        var mbWritten = totalRead / (1024.0 * 1024.0);
                        var mbTotal = totalBytes / (1024.0 * 1024.0);
                        Console.WriteLine($"[ModelDownloader] Download progress: {pct * 100:F0}% ({mbWritten:F1}/{mbTotal:F1} MB)");
                        lastProgressReport = now;
                    }
                }
            }

            progress?.Report(1.0);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[ModelDownloader] Download error: {ex.Message}");
            throw new DownloadError($"Network error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extract a zip archive to a target directory.
    /// Uses System.IO.Compression.ZipFile instead of /usr/bin/unzip for cross-platform support.
    /// </summary>
    private void ExtractZip(string zipPath, string extractDir)
    {
        Console.WriteLine($"[ModelDownloader] Extracting: {zipPath} to {extractDir}");

        Directory.CreateDirectory(extractDir);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            // Verify extraction worked by checking for files
            var extractedFiles = Directory.GetFileSystemEntries(extractDir);
            Console.WriteLine($"[ModelDownloader] Files in extract directory: {string.Join(", ", extractedFiles)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelDownloader] Extraction error: {ex.Message}");
            throw new DownloadError($"Failed to extract model: {ex.Message}", ex);
        }
    }
}
