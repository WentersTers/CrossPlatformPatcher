using System;
using System.Collections.Generic;
using System.Linq;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Fuzzy matching utility for command recognition.
/// Calculates Levenshtein distance (edit distance) between strings
/// and finds the closest match among a set of known commands.
/// </summary>
public sealed class FuzzyMatcher
{
    /// <summary>
    /// Find the best matching command from a set of known commands.
    /// 
    /// Returns a match result containing the best match and similarity score.
    /// If no match meets the minimum confidence threshold, returns null.
    /// </summary>
    public static FuzzyMatchResult? FindClosestMatch(
        string input,
        IEnumerable<string> knownCommands,
        float minConfidence = 0.80f,
        Action<string>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        if (knownCommands == null || !knownCommands.Any())
            return null;

        var inputNormalized = NormalizeString(input);
        var inputLength = inputNormalized.Length;

        // Calculate similarity score for each known command
        var scores = new List<(string command, float confidence)>();
        
        foreach (var command in knownCommands)
        {
            var commandNormalized = NormalizeString(command);
            var distance = LevenshteinDistance(inputNormalized, commandNormalized);
            
            // Convert distance to similarity confidence
            // confidence = 1 - (distance / max_length)
            var maxLen = Math.Max(inputLength, commandNormalized.Length);
            var confidence = maxLen > 0 ? 1.0f - (distance / (float)maxLen) : 1.0f;
            
            scores.Add((command, confidence));
        }

        // Get the best match
        var best = scores.OrderByDescending(s => s.confidence).FirstOrDefault();
        
        if (best.confidence >= minConfidence)
        {
            logger?.Invoke($"[oww-fuzzy] Matched '{input}' to '{best.command}' (confidence: {best.confidence:P1})");
            return new FuzzyMatchResult(best.command, best.confidence);
        }

        logger?.Invoke($"[oww-fuzzy] No match found for '{input}' (best: {best.command} at {best.confidence:P1}, threshold: {minConfidence:P0})");
        return null;
    }

    /// <summary>
    /// Calculate Levenshtein distance (edit distance) between two strings.
    /// Returns the minimum number of single-character edits (insert, delete, substitute)
    /// needed to change one string into another.
    /// </summary>
    private static int LevenshteinDistance(string s1, string s2)
    {
        if (s1.Length == 0) return s2.Length;
        if (s2.Length == 0) return s1.Length;

        var costs = new int[s2.Length + 1];
        for (int i = 0; i <= s2.Length; i++)
            costs[i] = i;

        for (int i = 1; i <= s1.Length; i++)
        {
            int nw = costs[0];
            costs[0] = i;

            for (int j = 1; j <= s2.Length; j++)
            {
                int nc = nw + (s1[i - 1] == s2[j - 1] ? 0 : 1);
                nw = costs[j];
                costs[j] = Math.Min(
                    Math.Min(costs[j] + 1, costs[j - 1] + 1),
                    nc);
            }
        }

        return costs[s2.Length];
    }

    /// <summary>
    /// Normalize string for matching: lowercase and trim whitespace.
    /// </summary>
    private static string NormalizeString(string s)
    {
        return (s ?? string.Empty).ToLowerInvariant().Trim();
    }
}

/// <summary>
/// Result of a fuzzy matching operation.
/// </summary>
public sealed class FuzzyMatchResult
{
    /// <summary>The matched command.</summary>
    public string MatchedCommand { get; }

    /// <summary>Confidence score [0.0, 1.0] indicating how close the match is.</summary>
    public float Confidence { get; }

    internal FuzzyMatchResult(string matchedCommand, float confidence)
    {
        MatchedCommand = matchedCommand;
        Confidence = confidence;
    }
}
