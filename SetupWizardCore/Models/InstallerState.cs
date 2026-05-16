using System;
using System.Collections.Generic;

namespace SetupWizardCore.Models
{
    /// <summary>
    /// Represents the current state of the installer process
    /// </summary>
    public class InstallerState
    {
        public InstallerStep CurrentStep { get; set; } = InstallerStep.Welcome;
        public bool IsProcessing { get; set; } = false;
        public double Progress { get; set; } = 0.0;
        public string? SelectedFolder { get; set; }
        public string? ModelPath { get; set; }
        public List<string> LogOutput { get; set; } = new List<string>();
        public string? ErrorMessage { get; set; }
        public bool IsCompleted { get; set; } = false;
        public bool HasFailed { get; set; } = false;
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }

    /// <summary>
    /// Represents the current step in the installer workflow
    /// </summary>
    public enum InstallerStep
    {
        Welcome,
        FolderPicker,
        ModelSelection,
        Download,
        DependencyCheck,
        DotNetInstall,
        Complete,
        Error
    }

    /// <summary>
    /// Represents the target platform for the installer
    /// </summary>
    public enum TargetPlatform
    {
        Windows,
        macOS,
        Linux
    }
}