using System.Resources;

// resprobe — dual-mount resource-resolution probe (acceptance check (f)).
// Verifies that every expected form `.resources` name resolves through a
// mounted directory: each file must open as a valid resource set with at
// least one entry. No product identifiers live in this tool; the expected
// name list is supplied per run (restricted channel).
// Usage: resprobe <mount-dir> <names.txt>   (exit 0 iff ALL resolve)
if (args.Length < 2) { Console.WriteLine("usage: resprobe <mount-dir> <names.txt>"); return 2; }
string dir = args[0];
var names = File.ReadAllLines(args[1]).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
int ok = 0;
foreach (var name in names)
{
    string path = Path.Combine(dir, name);
    try
    {
        using var fs = File.OpenRead(path);
        using var rr = new ResourceReader(fs);
        int entries = 0;
        foreach (System.Collections.DictionaryEntry _ in rr) entries++;
        if (entries > 0) { Console.WriteLine($"RESOLVE-OK   | {name} | entries={entries}"); ok++; }
        else Console.WriteLine($"RESOLVE-EMPTY| {name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"RESOLVE-FAIL | {name} | {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
    }
}
Console.WriteLine($"# SUMMARY resolved={ok}/{names.Count}");
Console.WriteLine(ok == names.Count && names.Count > 0 ? "PROBE: PASS (all listed resources resolve)" : "PROBE: REVIEW (stop before publish)");
return (ok == names.Count && names.Count > 0) ? 0 : 1;
