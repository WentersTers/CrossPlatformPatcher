using System.Linq;
using FluentAssertions;
using SetupWizardCore.Models;
using Xunit;

namespace SetupWizardCore.Tests.Models
{
    public class VoskModelTests
    {
        [Fact]
        public void GetAvailableModels_ShouldReturnAllModels()
        {
            // Act
            var models = VoskModel.GetAvailableModels();

            // Assert
            models.Should().NotBeNull();
            models.Should().HaveCount(4);
            
            models.Should().Contain(m => m.Id == "vosk-model-small-en-us-0.15");
            models.Should().Contain(m => m.Id == "vosk-model-en-us-0.22");
            models.Should().Contain(m => m.Id == "vosk-model-en-us-0.22-lgraph");
            models.Should().Contain(m => m.Id == "vosk-model-en-us-0.42-gigaspeech");
        }

        [Fact]
        public void GetAvailableModels_ShouldHaveValidProperties()
        {
            // Act
            var models = VoskModel.GetAvailableModels();

            // Assert
            foreach (var model in models)
            {
                model.Id.Should().NotBeNullOrEmpty();
                model.DisplayName.Should().NotBeNullOrEmpty();
                model.DownloadUrl.Should().NotBeNullOrEmpty();
                model.FileName.Should().NotBeNullOrEmpty();
                model.DownloadUrl.Should().StartWith("https://alphacephei.com/vosk/models/");
                model.FileName.Should().EndWith(".zip");
            }
        }

        [Fact]
        public void GetAvailableModels_ShouldHaveUniqueIds()
        {
            // Act
            var models = VoskModel.GetAvailableModels();

            // Assert
            var ids = models.Select(m => m.Id).ToList();
            ids.Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void GetAvailableModels_ShouldContainExpectedDisplayNames()
        {
            // Act
            var models = VoskModel.GetAvailableModels();

            // Assert
            var smallModel = models.First(m => m.Id == "vosk-model-small-en-us-0.15");
            smallModel.DisplayName.Should().Contain("fast, lightweight");

            var balancedModel = models.First(m => m.Id == "vosk-model-en-us-0.22");
            balancedModel.DisplayName.Should().Contain("balanced");

            var lgraphModel = models.First(m => m.Id == "vosk-model-en-us-0.22-lgraph");
            lgraphModel.DisplayName.Should().Contain("large graph");

            var gigaspeechModel = models.First(m => m.Id == "vosk-model-en-us-0.42-gigaspeech");
            gigaspeechModel.DisplayName.Should().Contain("large, accurate");
        }
    }
}