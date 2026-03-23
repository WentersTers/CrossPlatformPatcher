using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Produces a ranked report of every method in the assembly so the user can
/// identify the command/animation handler even when all strings are encrypted.
///
/// Run via:  dotnet run -- PAIcom.exe --analyze
/// </summary>
public static class AssemblyAnalyzer
{
    public static void Analyze(string inputPath, int topN = 40)
    {
        Console.WriteLine($"Loading {inputPath} for analysis …");

        byte[] peBytes;
        using (var fs = new System.IO.FileStream(inputPath,
                System.IO.FileMode.Open, System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite))
        {
            peBytes = new byte[fs.Length];
            fs.ReadExactly(peBytes);
        }

        var ctx    = ModuleDef.CreateModuleContext();
        var module = ModuleDefMD.Load(peBytes, ctx);

        var rows = new List<MethodRow>();

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;

                var instrs     = method.Body.Instructions;
                int total      = instrs.Count;
                int ldstrCount = instrs.Count(i => i.OpCode == OpCodes.Ldstr);
                int callCount  = instrs.Count(i =>
                    i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt);
                int brCount    = instrs.Count(i =>
                    i.OpCode.FlowControl == FlowControl.Cond_Branch ||
                    i.OpCode.FlowControl == FlowControl.Branch);
                int ldlocCount = instrs.Count(i =>
                    i.OpCode == OpCodes.Ldloc   || i.OpCode == OpCodes.Ldloc_0 ||
                    i.OpCode == OpCodes.Ldloc_1 || i.OpCode == OpCodes.Ldloc_2 ||
                    i.OpCode == OpCodes.Ldloc_3 || i.OpCode == OpCodes.Ldloc_S);
                int stfldCount = instrs.Count(i =>
                    i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Ldfld);
                int ldc_i4    = instrs.Count(i =>
                    i.OpCode == OpCodes.Ldc_I4   || i.OpCode == OpCodes.Ldc_I4_0 ||
                    i.OpCode == OpCodes.Ldc_I4_1 || i.OpCode == OpCodes.Ldc_I4_S ||
                    i.OpCode == OpCodes.Ldc_I4_M1);

                // Extract any plain string literals (may be empty if encrypted)
                var strings = instrs
                    .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string)
                    .Select(i => (string)i.Operand!)
                    .Distinct()
                    .Take(5)
                    .ToList();

                rows.Add(new MethodRow(
                    FullName:      method.FullName,
                    ShortName:     method.Name,
                    TypeName:      type.Name,
                    TotalInstrs:   total,
                    LdstrCount:    ldstrCount,
                    CallCount:     callCount,
                    BranchCount:   brCount,
                    LdlocCount:    ldlocCount,
                    FieldCount:    stfldCount,
                    Ldc_i4Count:   ldc_i4,
                    Strings:       strings,
                    HasReturn:     instrs.Any(i => i.OpCode == OpCodes.Ret),
                    IsStatic:      method.IsStatic,
                    IsCtor:        method.IsConstructor));
            }
        }

        int total_methods = rows.Count;
        Console.WriteLine($"Total methods with bodies: {total_methods}");
        Console.WriteLine();

        // ── Report 1: By instruction count (largest first = complex logic) ─
        PrintTable("TOP METHODS BY INSTRUCTION COUNT", rows
            .OrderByDescending(r => r.TotalInstrs)
            .Take(topN), showStrings: true);

        // ── Report 2: By branch density (highest = most switch/dispatch) ───
        PrintTable("TOP METHODS BY BRANCH DENSITY (dispatch candidates)", rows
            .Where(r => r.TotalInstrs > 20)
            .OrderByDescending(r => (double)r.BranchCount / r.TotalInstrs)
            .Take(topN), showStrings: true);

        // ── Report 3: By call count (highest = orchestrators / handlers) ───
        PrintTable("TOP METHODS BY CALL COUNT", rows
            .OrderByDescending(r => r.CallCount)
            .Take(topN), showStrings: false);

        // ── Report 4: Methods with plain string literals ──────────────────
        var withStrings = rows.Where(r => r.LdstrCount > 0)
                              .OrderByDescending(r => r.LdstrCount)
                              .Take(topN)
                              .ToList();

        if (withStrings.Count > 0)
        {
            PrintTable("METHODS WITH PLAIN STRING LITERALS (may be unencrypted)", withStrings, showStrings: true);
        }
        else
        {
            Console.WriteLine(">>> No plain ldstr literals found – strings are fully encrypted (ConfuserEx). <<<");
            Console.WriteLine("    Use dnSpy to decrypt and view string values at runtime.");
            Console.WriteLine();
        }

        // ── Report 5: ldc.i4 heavy  (integer look-up tables / enum dispatch)
        PrintTable("TOP METHODS BY INTEGER CONSTANT LOAD (enum/table dispatch)", rows
            .Where(r => r.Ldc_i4Count > 0)
            .OrderByDescending(r => r.Ldc_i4Count)
            .Take(topN), showStrings: false);
    }

    // ── Formatting ────────────────────────────────────────────────────────

    private static void PrintTable(string title, IEnumerable<MethodRow> rows, bool showStrings)
    {
        Console.WriteLine($"══ {title} ══");
        Console.WriteLine($"  {"#",3}  {"Total",5}  {"Calls",5}  {"Brnch",5}  {"Ldloc",5}  {"Fld",4}  {"I4",4}  {"Str",3}  {"Type / Method"}");
        Console.WriteLine($"  {"───",3}  {"─────",5}  {"─────",5}  {"─────",5}  {"─────",5}  {"────",4}  {"────",4}  {"───",3}  {"──────────"}");

        int n = 1;
        foreach (var r in rows)
        {
            // Trim display name to something readable (obfuscated names are long Unicode runs)
            var displayType   = TrimName(r.TypeName,   25);
            var displayMethod = TrimName(r.ShortName,  30);
            var flags = $"{(r.IsStatic ? "S" : "I")}{(r.IsCtor ? "C" : " ")}";

            Console.WriteLine(
                $"  {n,3}  {r.TotalInstrs,5}  {r.CallCount,5}  {r.BranchCount,5}  " +
                $"{r.LdlocCount,5}  {r.FieldCount,4}  {r.Ldc_i4Count,4}  {r.LdstrCount,3}  " +
                $"[{flags}] {displayType}::{displayMethod}");

            if (showStrings && r.Strings.Count > 0)
            {
                foreach (var s in r.Strings)
                    Console.WriteLine($"       \"{s}\"");
            }
            n++;
        }
        Console.WriteLine();
    }

    private static string TrimName(string name, int maxLen)
    {
        if (name.Length <= maxLen) return name;
        return name[..maxLen] + "…";
    }

    private record MethodRow(
        string FullName,
        string ShortName,
        string TypeName,
        int    TotalInstrs,
        int    LdstrCount,
        int    CallCount,
        int    BranchCount,
        int    LdlocCount,
        int    FieldCount,
        int    Ldc_i4Count,
        List<string> Strings,
        bool   HasReturn,
        bool   IsStatic,
        bool   IsCtor);
}

