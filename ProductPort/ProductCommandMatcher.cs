// Product-side port of the twin-validated matcher fix.
//
// WHAT: the three release inputs plus the media aliases, expressed as a
// dependency-free static module in the product's own idiom:
//   1. input stopword filter for the keyword stages,
//   2. containment/threshold review (length-ratio guard 0.75, affix floor 0.50),
//   3. shared-keyword disambiguation for de-uniquified commands,
//   plus phonetic aliases incl. "h b o" -> hbo and "holy" -> hulu.
//
// SEMANTICS: proven in the twin (replay, CAL re-cert, 109 flips, 112/112
// self-route) and in the addon (235 unit tests). This file is spelling, not
// invention: stage order, thresholds, confidences and tie-breaks mirror the
// twin exactly. Any behavioral drift between this file and matcher_fix.py is
// a defect in this file.
//
// CONSTRAINTS (product builds on .NET Framework with an older compiler):
//   - C# 7.3 surface only: no ranges, indices, switch expressions,
//     tuple deconstruction or GetValueOrDefault.
//   - No ValueTuple (needs a package ref before net47): small result class.
//   - Only System + System.Collections.Generic + System.Linq.
//   - No product command strings live here: candidates arrive as input.
//     The alias table carries ASR garble forms plus command spellings
//     already present in the addon codebase; no new product content.
//
// INTEGRATION (core release):
//   - Build the candidate list from the existing command literals (the same
//     strings the literal chain compares today), wake word already stripped.
//   - Call FindBestMatch(normalizedInput, candidates, 0.80f).
//   - Dispatch on result.Command when non-null; keep the existing literal
//     chain as the fallback for null (fail-closed, today's behavior).
//   - Feed GetPhoneticGrammarTerms into the recognizer grammar so alias
//     forms stay recognizable.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PAIcom.Product
{
    /// <summary>Outcome of <see cref="ProductCommandMatcher.FindBestMatch"/>.</summary>
    public sealed class ProductCommandMatch
    {
        public ProductCommandMatch(string command, float confidence, string how)
        {
            Command = command;
            Confidence = confidence;
            How = how;
        }

        public string Command { get; }
        public float Confidence { get; }
        public string How { get; }
    }

    /// <summary>
    /// Twin-validated command matcher for the product's own source.
    /// Four stages: exact keyword (0.95), shared keyword (0.90),
    /// partial keyword (0.90), lev-plus-overlap at the floor.
    /// </summary>
    public static class ProductCommandMatcher
    {
        public const float ExactConfidence = 0.95f;
        public const float PartialConfidence = 0.90f;
        public const float SharedConfidence = 0.90f;
        public const float DefaultFloor = 0.80f;
        public const float PartialThreshold = 0.85f;

        private const float ContainMinRatio = 0.75f;
        private const float ContainAffixFloor = 0.50f;

        /// <summary>
        /// Best matching command for <paramref name="input"/>, or null when
        /// nothing reaches <paramref name="floor"/>. Returns null (never
        /// throws) on empty input or an empty candidate list.
        /// </summary>
        public static ProductCommandMatch FindBestMatch(
            string input,
            IList<string> candidates,
            float floor)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;
            if (candidates == null || candidates.Count == 0)
                return null;

            string normalized = NormalizePhoneticAliases(NormalizeString(input));
            string[] inputWords = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string[] keywordWords = inputWords.Where(w => !IsStopWord(w)).ToArray();

            Dictionary<string, string> uniqueIndex;
            Dictionary<string, List<string>> sharedIndex;
            BuildIndexes(candidates, out uniqueIndex, out sharedIndex);

            // Stage 1: exact keyword on filtered input.
            foreach (string word in keywordWords)
            {
                string command;
                if (uniqueIndex.TryGetValue(word, out command))
                    return new ProductCommandMatch(command, ExactConfidence, "exact-keyword:" + word);
            }

            // Stage 2: shared keyword with disambiguation.
            {
                HashSet<string> inputSet = new HashSet<string>(keywordWords, StringComparer.OrdinalIgnoreCase);
                string bestCommand = null;
                string bestKeyword = null;
                int bestCount = -1;
                int bestDistance = int.MaxValue;

                foreach (string word in keywordWords)
                {
                    List<string> owners;
                    if (!sharedIndex.TryGetValue(word, out owners))
                        continue;

                    foreach (string command in owners)
                    {
                        List<string> commandWords = NormalizeString(command)
                            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(w => !IsStopWord(w))
                            .ToList();
                        int count = commandWords.Count(w => inputSet.Contains(w));
                        int distance = LevenshteinDistance(normalized, NormalizeString(command));

                        if (bestCommand == null || count > bestCount ||
                            (count == bestCount && (distance < bestDistance ||
                                (distance == bestDistance &&
                                 string.Compare(command, bestCommand, StringComparison.Ordinal) > 0))))
                        {
                            bestCommand = command;
                            bestKeyword = word;
                            bestCount = count;
                            bestDistance = distance;
                        }
                    }
                }

                if (bestCommand != null)
                    return new ProductCommandMatch(bestCommand, SharedConfidence,
                        "shared-keyword:" + bestKeyword + "->" + bestCommand + "(" + bestCount + ")");
            }

            // Stage 3: partial keyword on filtered input.
            foreach (string word in keywordWords)
            {
                foreach (KeyValuePair<string, string> pair in uniqueIndex)
                {
                    if (word.Length >= 3 && pair.Key.Length >= 3 &&
                        WordSimilarity(word, pair.Key) >= PartialThreshold)
                    {
                        return new ProductCommandMatch(pair.Value, PartialConfidence,
                            "partial-keyword:" + word + "~" + pair.Key);
                    }
                }
            }

            // Stage 4: lev plus overlap on the full input (replay shape).
            string bestRoute = null;
            float bestConf = -1.0f;
            foreach (string command in candidates)
            {
                string cn = command.ToLowerInvariant();
                string[] cw = cn.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int d = LevenshteinDistance(normalized, cn);
                int ml = Math.Max(normalized.Length, cn.Length);
                float conf = ml > 0 ? 1.0f - (d / (float)ml) : 1.0f;
                float ov = OverlapScore(inputWords, cw);
                if (ov > 0.0f)
                    conf = Math.Min(1.0f, conf * (1.0f + 0.15f * ov));
                if (bestRoute == null || conf > bestConf)
                {
                    bestRoute = command;
                    bestConf = conf;
                }
            }

            if (bestRoute != null && bestConf >= floor)
                return new ProductCommandMatch(bestRoute, bestConf, "lev+overlap:" + bestConf.ToString("0.000", CultureInfo.InvariantCulture));
            return null;
        }

        private static void BuildIndexes(
            IList<string> candidates,
            out Dictionary<string, string> uniqueIndex,
            out Dictionary<string, List<string>> sharedIndex)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string command in candidates)
            {
                string[] words = NormalizeString(command).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                HashSet<string> seen = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
                foreach (string word in seen)
                {
                    if (IsStopWord(word))
                        continue;
                    int n;
                    counts.TryGetValue(word, out n);
                    counts[word] = n + 1;
                }
            }

            uniqueIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sharedIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string command in candidates)
            {
                string[] words = NormalizeString(command).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                HashSet<string> seen = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
                foreach (string word in seen)
                {
                    if (IsStopWord(word))
                        continue;
                    int count = counts[word];
                    if (count == 1)
                    {
                        if (!uniqueIndex.ContainsKey(word))
                            uniqueIndex[word] = command;
                    }
                    else
                    {
                        List<string> owners;
                        if (!sharedIndex.TryGetValue(word, out owners))
                        {
                            owners = new List<string>();
                            sharedIndex[word] = owners;
                        }
                        if (!owners.Contains(command))
                            owners.Add(command);
                    }
                }
            }
        }

        internal static float WordSimilarity(string word1, string word2)
        {
            string s1 = word1.ToLowerInvariant();
            string s2 = word2.ToLowerInvariant();

            if (s1 == s2)
                return 1.0f;

            if (NormalizeWordVariant(s1) == NormalizeWordVariant(s2))
                return 0.96f;

            // Containment counts only for genuine affixation. Coincidence
            // containment (chat in viarchat, the in weather) falls through
            // to bigram scoring. Census: legit pairs at ratio 0.80, hijack
            // pairs at 0.50 or below; affixed compounds keep a 0.50 floor.
            if (s1.Contains(s2) || s2.Contains(s1))
            {
                string shortWord = s1.Length <= s2.Length ? s1 : s2;
                string longWord = s1.Length <= s2.Length ? s2 : s1;
                float ratio = (float)shortWord.Length / longWord.Length;
                if (ratio >= ContainMinRatio)
                    return 0.90f;
                if (ratio >= ContainAffixFloor &&
                    (longWord.StartsWith(shortWord, StringComparison.Ordinal) ||
                     longWord.EndsWith(shortWord, StringComparison.Ordinal)))
                    return 0.90f;
            }

            if (s1.Length >= 3 && s2.Length >= 3)
            {
                HashSet<string> b1 = GetBigrams(s1);
                HashSet<string> b2 = GetBigrams(s2);
                int inter = 0;
                foreach (string b in b1)
                {
                    if (b2.Contains(b))
                        inter++;
                }
                int union = b1.Count + b2.Count - inter;
                return union > 0 ? (float)inter / union : 0.0f;
            }

            return 0.0f;
        }

        internal static float OverlapScore(string[] inputWords, string[] commandWords)
        {
            if (inputWords.Length == 0 || commandWords.Length == 0)
                return 0.0f;

            HashSet<int> used = new HashSet<int>();
            float wm = 0.0f;
            foreach (string w in inputWords)
            {
                int bi = -1;
                float bs = 0.0f;
                for (int j = 0; j < commandWords.Length; j++)
                {
                    if (used.Contains(j))
                        continue;
                    float s = WordSimilarity(w, commandWords[j]);
                    if (s > bs)
                    {
                        bi = j;
                        bs = s;
                    }
                }
                if (bi >= 0 && bs >= PartialThreshold)
                {
                    used.Add(bi);
                    wm += bs;
                }
            }

            int den = Math.Max(inputWords.Length, commandWords.Length);
            float raw = den > 0 ? wm / den : 0.0f;
            return Math.Min(1.0f, raw);
        }

        internal static string NormalizePhoneticAliases(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            string normalized = NormalizeString(input);
            if (string.IsNullOrWhiteSpace(normalized))
                return normalized;

            string[] words = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return normalized;

            List<string> rewritten = new List<string>(words.Length);
            int index = 0;
            while (index < words.Length)
            {
                string canonical;
                int consumed;
                if (TryMatchAlias(words, index, out canonical, out consumed))
                {
                    rewritten.Add(canonical);
                    index += consumed;
                    continue;
                }
                rewritten.Add(words[index]);
                index++;
            }
            return string.Join(" ", rewritten.ToArray());
        }

        /// <summary>Alias vocabulary for recognizer-grammar feeding.</summary>
        public static string[] GetPhoneticGrammarTerms()
        {
            HashSet<string> terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in PhoneticAliases)
            {
                terms.Add(pair.Key);
                terms.Add(pair.Value);
            }
            string[] result = new string[terms.Count];
            terms.CopyTo(result);
            return result;
        }

        private static bool TryMatchAlias(string[] words, int start, out string canonical, out int consumed)
        {
            foreach (AliasPattern pattern in AliasPatterns)
            {
                if (start + pattern.Words.Length > words.Length)
                    continue;
                bool matched = true;
                for (int i = 0; i < pattern.Words.Length; i++)
                {
                    if (!AliasWordsEquivalent(words[start + i], pattern.Words[i]))
                    {
                        matched = false;
                        break;
                    }
                }
                if (!matched)
                    continue;
                canonical = pattern.Canonical;
                consumed = pattern.Words.Length;
                return true;
            }
            canonical = string.Empty;
            consumed = 1;
            return false;
        }

        private static bool AliasWordsEquivalent(string spoken, string alias)
        {
            if (string.Equals(spoken, alias, StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(NormalizeWordVariant(spoken), NormalizeWordVariant(alias), StringComparison.Ordinal);
        }

        internal static string NormalizeWordVariant(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
                return string.Empty;
            string n = word.ToLowerInvariant();
            if (n.Length <= 3)
                return n;
            if (n.EndsWith("ies", StringComparison.Ordinal) && n.Length > 4)
                return n.Substring(0, n.Length - 3) + "y";
            if (n.EndsWith("es", StringComparison.Ordinal) && n.Length > 3)
            {
                if (n.EndsWith("ses", StringComparison.Ordinal) ||
                    n.EndsWith("xes", StringComparison.Ordinal) ||
                    n.EndsWith("zes", StringComparison.Ordinal) ||
                    n.EndsWith("ches", StringComparison.Ordinal) ||
                    n.EndsWith("shes", StringComparison.Ordinal) ||
                    n.EndsWith("oes", StringComparison.Ordinal))
                {
                    return n.Substring(0, n.Length - 2);
                }
            }
            if (n.EndsWith("s", StringComparison.Ordinal) &&
                !n.EndsWith("ss", StringComparison.Ordinal) && n.Length > 3)
            {
                return n.Substring(0, n.Length - 1);
            }
            return n;
        }

        internal static int LevenshteinDistance(string s1, string s2)
        {
            if (s1.Length == 0) return s2.Length;
            if (s2.Length == 0) return s1.Length;
            int[] costs = new int[s2.Length + 1];
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
                    costs[j] = Math.Min(Math.Min(costs[j] + 1, costs[j - 1] + 1), nc);
                }
            }
            return costs[s2.Length];
        }

        internal static HashSet<string> GetBigrams(string s)
        {
            HashSet<string> result = new HashSet<string>();
            for (int i = 0; i < s.Length - 1; i++)
                result.Add(s.Substring(i, 2));
            return result;
        }

        internal static bool IsStopWord(string word)
        {
            return word.Length <= 2 || StopWords.Contains(word.ToLowerInvariant());
        }

        internal static string NormalizeString(string s)
        {
            return (s ?? string.Empty).ToLowerInvariant().Trim();
        }

        private sealed class AliasPattern
        {
            public AliasPattern(string[] words, string canonical)
            {
                Words = words;
                Canonical = canonical;
            }

            public string[] Words { get; }
            public string Canonical { get; }
        }

        private static AliasPattern[] BuildAliasPatterns()
        {
            // Twin parity: longest word-count first, then most word chars.
            List<AliasPattern> patterns = new List<AliasPattern>(PhoneticAliases.Count);
            foreach (KeyValuePair<string, string> pair in PhoneticAliases)
            {
                string[] words = pair.Key.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                patterns.Add(new AliasPattern(words, pair.Value));
            }
            patterns.Sort(delegate (AliasPattern a, AliasPattern b)
            {
                if (a.Words.Length != b.Words.Length)
                    return b.Words.Length.CompareTo(a.Words.Length);
                int alen = 0;
                int blen = 0;
                foreach (string w in a.Words)
                    alen += w.Length;
                foreach (string w in b.Words)
                    blen += w.Length;
                return blen.CompareTo(alen);
            });
            return patterns.ToArray();
        }

        private static readonly Dictionary<string, string> PhoneticAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "road blocks", "roblox" },
            { "roadblocks", "roblox" },
            { "rob locks", "roblox" },
            { "robloks", "roblox" },
            { "spot if i", "spotify" },
            { "spot a fi", "spotify" },
            { "spotifiy", "spotify" },
            { "spotifie", "spotify" },
            { "ali express", "aliexpress" },
            { "allie express", "aliexpress" },
            { "reddit", "redit" },
            { "twitter", "twiter" },
            { "instagram", "insta grem" },
            { "insta gram", "insta grem" },
            { "furaffinity", "fur affinity" },
            { "fur affinity", "fur affinity" },
            { "furry affinity", "fur affinity" },
            { "vrchat", "viarchat" },
            { "v r chat", "viarchat" },
            { "vee are chat", "viarchat" },
            { "newgrounds", "new grounds" },
            { "bluesky", "blue sky" },
            { "boykisser", "boy kisser" },
            { "chatgpt", "chat gi bi ti" },
            { "chat g p t", "chat gi bi ti" },
            { "fiverr", "fiver" },
            { "h b o", "hbo" },
            { "holy", "hulu" },
            { "pin terest", "pinterest" },
            { "vin tid", "vinted" },
        };

        private static readonly AliasPattern[] AliasPatterns = BuildAliasPatterns();

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
            "i", "i'm", "me", "my", "myself", "we", "our", "ours", "ourselves",
            "you", "your", "yours", "yourself", "yourselves", "he", "him", "his",
            "himself", "she", "her", "hers", "herself", "it", "its", "itself",
            "they", "them", "their", "theirs", "themselves", "what", "which", "who",
            "whom", "this", "that", "these", "those", "am", "about", "dont",
            "didnt", "wont", "wouldnt", "shouldnt", "couldnt", "im",
        };
    }
}
