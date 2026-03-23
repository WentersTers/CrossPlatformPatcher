using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Provides fuzzy IL-level pattern matching so we can locate methods by
/// characteristic instruction sequences even after obfuscation renames
/// every identifier.
///
/// Each <see cref="ILPattern"/> is a list of <see cref="ILConstraint"/>
/// objects. A constraint can match:
///   - A specific opcode (exact).
///   - Any opcode from a set.
///   - Any single instruction (wildcard).
///   - An opcode with a specific string operand.
///
/// The scanner uses a sliding window – one window per candidate starting
/// position – so partial patterns anywhere in the method body are found.
/// </summary>
public class ILPatternScanner
{
    // ── Pattern primitives ────────────────────────────────────────────────

    /// <summary>A single constraint in an IL pattern.</summary>
    public record ILConstraint
    {
        public static readonly ILConstraint Any = new ILConstraint(); // wildcard

        public OpCode? Opcode     { get; init; }   // null → any opcode
        public HashSet<OpCode>? OpcodeSet { get; init; }
        public string? StringOperand { get; init; } // null → any operand

        public bool Matches(Instruction instr)
        {
            if (Opcode is not null && instr.OpCode != Opcode)
                return false;

            if (OpcodeSet is not null && !OpcodeSet.Contains(instr.OpCode))
                return false;

            if (StringOperand is not null)
            {
                if (instr.Operand is not string s || s != StringOperand)
                    return false;
            }

            return true;
        }
    }

    /// <summary>A named IL pattern.</summary>
    public record ILPattern(string Name, IReadOnlyList<ILConstraint> Constraints);

    // ── Factory helpers ───────────────────────────────────────────────────

    public static ILConstraint Op(OpCode op)            => new() { Opcode = op };
    public static ILConstraint Ops(params OpCode[] ops) => new() { OpcodeSet = [..ops] };
    public static ILConstraint LdStr(string s)          => new() { Opcode = OpCodes.Ldstr, StringOperand = s };
    public static ILConstraint Wild()                   => ILConstraint.Any;

    // ── Known patterns ─────────────────────────────────────────────────────

    /// <summary>
    /// Pattern A – command dispatch.
    /// Looks for a string load followed by a string comparison (Equals or
    /// op_Equality) with a callvirt: typical shape of a command handler
    /// that loads the command name, compares it to a literal, then dispatches.
    /// </summary>
    public static ILPattern CommandDispatchPattern => new("CommandDispatch",
    [
        Op(OpCodes.Ldstr),          // load a command-name literal
        Wild(),                     // push second operand (local or arg)
        Ops(OpCodes.Call, OpCodes.Callvirt),   // string comparison call
        Ops(OpCodes.Brfalse, OpCodes.Brfalse_S, OpCodes.Brtrue, OpCodes.Brtrue_S),
    ]);

    /// <summary>
    /// Pattern B – animation/command table population.
    /// Looks for repeated "ldstr / stelem or call" pairs that build a list
    /// or array of command strings.
    /// </summary>
    public static ILPattern CommandTablePopulatePattern => new("CommandTablePopulate",
    [
        Op(OpCodes.Ldstr),
        Wild(),
        Wild(),
        Ops(OpCodes.Stelem_Ref, OpCodes.Call, OpCodes.Callvirt),
        Op(OpCodes.Ldstr),    // second literal right after → it's a table build
    ]);

    /// <summary>
    /// Pattern C – main-loop entry.
    /// Looks for an Update() / Tick() shape: a call to Time.deltaTime or
    /// a comparison with a frame counter.
    /// </summary>
    public static ILPattern MainLoopPattern => new("MainLoop",
    [
        Ops(OpCodes.Call, OpCodes.Callvirt),
        Ops(OpCodes.Ldc_R4, OpCodes.Ldc_R8),
        Ops(OpCodes.Bge, OpCodes.Ble, OpCodes.Bge_Un, OpCodes.Bge_S, OpCodes.Bge_Un_S),
    ]);

    // ── Scanner ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all (method, offset) pairs where the given pattern matches
    /// inside the method body.
    /// </summary>
    public static IEnumerable<PatternMatch> FindPattern(
        IEnumerable<MethodDef> methods,
        ILPattern pattern,
        bool stopAfterFirst = false)
    {
        var constraints = pattern.Constraints;
        int patLen      = constraints.Count;

        foreach (var method in methods)
        {
            if (!method.HasBody) continue;
            var instrs = method.Body.Instructions;
            int count  = instrs.Count;

            for (int i = 0; i <= count - patLen; i++)
            {
                bool ok = true;
                for (int j = 0; j < patLen; j++)
                {
                    if (!constraints[j].Matches(instrs[i + j]))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                {
                    yield return new PatternMatch(method, i, instrs[i], pattern.Name);
                    if (stopAfterFirst) yield break;
                }
            }
        }
    }

    /// <summary>
    /// Score a method by how many distinct patterns from the list it satisfies.
    /// The method with the highest score is the best candidate.
    /// </summary>
    public static MethodDef? BestCandidate(
        IEnumerable<MethodDef> methods,
        params ILPattern[] patterns)
    {
        var scores = new Dictionary<MethodDef, int>();
        var methodList = methods.ToList();

        foreach (var pattern in patterns)
        {
            foreach (var match in FindPattern(methodList, pattern))
            {
                scores.TryGetValue(match.Method, out int prev);
                scores[match.Method] = prev + 1;
            }
        }

        return scores.OrderByDescending(kvp => kvp.Value).FirstOrDefault().Key;
    }
}

/// <summary>A located pattern match inside a method.</summary>
public record PatternMatch(
    MethodDef Method,
    int       InstructionIndex,
    Instruction FirstInstruction,
    string    PatternName);

