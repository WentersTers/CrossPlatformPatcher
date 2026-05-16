using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SetupWizardCore.Models
{
    /// <summary>
    /// Represents a GitHub release asset
    /// </summary>
    public class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    /// <summary>
    /// Represents a GitHub release
    /// </summary>
    public class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = new List<GitHubAsset>();
    }

    /// <summary>
    /// Represents available Vosk models
    /// </summary>
    public class VoskModel
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long Size { get; set; }

        public static List<VoskModel> GetAvailableModels()
        {
            return new List<VoskModel>
            {
                new VoskModel
                {
                    Id = "vosk-model-small-en-us-0.15",
                    DisplayName = "vosk-model-small-en-us-0.15 (fast, lightweight)",
                    DownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip",
                    FileName = "vosk-model-small-en-us-0.15.zip"
                },
                new VoskModel
                {
                    Id = "vosk-model-en-us-0.22",
                    DisplayName = "vosk-model-en-us-0.22 (balanced)",
                    DownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-en-us-0.22.zip",
                    FileName = "vosk-model-en-us-0.22.zip"
                },
                new VoskModel
                {
                    Id = "vosk-model-en-us-0.22-lgraph",
                    DisplayName = "vosk-model-en-us-0.22-lgraph (large graph)",
                    DownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-en-us-0.22-lgraph.zip",
                    FileName = "vosk-model-en-us-0.22-lgraph.zip"
                },
                new VoskModel
                {
                    Id = "vosk-model-en-us-0.42-gigaspeech",
                    DisplayName = "vosk-model-en-us-0.42-gigaspeech (large, accurate)",
                    DownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-en-us-0.42-gigaspeech.zip",
                    FileName = "vosk-model-en-us-0.42-gigaspeech.zip"
                }
            };
        }
    }
}