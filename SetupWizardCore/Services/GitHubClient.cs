using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SetupWizardCore.Services;

/// <summary>
/// Handles GitHub API interactions for fetching latest patcher release.
/// Port of Swift APIClient actor.
/// </summary>
public class GitHubClient
{
    public class GitHubApiException : Exception
    {
        public int? StatusCode { get; }
        public string? ApiMessage { get; }

        public GitHubApiException(string message, int? statusCode = null, string? apiMessage = null)
            : base(message)
        {
            StatusCode = statusCode;
            ApiMessage = apiMessage;
        }
    }

    public class AssetNotFoundException : GitHubApiException
    {
        public string AssetName { get; }
        public AssetNotFoundException(string assetName)
            : base($"Asset not found: {assetName}")
        {
            AssetName = assetName;
        }
    }

    public class NoReleaseException : GitHubApiException
    {
        public NoReleaseException()
            : base("No releases found")
        {
        }
    }

    public class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";

        [JsonPropertyName("size")]
        public int Size { get; set; }
    }

    public class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    private readonly HttpClient _httpClient;
    private readonly string _repository;

    public GitHubClient(string repository = "WentersTers/CrossPlatformPatcher")
    {
        _repository = repository;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CrossPlatformPatcher-SetupWizard/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Fetch latest release metadata from GitHub.
    /// Port of Swift fetchLatestRelease().
    /// </summary>
    public async Task<GitHubRelease> FetchLatestReleaseAsync()
    {
        var latestUrl = $"https://api.github.com/repos/{_repository}/releases/latest";
        SetupWizardLogger.Instance.Debug($"Fetching latest release from: {latestUrl}");

        try
        {
            var response = await _httpClient.GetAsync(latestUrl);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                SetupWizardLogger.Instance.Debug("No latest release found, falling back to list all releases endpoint");
                return await FetchLatestReleaseFromListAsync();
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await TryReadErrorMessageAsync(response);
                throw new GitHubApiException(
                    $"GitHub API error {(int)response.StatusCode}: {errorBody ?? "Unknown error"}",
                    (int)response.StatusCode,
                    errorBody);
            }

            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>();
            if (release == null)
                throw new GitHubApiException("Failed to deserialize GitHub release response");

            SetupWizardLogger.Instance.Debug($"Fetched release: {release.TagName} with {release.Assets.Count} assets");
            return release;
        }
        catch (GitHubApiException) { throw; }
        catch (HttpRequestException ex)
        {
            SetupWizardLogger.Instance.Error($"Network error fetching release: {ex.Message}");
            throw new GitHubApiException($"Network error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            SetupWizardLogger.Instance.Error($"Failed to decode release: {ex.Message}");
            throw new GitHubApiException($"Failed to parse GitHub response: {ex.Message}");
        }
    }

    /// <summary>
    /// Fallback: fetch all releases and return the newest one.
    /// Port of Swift fetchLatestReleaseFromList().
    /// </summary>
    private async Task<GitHubRelease> FetchLatestReleaseFromListAsync()
    {
        var allReleasesUrl = $"https://api.github.com/repos/{_repository}/releases";
        SetupWizardLogger.Instance.Debug($"Fetching all releases from: {allReleasesUrl}");

        try
        {
            var response = await _httpClient.GetAsync(allReleasesUrl);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await TryReadErrorMessageAsync(response);
                throw new GitHubApiException(
                    $"GitHub API error {(int)response.StatusCode}: {errorBody ?? "Unknown error"}",
                    (int)response.StatusCode,
                    errorBody);
            }

            var releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>();
            if (releases == null || releases.Count == 0)
                throw new NoReleaseException();

            SetupWizardLogger.Instance.Debug($"Found {releases.Count} releases, using newest: {releases[0].TagName}");
            return releases[0];
        }
        catch (GitHubApiException) { throw; }
        catch (HttpRequestException ex)
        {
            throw new GitHubApiException($"Network error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException($"Failed to parse releases list: {ex.Message}");
        }
    }

    /// <summary>
    /// Get the patcher asset URL for the current OS + architecture.
    /// Port of Swift getPatcherAssetURL().
    /// </summary>
    public async Task<(Uri url, string name, int size)> GetPatcherAssetUrlAsync()
    {
        var release = await FetchLatestReleaseAsync();
        if (release.Assets.Count == 0)
            throw new NoReleaseException();

        var assetName = GetAssetNameForCurrentPlatform();

        SetupWizardLogger.Instance.Debug($"Looking for asset: {assetName}");

        var asset = release.Assets.FirstOrDefault(a => a.Name == assetName);
        if (asset == null)
            throw new AssetNotFoundException(assetName);

        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url))
            throw new GitHubApiException($"Invalid asset URL: {asset.BrowserDownloadUrl}");

        SetupWizardLogger.Instance.Debug($"Found asset: {asset.Name} ({asset.Size} bytes)");
        return (url, asset.Name, asset.Size);
    }

    /// <summary>
    /// Download a file from URL to a local path with progress reporting.
    /// Port of Swift download().
    /// </summary>
    public async Task DownloadAsync(Uri url, string destinationPath, IProgress<double>? progress = null)
    {
        SetupWizardLogger.Instance.Debug($"Downloading from: {url} to: {destinationPath}");

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

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    var pct = (double)totalRead / totalBytes;
                    progress.Report(pct);
                }
            }

            progress?.Report(1.0);
            SetupWizardLogger.Instance.Debug($"Download completed: {destinationPath}");
        }
        catch (HttpRequestException ex)
        {
            SetupWizardLogger.Instance.Error($"Download failed: {ex.Message}");
            throw new GitHubApiException($"Download failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Determine the patcher asset name based on the current OS and architecture.
    /// </summary>
    private static string GetAssetNameForCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "CrossPlatformPatcher-W-x64";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "CrossPlatformPatcher-L-x64";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "CrossPlatformPatcher-M-Arm"
                : "CrossPlatformPatcher-M-x64";
        }

        // Fallback
        return "CrossPlatformPatcher-L-x64";
    }

    private static async Task<string?> TryReadErrorMessageAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
