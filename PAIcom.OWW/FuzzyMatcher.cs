using System;
using System.Collections.Generic;
using System.Linq;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Fuzzy matching utility for command recognition.
/// Calculates Levenshtein distance (edit distance) between strings
/// and finds the closest match among a set of known commands.
/// 
/// Enhanced with keyword-based matching for hard-to-pronounce words
/// (roblox, spotify, aliexpress, vinted, furaffinity, etc.)
/// </summary>
public sealed class FuzzyMatcher
{
    /// <summary>
    /// Find the best matching command from a set of known commands.
    ///
    /// Uses a multi-stage matching pipeline:
    /// 1. Keyword matching (if a unique keyword like "roblox" is found)
    /// 2. Levenshtein distance (fuzzy string matching)
    /// 3. Word overlap bonus
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
        inputNormalized = NormalizePhoneticAliases(inputNormalized);
        var inputWords = inputNormalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var inputLength = inputNormalized.Length;

        // Keyword stages run on stopword-filtered input: unfiltered input
        // words let stopwords fire keyword routes (the~weather, some~someone).
        // Lev/overlap below intentionally keep the full input (replay shape).
        var keywordWords = inputWords.Where(w => !IsStopWord(w)).ToArray();

        // Build keyword index for unique word detection
        var keywordIndex = BuildKeywordIndex(knownCommands);
        logger?.Invoke($"[oww-fuzzy] Built keyword index with {keywordIndex.Count} unique keywords");
        var sharedIndex = BuildSharedKeywordIndex(knownCommands);

        // Stage 1: Try exact keyword match first
        var keywordMatch = TryKeywordMatch(inputNormalized, keywordWords, keywordIndex, knownCommands, logger);
        if (keywordMatch != null)
        {
            logger?.Invoke($"[oww-fuzzy] KEYWORD MATCH: '{input}' -> '{keywordMatch.Value.command}' via keyword '{keywordMatch.Value.keyword}'");
            return new FuzzyMatchResult(keywordMatch.Value.command, keywordMatch.Value.confidence);
        }

        // Stage 1b: Shared keyword with disambiguation. Manifest growth
        // de-uniquifies working commands (discord); the shared word picks the
        // candidate set and shared-word count plus edit distance disambiguates.
        var sharedMatch = TrySharedKeywordMatch(inputNormalized, keywordWords, sharedIndex, logger);
        if (sharedMatch != null)
        {
            logger?.Invoke($"[oww-fuzzy] SHARED KEYWORD MATCH: '{input}' -> '{sharedMatch.Value.command}' via keyword '{sharedMatch.Value.keyword}' ({sharedMatch.Value.sharedCount} shared words)");
            return new FuzzyMatchResult(sharedMatch.Value.command, sharedMatch.Value.confidence);
        }

        // Stage 2: Calculate similarity score for each known command
        var scores = new List<(string command, float confidence, bool hasKeywordBonus, float overlapScore)>();

        foreach (var command in knownCommands)
        {
            var commandNormalized = NormalizeString(command);
            var commandWords = commandNormalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var distance = LevenshteinDistance(inputNormalized, commandNormalized);

            // Convert distance to similarity confidence
            // confidence = 1 - (distance / max_length)
            var maxLen = Math.Max(inputLength, commandNormalized.Length);
            var confidence = maxLen > 0 ? 1.0f - (distance / (float)maxLen) : 1.0f;

            // Stage 3: Bonus for fuzzy word overlap (exact + singular/plural variants)
            var overlapScore = CalculateWordOverlapScore(inputWords, commandWords);
            var hasKeywordBonus = overlapScore > 0.0f;
            if (hasKeywordBonus)
            {
                // Boost up to 15% based on overlap quality.
                var overlapMultiplier = 1.0f + (0.15f * overlapScore);
                confidence = Math.Min(1.0f, confidence * overlapMultiplier);
            }

            scores.Add((command, confidence, hasKeywordBonus, overlapScore));
        }

        // Get the best match
        var best = scores.OrderByDescending(s => s.confidence).FirstOrDefault();

        if (best.confidence >= minConfidence)
        {
            var bonusNote = best.hasKeywordBonus
                ? $" [word bonus: {best.overlapScore:P0}]"
                : "";
            logger?.Invoke($"[oww-fuzzy] Matched '{input}' to '{best.command}' (confidence: {best.confidence:P1}{bonusNote})");
            return new FuzzyMatchResult(best.command, best.confidence);
        }

        logger?.Invoke($"[oww-fuzzy] No match found for '{input}' (best: {best.command} at {best.confidence:P1}, threshold: {minConfidence:P0})");
        return null;
    }

    /// <summary>
    /// Normalizes common phonetic aliases back to the command spellings used by the matcher.
    /// This helps when Vosk hears a spoken phrase like "road blocks" instead of "roblox".
    /// </summary>
    public static string NormalizePhoneticAliases(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var normalized = NormalizeString(input);
        if (string.IsNullOrWhiteSpace(normalized))
            return normalized;

        var words = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return normalized;

        var rewritten = new List<string>(words.Length);
        for (var index = 0; index < words.Length;)
        {
            if (TryMatchPhoneticAlias(words, index, out var canonical, out var consumed))
            {
                rewritten.Add(canonical);
                index += consumed;
                continue;
            }

            rewritten.Add(words[index]);
            index++;
        }

        return string.Join(" ", rewritten);
    }

    /// <summary>
    /// Returns the alias vocabulary terms that should be fed into Vosk grammar mode.
    /// </summary>
    public static IReadOnlyList<string> GetPhoneticGrammarTerms()
    {
        return _phoneticAliases
            .SelectMany(pair => new[] { pair.Key, pair.Value })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Builds an index of unique keywords across all commands.
    /// Only includes words that appear in exactly ONE command (unique identifiers).
    /// </summary>
    private static Dictionary<string, string> BuildKeywordIndex(IEnumerable<string> commands)
    {
        var wordToCommand = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Count how many commands each word appears in
        foreach (var command in commands)
        {
            var words = NormalizeString(command).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var uniqueWords = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);

            foreach (var word in uniqueWords)
            {
                // Skip common filler words
                if (IsStopWord(word))
                    continue;

                wordCounts[word] = wordCounts.ContainsKey(word) ? wordCounts[word] + 1 : 1;
            }
        }

        // Only keep words that appear in exactly ONE command
        foreach (var command in commands)
        {
            var words = NormalizeString(command).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var uniqueWords = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);

            foreach (var word in uniqueWords)
            {
                if (IsStopWord(word))
                    continue;

                if (wordCounts[word] == 1)
                {
                    wordToCommand[word] = command;
                }
            }
        }

        return wordToCommand;
    }

    /// <summary>
    /// Builds the companion map for words shared by two or more commands.
    /// Manifest growth de-uniquifies working commands; the shared map keeps
    /// them routable via <see cref="TrySharedKeywordMatch"/> instead of
    /// dropping them from keyword routing entirely.
    /// </summary>
    private static Dictionary<string, List<string>> BuildSharedKeywordIndex(IEnumerable<string> commands)
    {
        var wordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var materialized = commands.ToList();

        foreach (var command in materialized)
        {
            var words = NormalizeString(command).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var uniqueWords = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);

            foreach (var word in uniqueWords)
            {
                if (IsStopWord(word))
                    continue;

                wordCounts[word] = wordCounts.ContainsKey(word) ? wordCounts[word] + 1 : 1;
            }
        }

        var shared = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in materialized)
        {
            var words = NormalizeString(command).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var uniqueWords = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);

            foreach (var word in uniqueWords)
            {
                if (IsStopWord(word))
                    continue;

                if (wordCounts[word] > 1)
                {
                    if (!shared.TryGetValue(word, out var owners))
                    {
                        owners = new List<string>();
                        shared[word] = owners;
                    }

                    if (!owners.Contains(command))
                        owners.Add(command);
                }
            }
        }

        return shared;
    }

    /// <summary>
    /// Disambiguates input words owned by several commands: the candidate
    /// sharing the most input words wins, edit distance breaks ties.
    /// Only fires on words literally present in the input, so it cannot
    /// invent routes the input does not name.
    /// </summary>
    private static (string command, string keyword, float confidence, int sharedCount)? TrySharedKeywordMatch(
        string inputNormalized,
        string[] keywordWords,
        Dictionary<string, List<string>> sharedIndex,
        Action<string>? logger)
    {
        var inputSet = new HashSet<string>(keywordWords, StringComparer.OrdinalIgnoreCase);
        string? bestCommand = null;
        string? bestKeyword = null;
        var bestCount = -1;
        var bestDistance = int.MaxValue;

        foreach (var word in keywordWords)
        {
            if (!sharedIndex.TryGetValue(word, out var owners))
                continue;

            foreach (var command in owners)
            {
                var commandWords = NormalizeString(command)
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => !IsStopWord(w))
                    .ToList();
                var count = commandWords.Count(w => inputSet.Contains(w));
                var distance = LevenshteinDistance(inputNormalized, NormalizeString(command));

                if (bestCommand == null || count > bestCount ||
                    (count == bestCount && (distance < bestDistance ||
                        (distance == bestDistance && string.Compare(command, bestCommand, StringComparison.Ordinal) > 0))))
                {
                    bestCommand = command;
                    bestKeyword = word;
                    bestCount = count;
                    bestDistance = distance;
                }
            }
        }

        if (bestCommand != null)
        {
            logger?.Invoke($"[oww-fuzzy] Shared keyword: '{bestKeyword}' -> '{bestCommand}' ({bestCount} shared words)");
            return (bestCommand, bestKeyword!, 0.90f, bestCount);
        }

        return null;
    }

    /// <summary>
    /// Tries to find a unique keyword match in the input.
    /// If a keyword like "roblox", "spotify", "aliexpress" appears in input
    /// and only belongs to ONE command, return that command immediately.
    /// </summary>
    private static (string command, string keyword, float confidence)? TryKeywordMatch(
        string input,
        string[] inputWords,
        Dictionary<string, string> keywordIndex,
        IEnumerable<string> commands,
        Action<string>? logger)
    {
        foreach (var word in inputWords)
        {
            if (keywordIndex.TryGetValue(word, out var command))
            {
                // Found a unique keyword - return this command with high confidence
                return (command, word, 0.95f);
            }
        }

        // Also try partial matches: check if any keyword contains or is contained by input words
        foreach (var word in inputWords)
        {
            foreach (var kvp in keywordIndex)
            {
                var keyword = kvp.Key;
                var cmd = kvp.Value;
                // Check if word is similar to keyword (handles typos/misrecognitions)
                if (word.Length >= 3 && keyword.Length >= 3)
                {
                    var similarity = CalculateWordSimilarity(word, keyword);
                    if (similarity >= 0.85f)
                    {
                        logger?.Invoke($"[oww-fuzzy] Keyword similarity: '{word}' ~ '{keyword}' ({similarity:P0})");
                        return (cmd, keyword, 0.90f);
                    }
                }
            }
        }

        return null;
    }

    private static bool TryMatchPhoneticAlias(string[] words, int startIndex, out string canonical, out int consumed)
    {
        foreach (var alias in _phoneticAliasPatterns)
        {
            if (startIndex + alias.AliasWords.Length > words.Length)
                continue;

            var matched = true;
            for (var i = 0; i < alias.AliasWords.Length; i++)
            {
                if (!AreAliasWordsEquivalent(words[startIndex + i], alias.AliasWords[i]))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
                continue;

            canonical = alias.Canonical;
            consumed = alias.AliasWords.Length;
            return true;
        }

        canonical = string.Empty;
        consumed = 1;
        return false;
    }

    /// <summary>
    /// Calculates weighted overlap between input words and command words.
    /// Treats exact and inflection-variant words (singular/plural) as strong matches.
    /// </summary>
    private static float CalculateWordOverlapScore(string[] inputWords, string[] commandWords)
    {
        if (inputWords.Length == 0 || commandWords.Length == 0)
            return 0.0f;

        var matchedCommandIndexes = new HashSet<int>();
        var weightedMatches = 0.0f;

        foreach (var inputWord in inputWords)
        {
            var bestScore = 0.0f;
            var bestIndex = -1;

            for (var index = 0; index < commandWords.Length; index++)
            {
                if (matchedCommandIndexes.Contains(index))
                    continue;

                var similarity = CalculateWordSimilarity(inputWord, commandWords[index]);
                if (similarity > bestScore)
                {
                    bestScore = similarity;
                    bestIndex = index;
                }
            }

            if (bestIndex >= 0 && bestScore >= 0.85f)
            {
                matchedCommandIndexes.Add(bestIndex);
                weightedMatches += bestScore;
            }
        }

        var denominator = Math.Max(inputWords.Length, commandWords.Length);
        return denominator > 0 ? Math.Min(1.0f, weightedMatches / denominator) : 0.0f;
    }

    /// <summary>
    /// Alias comparison helper that accepts exact and inflection-variant matches.
    /// </summary>
    private static bool AreAliasWordsEquivalent(string spokenWord, string aliasWord)
    {
        if (string.Equals(spokenWord, aliasWord, StringComparison.OrdinalIgnoreCase))
            return true;

        var spokenVariant = NormalizeWordVariant(spokenWord);
        var aliasVariant = NormalizeWordVariant(aliasWord);
        return string.Equals(spokenVariant, aliasVariant, StringComparison.Ordinal);
    }

    /// <summary>
    /// Calculate similarity between two individual words.
    /// Uses character overlap + length ratio for fast comparison.
    /// </summary>
    private static float CalculateWordSimilarity(string word1, string word2)
    {
        var s1 = word1.ToLowerInvariant();
        var s2 = word2.ToLowerInvariant();

        if (s1 == s2)
            return 1.0f;

        var v1 = NormalizeWordVariant(s1);
        var v2 = NormalizeWordVariant(s2);
        if (v1 == v2)
            return 0.96f;

        // Containment is similarity only for genuine affixation (five/fiver,
        // pony/ponytail). Coincidence containment (chat in viarchat, the in
        // weather, eat in weather) relocates hijacks instead of removing
        // them, so it falls through to bigram scoring. Census: legit pairs
        // sit at length ratio 0.80, hijack pairs at 0.50 or below; affixed
        // compounds keep a 0.50 floor.
        if (s1.Contains(s2) || s2.Contains(s1))
        {
            var shortWord = s1.Length <= s2.Length ? s1 : s2;
            var longWord = s1.Length <= s2.Length ? s2 : s1;
            var ratio = (float)shortWord.Length / longWord.Length;
            if (ratio >= 0.75f)
                return 0.90f;
            if (ratio >= 0.50f && (longWord.StartsWith(shortWord, StringComparison.Ordinal) ||
                                   longWord.EndsWith(shortWord, StringComparison.Ordinal)))
                return 0.90f;
        }

        // Use character bigram overlap for short words
        if (s1.Length >= 3 && s2.Length >= 3)
        {
            var bigrams1 = GetBigrams(s1);
            var bigrams2 = GetBigrams(s2);
            var intersection = bigrams1.Intersect(bigrams2).Count();
            var union = bigrams1.Union(bigrams2).Count();
            return union > 0 ? (float)intersection / union : 0.0f;
        }

        return 0.0f;
    }

    /// <summary>
    /// Normalizes common inflection variants (for example, plural vs singular).
    /// </summary>
    private static string NormalizeWordVariant(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return string.Empty;

        var normalized = word.ToLowerInvariant();
        if (normalized.Length <= 3)
            return normalized;

        if (normalized.EndsWith("ies", StringComparison.Ordinal) && normalized.Length > 4)
            return normalized.Substring(0, normalized.Length - 3) + "y";

        if (normalized.EndsWith("es", StringComparison.Ordinal) && normalized.Length > 3)
        {
            if (normalized.EndsWith("ses", StringComparison.Ordinal)
                || normalized.EndsWith("xes", StringComparison.Ordinal)
                || normalized.EndsWith("zes", StringComparison.Ordinal)
                || normalized.EndsWith("ches", StringComparison.Ordinal)
                || normalized.EndsWith("shes", StringComparison.Ordinal)
                || normalized.EndsWith("oes", StringComparison.Ordinal))
            {
                return normalized.Substring(0, normalized.Length - 2);
            }
        }

        if (normalized.EndsWith("s", StringComparison.Ordinal)
            && !normalized.EndsWith("ss", StringComparison.Ordinal)
            && normalized.Length > 3)
        {
            return normalized.Substring(0, normalized.Length - 1);
        }

        return normalized;
    }

    /// <summary>
    /// Get character bigrams from a string.
    /// </summary>
    private static HashSet<string> GetBigrams(string s)
    {
        var bigrams = new HashSet<string>();
        for (int i = 0; i < s.Length - 1; i++)
        {
            bigrams.Add(s.Substring(i, 2));
        }
        return bigrams;
    }

    /// <summary>
    /// Check if a word is a common stop word that shouldn't be used for matching.
    /// </summary>
    private static bool IsStopWord(string word)
    {
        return word.Length <= 2 || _stopWords.Contains(word.ToLowerInvariant());
    }

    private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "shall", "can", "need", "dare", "ought",
        "used", "to", "of", "in", "for", "on", "with", "at", "by", "from",
        "as", "into", "through", "during", "before", "after", "above", "below",
        "between", "out", "off", "over", "under", "again", "further", "then",
        "once", "here", "there", "when", "where", "why", "how", "all", "both",
        "each", "few", "more", "most", "other", "some", "such", "no", "nor",
        "not", "only", "own", "same", "so", "than", "too", "very", "just",
        "don't", "doesn't", "didn't", "won't", "wouldn't", "shouldn't", "couldn't",
        "i", "i'm", "i'm", "me", "my", "myself", "we", "our", "ours", "ourselves",
        "you", "your", "yours", "yourself", "yourselves", "he", "him", "his",
        "himself", "she", "her", "hers", "herself", "it", "its", "itself",
        "they", "them", "their", "theirs", "themselves", "what", "which", "who",
        "whom", "this", "that", "these", "those", "am", "about"
    };

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

    private static readonly Dictionary<string, string> _phoneticAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["road blocks"] = "roblox",
        ["roadblocks"] = "roblox",
        ["rob locks"] = "roblox",
        ["robloks"] = "roblox",
        ["spot if i"] = "spotify",
        ["spot a fi"] = "spotify",
        ["spotifiy"] = "spotify",
        ["spotifie"] = "spotify",
        ["ali express"] = "aliexpress",
        ["allie express"] = "aliexpress",
        ["reddit"] = "redit",
        ["twitter"] = "twiter",
        ["instagram"] = "insta grem",
        ["insta gram"] = "insta grem",
        ["furaffinity"] = "fur affinity",
        ["fur affinity"] = "fur affinity",
        ["furry affinity"] = "fur affinity",
        ["vrchat"] = "viarchat",
        ["v r chat"] = "viarchat",
        ["vee are chat"] = "viarchat",
        ["newgrounds"] = "new grounds",
        ["bluesky"] = "blue sky",
        ["boykisser"] = "boy kisser",
        ["chatgpt"] = "chat gi bi ti",
        ["chat g p t"] = "chat gi bi ti",
        ["fiverr"] = "fiver",
        ["h b o"] = "hbo",
        ["holy"] = "hulu",
        ["pin terest"] = "pinterest",
        ["vin tid"] = "vinted"
    };

    private static readonly (string[] AliasWords, string Canonical)[] _phoneticAliasPatterns = _phoneticAliases
        .Select(pair => (AliasWords: pair.Key.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), Canonical: pair.Value))
        .OrderByDescending(alias => alias.AliasWords.Length)
        .ThenByDescending(alias => alias.AliasWords.Sum(word => word.Length))
        .ToArray();
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
