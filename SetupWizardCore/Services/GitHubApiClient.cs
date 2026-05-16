using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SetupWizardCore.Models;

namespace SetupWizardCore.Services
{
    /// <summary>
    /// Handles GitHub API interactions for fetching latest patcher release
    /// </summary>
    public class GitHubApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _repository;

        public GitHubApiClient(string repository = "WentersTers/CrossPlatformPatcher")
        {
            _repository = repository;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SetupWizard/1.0");
        }

        /// <summary>
        /// Fetch latest release metadata
        /// </summary>
        public async Task<GitHubRelease> FetchLatestReleaseAsync()
        {
            var latestUrl = $"https://api.github.com/repos/{_repository}/releases/latest";
            
            try
            {
                var response = await _httpClient.GetAsync(latestUrl);
                
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Fall back to fetching all releases
                    return await FetchLatestReleaseFromListAsync();
                }
                
                response.EnsureSuccessStatusCode();
                
                var json = await response.Content.ReadAsStringAsync();
                var release = JsonSerializer.Deserialize<GitHubRelease>(json, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
                
                return release ?? throw new InvalidOperationException("Failed to deserialize release");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to fetch latest release: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Fetch all releases and return the latest
        /// </summary>
        public async Task<GitHubRelease> FetchLatestReleaseFromListAsync()
        {
            var listUrl = $"https://api.github.com/repos/{_repository}/releases";
            
            var response = await _httpClient.GetAsync(listUrl);
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
            
            return releases?.Count > 0 ? releases[0] : throw new InvalidOperationException("No releases found");
        }

        /// <summary>
        /// Get the appropriate patcher asset for the target platform
        /// </summary>
        public GitHubAsset? GetPatcherAsset(GitHubRelease release, TargetPlatform platform, string architecture)
        {
            var assetName = platform switch
            {
                TargetPlatform.Windows => $"CrossPlatformPatcher-{architecture}.zip",
                TargetPlatform.macOS => $"CrossPlatformPatcher-osx-{architecture}.zip",
                TargetPlatform.Linux => $"CrossPlatformPatcher-linux-{architecture}.zip",
                _ => null
            };

            if (string.IsNullOrEmpty(assetName))
                return null;

            foreach (var asset in release.Assets)
            {
                if (asset.Name.Contains(assetName) || asset.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase))
                {
                    return asset;
                }
            }

            return null;
        }

        /// <summary>
        /// Get the current system architecture
        /// </summary>
        public static string GetCurrentArchitecture()
        {
            return Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => Environment.Is64BitOperatingSystem ? "x64" : "x86",
                PlatformID.Unix => "x64", // Simplified for now
                PlatformID.MacOSX => Environment.Is64BitOperatingSystem ? "x64" : "arm64",
                _ => "x64"
            };
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}