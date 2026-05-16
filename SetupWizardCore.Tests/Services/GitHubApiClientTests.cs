using System;
using System.Threading.Tasks;
using FluentAssertions;
using SetupWizardCore.Models;
using SetupWizardCore.Services;
using Xunit;

namespace SetupWizardCore.Tests.Services
{
    public class GitHubApiClientTests
    {
        [Fact]
        public void Constructor_ShouldInitializeWithDefaultRepository()
        {
            // Act
            var client = new GitHubApiClient();

            // Assert
            client.Should().NotBeNull();
        }

        [Fact]
        public void Constructor_ShouldInitializeWithCustomRepository()
        {
            // Arrange
            var customRepository = "owner/repo";

            // Act
            var client = new GitHubApiClient(customRepository);

            // Assert
            client.Should().NotBeNull();
        }

        [Fact]
        public void GetCurrentArchitecture_ShouldReturnValidArchitecture()
        {
            // Act
            var architecture = GitHubApiClient.GetCurrentArchitecture();

            // Assert
            architecture.Should().NotBeNullOrEmpty();
            architecture.Should().BeOneOf("x64", "x86", "arm64");
        }

        [Fact]
        public void GetPatcherAsset_WithWindowsPlatform_ShouldReturnCorrectAssetName()
        {
            // Arrange
            var client = new GitHubApiClient();
            var release = new GitHubRelease
            {
                TagName = "v1.0.0",
                Assets = new()
                {
                    new GitHubAsset { Name = "CrossPlatformPatcher-x64.zip", BrowserDownloadUrl = "http://example.com/x64.zip", Size = 1000 },
                    new GitHubAsset { Name = "CrossPlatformPatcher-x86.zip", BrowserDownloadUrl = "http://example.com/x86.zip", Size = 1000 }
                }
            };

            // Act
            var asset = client.GetPatcherAsset(release, TargetPlatform.Windows, "x64");

            // Assert
            asset.Should().NotBeNull();
            asset!.Name.Should().Be("CrossPlatformPatcher-x64.zip");
        }

        [Fact]
        public void GetPatcherAsset_WithMacPlatform_ShouldReturnCorrectAssetName()
        {
            // Arrange
            var client = new GitHubApiClient();
            var release = new GitHubRelease
            {
                TagName = "v1.0.0",
                Assets = new()
                {
                    new GitHubAsset { Name = "CrossPlatformPatcher-osx-x64.zip", BrowserDownloadUrl = "http://example.com/osx-x64.zip", Size = 1000 },
                    new GitHubAsset { Name = "CrossPlatformPatcher-osx-arm64.zip", BrowserDownloadUrl = "http://example.com/osx-arm64.zip", Size = 1000 }
                }
            };

            // Act
            var asset = client.GetPatcherAsset(release, TargetPlatform.macOS, "x64");

            // Assert
            asset.Should().NotBeNull();
            asset!.Name.Should().Be("CrossPlatformPatcher-osx-x64.zip");
        }

        [Fact]
        public void GetPatcherAsset_WithLinuxPlatform_ShouldReturnCorrectAssetName()
        {
            // Arrange
            var client = new GitHubApiClient();
            var release = new GitHubRelease
            {
                TagName = "v1.0.0",
                Assets = new()
                {
                    new GitHubAsset { Name = "CrossPlatformPatcher-linux-x64.zip", BrowserDownloadUrl = "http://example.com/linux-x64.zip", Size = 1000 }
                }
            };

            // Act
            var asset = client.GetPatcherAsset(release, TargetPlatform.Linux, "x64");

            // Assert
            asset.Should().NotBeNull();
            asset!.Name.Should().Be("CrossPlatformPatcher-linux-x64.zip");
        }

        [Fact]
        public void GetPatcherAsset_WithNoMatchingAsset_ShouldReturnNull()
        {
            // Arrange
            var client = new GitHubApiClient();
            var release = new GitHubRelease
            {
                TagName = "v1.0.0",
                Assets = new()
                {
                    new GitHubAsset { Name = "OtherAsset.zip", BrowserDownloadUrl = "http://example.com/other.zip", Size = 1000 }
                }
            };

            // Act
            var asset = client.GetPatcherAsset(release, TargetPlatform.Windows, "x64");

            // Assert
            asset.Should().BeNull();
        }
    }
}