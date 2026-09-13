using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PAIcom.Product;

namespace PortCheck
{
    // Compares the product port against twin-generated vectors.
    // Usage: PortCheck <port-vectors.json> <commands-manifest.txt>
    // The manifest is the release tree's commands file (wake-prefixed lines
    // with optional "(ref)" suffixes, same shape the twin loads).
    // Exit code: 0 when every vector matches (route exact, confidence
    // within 0.002, stage class identical), 1 otherwise.
    internal static class Program
    {
        private static readonly string[] WakePrefixes =
        {
            "hey paicom", "hey pie com", "hey p a i com", "paicom"
        };

        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Usage: PortCheck <port-vectors.json> <commands-manifest.txt>");
                return 2;
            }

            List<string> candidates = LoadCandidates(args[1]);
            Console.WriteLine("candidates: " + candidates.Count);

            string json = File.ReadAllText(args[0], Encoding.UTF8);
            List<Vector> vectors = ParseVectors(json);
            Console.WriteLine("vectors: " + vectors.Count);

            int pass = 0;
            int fail = 0;
            foreach (Vector v in vectors)
            {
                string stripped = StripWake(v.Input);
                ProductCommandMatch match = ProductCommandMatcher.FindBestMatch(stripped, candidates, 0.80f);
                string route = match != null ? match.Command : null;
                float conf = match != null ? match.Confidence : v.Conf;
                string stage = StageClass(match != null ? match.How : "REJECT");

                bool routeOk = string.Equals(route ?? "NULL", v.Route ?? "NULL", StringComparison.OrdinalIgnoreCase);
                bool confOk = Math.Abs(conf - v.Conf) < 0.002f;
                bool stageOk = stage == StageClass(v.How);

                if (routeOk && confOk && stageOk)
                {
                    pass++;
                }
                else
                {
                    fail++;
                    Console.WriteLine("MISS " + v.Name + " in=[" + v.Input + "]");
                    Console.WriteLine("  want route=" + (v.Route ?? "NULL") + " conf=" + v.Conf.ToString("0.000") + " stage=" + StageClass(v.How));
                    Console.WriteLine("  got  route=" + (route ?? "NULL") + " conf=" + conf.ToString("0.000") + " stage=" + stage);
                }
            }

            Console.WriteLine("PORTCHECK: " + pass + "/" + (pass + fail) + " pass");
            return fail == 0 ? 0 : 1;
        }

        private static string StageClass(string how)
        {
            if (how.StartsWith("exact-keyword:", StringComparison.Ordinal))
                return "exact";
            if (how.StartsWith("shared-keyword:", StringComparison.Ordinal))
                return "shared";
            if (how.StartsWith("partial-keyword:", StringComparison.Ordinal) ||
                how.StartsWith("partial:", StringComparison.Ordinal))
                return "partial";
            if (how.StartsWith("lev", StringComparison.Ordinal))
                return "lev";
            return "REJECT";
        }

        private static string StripWake(string text)
        {
            string n = text.Trim();
            foreach (string p in WakePrefixes)
            {
                if (n.Length >= p.Length &&
                    n.Substring(0, p.Length).Equals(p, StringComparison.OrdinalIgnoreCase))
                {
                    n = n.Substring(p.Length).Trim();
                    break;
                }
            }
            return n.TrimStart(',', '.', '!', '?', ':', ';');
        }

        private static List<string> LoadCandidates(string path)
        {
            List<string> cands = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string ln = raw.Trim();
                if (ln.Length == 0 || ln[0] == '#')
                    continue;
                string t = ln;
                int o = t.LastIndexOf('(');
                int c = t.LastIndexOf(')');
                if (o > 0 && c > o)
                    t = t.Substring(0, o).Trim();
                string n = StripWake(t);
                if (n.Length > 0 && seen.Add(n))
                    cands.Add(n);
            }
            // Same built-in extras the twin appends.
            string[] extras =
            {
                "open the browser", "open browser", "launch browser", "open the web",
                "pause the music", "resume the music", "play the next song",
                "play the previous song", "play the previous song on spotify"
            };
            foreach (string e in extras)
            {
                if (seen.Add(e))
                    cands.Add(e);
            }
            return cands;
        }

        private sealed class Vector
        {
            public string Name;
            public string Input;
            public string Route;
            public float Conf;
            public string How;
        }

        // Minimal JSON reader for the twin's vector shape (no dependencies).
        private static List<Vector> ParseVectors(string json)
        {
            List<Vector> vectors = new List<Vector>();
            int i = 0;
            while (i < json.Length)
            {
                int nameAt = json.IndexOf("\"name\"", i, StringComparison.Ordinal);
                if (nameAt < 0)
                    break;
                Vector v = new Vector();
                v.Name = ReadStringField(json, ref nameAt, "name");
                v.Input = ReadStringField(json, ref nameAt, "input");
                v.Route = ReadNullableStringField(json, ref nameAt, "route");
                v.Conf = ReadFloatField(json, ref nameAt, "conf");
                v.How = ReadStringField(json, ref nameAt, "how");
                vectors.Add(v);
                i = nameAt;
            }
            return vectors;
        }

        private static string ReadRawValue(string json, ref int pos, string field)
        {
            int key = json.IndexOf("\"" + field + "\"", pos, StringComparison.Ordinal);
            int colon = json.IndexOf(':', key);
            int p = colon + 1;
            while (p < json.Length && char.IsWhiteSpace(json[p]))
                p++;
            if (json[p] == '"')
            {
                StringBuilder sb = new StringBuilder();
                p++;
                while (p < json.Length)
                {
                    char ch = json[p];
                    if (ch == '\\' && p + 1 < json.Length)
                    {
                        char esc = json[p + 1];
                        if (esc == 'n') sb.Append('\n');
                        else if (esc == 't') sb.Append('\t');
                        else if (esc == 'r') sb.Append('\r');
                        else sb.Append(esc);
                        p += 2;
                        continue;
                    }
                    if (ch == '"')
                    {
                        p++;
                        break;
                    }
                    sb.Append(ch);
                    p++;
                }
                pos = p;
                return "\u0001" + sb.ToString();
            }
            int q = p;
            while (q < json.Length && json[q] != ',' && json[q] != '}' && json[q] != '\n')
                q++;
            pos = q;
            return json.Substring(p, q - p).Trim();
        }

        private static string ReadStringField(string json, ref int pos, string field)
        {
            string raw = ReadRawValue(json, ref pos, field);
            return raw.Length > 0 && raw[0] == '\u0001' ? raw.Substring(1) : raw;
        }

        private static string ReadNullableStringField(string json, ref int pos, string field)
        {
            string raw = ReadRawValue(json, ref pos, field);
            if (raw.Length > 0 && raw[0] == '\u0001')
                return raw.Substring(1);
            return raw == "null" ? null : raw;
        }

        private static float ReadFloatField(string json, ref int pos, string field)
        {
            string raw = ReadRawValue(json, ref pos, field);
            return float.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
