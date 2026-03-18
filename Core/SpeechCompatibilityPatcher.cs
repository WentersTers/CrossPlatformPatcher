using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Wraps methods that touch System.Speech in a safety catch so missing recognizers
/// under Wine/macOS cannot crash the WinForms startup path.
/// </summary>
public static class SpeechCompatibilityPatcher
{
    public static int Patch(ModuleDefMD module, Action<string>? log = null)
    {
        var patched = 0;

        var methods = module.GetTypes()
            .SelectMany(t => t.Methods)
            .Where(m => m.HasBody)
            .ToList();

        foreach (var method in methods)
        {
            if (!ReferencesSystemSpeech(method))
                continue;

            if (method.Body.ExceptionHandlers.Count > 0)
                continue;

            if (TryWrapMethodWithCatch(module, method))
                patched++;
        }

        log?.Invoke($"System.Speech safety wrappers applied: {patched}");
        return patched;
    }

    private static bool ReferencesSystemSpeech(MethodDef method)
    {
        if (!method.HasBody)
            return false;

        foreach (var instr in method.Body.Instructions)
        {
            if (instr.Operand is IMethod m)
            {
                var dt = m.DeclaringType?.FullName;
                if (!string.IsNullOrEmpty(dt) && dt.StartsWith("System.Speech.", StringComparison.Ordinal))
                    return true;
            }

            if (instr.Operand is IField f)
            {
                var dt = f.DeclaringType?.FullName;
                if (!string.IsNullOrEmpty(dt) && dt.StartsWith("System.Speech.", StringComparison.Ordinal))
                    return true;
            }

            if (instr.Operand is ITypeDefOrRef t)
            {
                var fullName = t.FullName;
                if (!string.IsNullOrEmpty(fullName) && fullName.StartsWith("System.Speech.", StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static bool TryWrapMethodWithCatch(ModuleDefMD module, MethodDef method)
    {
        var body = method.Body;
        if (body.Instructions.Count == 0)
            return false;

        body.SimplifyBranches();
        body.SimplifyMacros(method.Parameters);

        var instructions = body.Instructions;
        var originalFirst = instructions[0];

        var retType = method.MethodSig.RetType;
        var isVoid = retType.GetElementType() == ElementType.Void;

        Local? retLocal = null;
        if (!isVoid)
        {
            retLocal = new Local(retType);
            body.Variables.Add(retLocal);
        }

        var finalReturnNop = Instruction.Create(OpCodes.Nop);

        // Rewrite all returns to leave the protected region via a single exit.
        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            if (instr.OpCode != OpCodes.Ret)
                continue;

            if (isVoid)
            {
                instr.OpCode = OpCodes.Leave;
                instr.Operand = finalReturnNop;
            }
            else
            {
                var storeRet = Instruction.Create(OpCodes.Stloc, retLocal);
                instructions.Insert(i, storeRet);
                i++;

                instr.OpCode = OpCodes.Leave;
                instr.Operand = finalReturnNop;
            }
        }

        var catchStart = Instruction.Create(OpCodes.Pop);
        var catchLeave = Instruction.Create(OpCodes.Leave, finalReturnNop);

        instructions.Add(catchStart);

        if (!isVoid)
        {
            if (retType.IsValueType)
            {
                instructions.Add(Instruction.Create(OpCodes.Ldloca, retLocal));
                instructions.Add(Instruction.Create(OpCodes.Initobj, retType.ToTypeDefOrRef()));
            }
            else
            {
                instructions.Add(Instruction.Create(OpCodes.Ldnull));
                instructions.Add(Instruction.Create(OpCodes.Stloc, retLocal));
            }
        }

        instructions.Add(catchLeave);
        instructions.Add(finalReturnNop);

        if (!isVoid)
            instructions.Add(Instruction.Create(OpCodes.Ldloc, retLocal));

        instructions.Add(Instruction.Create(OpCodes.Ret));

        var exceptionType = module.CorLibTypes.GetTypeRef("System", "Exception");
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = originalFirst,
            TryEnd = catchStart,
            HandlerStart = catchStart,
            HandlerEnd = finalReturnNop,
            CatchType = exceptionType
        });

        body.OptimizeBranches();
        body.OptimizeMacros();
        return true;
    }
}
