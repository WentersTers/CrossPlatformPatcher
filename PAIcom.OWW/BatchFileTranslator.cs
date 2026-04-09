using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Translates Windows batch files (.bat) to POSIX shell scripts (.sh) for cross-platform execution.
/// Handles common patterns: taskkill, explorer (protocol handlers), echo, exit, etc.
/// </summary>
public static class BatchFileTranslator
{
    public enum TranslationMode
    {
        /// <summary>Translate batch content in-memory and execute without writing files.</summary>
        Internal,
        /// <summary>Generate .sh files alongside .bat files and validate syntax for testing.</summary>
        GenerateAndTest
    }

    /// <summary>
    /// Translates batch file content to POSIX shell syntax.
    /// Returns translated shell command(s) as a single executable string.
    /// </summary>
    public static string TranslateBatchContent(string batchContent)
    {
        var lines = batchContent.Split('\n', '\r')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("::"))
            .ToList();

        var translated = new List<string>();

        foreach (var line in lines)
        {
            // Skip batch directives and comments
            if (line.StartsWith("@echo off", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("@if", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("echo ", StringComparison.OrdinalIgnoreCase) && line == "echo off" ||
                line.StartsWith("setlocal", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("endlocal", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var shellCmd = TranslateBatchLine(line);
            if (!string.IsNullOrWhiteSpace(shellCmd))
            {
                translated.Add(shellCmd);
            }
        }

        // Join with && to maintain sequential execution and error handling
        return string.Join(" && ", translated);
    }

    /// <summary>
    /// Translates a single batch line to shell syntax.
    /// </summary>
    private static string? TranslateBatchLine(string line)
    {
        // taskkill /im PROCESS.exe → killall PROCESS
        if (line.StartsWith("taskkill", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(line, @"taskkill\s+/im\s+([^\s]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var processName = match.Groups[1].Value.TrimEnd();
                // Remove .exe extension if present
                if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    processName = processName.Substring(0, processName.Length - 4);
                }
                // Use pkill for better compatibility, with timeout to avoid hanging
                return $"pkill -f '{processName}' || true";
            }
        }

        // explorer "steam://..." → xdg-open (Linux) or open (macOS)
        if (line.StartsWith("explorer", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(line, @"explorer\s+""([^""]+)""", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var uri = match.Groups[1].Value;
                return TranslateProtocolUrl(uri);
            }
            
            // explorer without URI might open current directory
            return "(command -v open >/dev/null && open . || xdg-open .) 2>/dev/null || true";
        }

        // exit → exit 0
        if (line.Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            return "exit 0";
        }

        // @echo off, echo off, etc. → skip (shell doesn't need this)
        if (line.Equals("@echo off", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("echo off", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // timeout /t N → sleep N
        if (line.StartsWith("timeout", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(line, @"timeout\s+/t\s+(\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var seconds = match.Groups[1].Value;
                return $"sleep {seconds}";
            }
        }

        // echo message → echo message
        if (line.StartsWith("echo", StringComparison.OrdinalIgnoreCase))
        {
            var message = Regex.Replace(line, @"^echo\s+", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(message) && message != "off")
            {
                return $"echo '{message}'";
            }
        }

        // cscript %0 → handled as-is (uncommon in your .bat files)
        if (line.StartsWith("cscript", StringComparison.OrdinalIgnoreCase))
        {
            // cscript is Windows-specific; log warning but skip
            return $"# WARNING: cscript not supported on Unix: {line}";
        }

        // powershell → handled as-is (uncommon in your .bat files)
        if (line.StartsWith("powershell", StringComparison.OrdinalIgnoreCase))
        {
            // Complex PowerShell scripts can't be auto-translated; log warning
            return $"# WARNING: PowerShell not supported on Unix: {line}";
        }

        // start command → handle Windows apps and protocol URLs specially
        if (line.StartsWith("start", StringComparison.OrdinalIgnoreCase))
        {
            // Remove quoted window title if present: start "title" program
            var cmd = Regex.Replace(line, @"^start\s+""[^""]*""\s+", "", RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(cmd))
            {
                cmd = Regex.Replace(line, @"^start\s+", "", RegexOptions.IgnoreCase).Trim();
            }

            // Map Windows applications to their Unix equivalents
            var cmdLower = cmd.ToLower();
            
            // Task Manager variations
            if (cmdLower == "taskmgr" || cmdLower == "taskmgr.exe")
            {
                // taskmgr → Activity Monitor (macOS) or System Monitor (Linux)
                return TranslateTaskManagerCommand();
            }
            
            // Steam application
            if (cmdLower == "steam" || cmdLower == "steam.exe")
            {
                return TranslateSteamLaunchCommand();
            }

            // Check if this is a protocol URL (steam://, http://, https://, etc.)
            if (cmd.Contains("://") || cmd.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
            {
                return TranslateProtocolUrl(cmd);
            }

            // Regular command: execute in background
            return $"{cmd} &";
        }

        // If we couldn't translate it but it's not a batch directive, return it as-is with a comment
        if (!string.IsNullOrWhiteSpace(line))
        {
            return $"# Untranslated: {line}";
        }

        return null;
    }

    /// <summary>
    /// Validates shell syntax by running `sh -n` (syntax check without execution).
    /// Returns (isValid, errorMessage).
    /// </summary>
    public static (bool IsValid, string? ErrorMessage) ValidateShellSyntax(string shellScript)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sh",
                Arguments = "-n", // Syntax check only
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    return (false, "Failed to start shell validator");

                process.StandardInput.WriteLine(shellScript);
                process.StandardInput.Close();

                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0)
                    return (true, null);

                return (false, error);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Generates a .sh file alongside a .bat file in generate-and-test mode.
    /// </summary>
    public static bool TryGenerateShellEquivalent(string batchFilePath, string translatedContent, out string? generatedPath, Action<string>? logger = null)
    {
        generatedPath = null;

        try
        {
            var shFilePath = Path.ChangeExtension(batchFilePath, ".sh");
            var shContent = $"#!/bin/sh\n# Auto-generated from {Path.GetFileName(batchFilePath)}\nset -e\n\n{translatedContent}\n";

            File.WriteAllText(shFilePath, shContent);
            generatedPath = shFilePath;

            // Make it executable
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = "+x " + QuoteArgument(shFilePath),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi)?.WaitForExit();
            }
            catch { /* chmod might not be available on Windows */ }

            logger?.Invoke($"[batch-converter] Generated shell equivalent: {shFilePath}");
            return true;
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[batch-converter-error] Failed to generate shell equivalent: {ex.Message}");
            return false;
        }
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.IndexOf(' ') < 0 && argument.IndexOf('\t') < 0)
            return argument;
        return "\"" + argument.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// Translates protocol URLs (steam://, http://, etc.) to platform-specific commands.
    /// </summary>
    private static string TranslateProtocolUrl(string uri)
    {
        // Escape single quotes in URI to prevent shell injection
        var escapedUri = uri.Replace("'", "'\\''");
        
        // macOS: 'open' handles protocol URLs natively
        // Linux: 'xdg-open' is the standard
        return $"(command -v open >/dev/null 2>&1 && open '{escapedUri}' || xdg-open '{escapedUri}' 2>/dev/null) 2>/dev/null || true";
    }

    /// <summary>
    /// Translates Task Manager command to platform-specific system monitors.
    /// </summary>
    private static string TranslateTaskManagerCommand()
    {
        // macOS: Activity Monitor
        // Linux: gnome-system-monitor, mate-system-monitor, xterm + top/htop
        return "(command -v open >/dev/null 2>&1 && open -a 'Activity Monitor' || gnome-system-monitor 2>/dev/null || mate-system-monitor 2>/dev/null || (command -v xterm >/dev/null 2>&1 && xterm -e htop &) || (command -v htop >/dev/null 2>&1 && htop &)) 2>/dev/null || true";
    }

    /// <summary>
    /// Translates Steam launch command to platform-specific approaches.
    /// </summary>
    private static string TranslateSteamLaunchCommand()
    {
        // macOS: try to open Steam app directly, fallback to steam:// protocol
        // Linux: try steam command or xdg-open with steam:// protocol
        return "(command -v open >/dev/null 2>&1 && open -a 'Steam' || command -v steam >/dev/null 2>&1 && steam || xdg-open 'steam://open/main' 2>/dev/null) 2>/dev/null || true";
    }
}
