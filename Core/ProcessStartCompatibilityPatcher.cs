using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Rewrites calls to Process.Start(string) so startup does not crash under Wine/macOS
/// when shell/environment integration is unavailable.
/// </summary>
public static class ProcessStartCompatibilityPatcher
{
    public static int Patch(ModuleDefMD module, Action<string>? log = null)
    {
        var rewritten = 0;
        MethodDef? safeStartMethod = null;

        // Snapshot methods first because we may inject a helper type during patching,
        // and mutating module.Types while iterating module.GetTypes() triggers dnlib
        // "List was modified" exceptions.
        var methods = module.GetTypes()
            .SelectMany(t => t.Methods)
            .Where(m => m.HasBody)
            .ToList();

        foreach (var method in methods)
        {
            var body = method.Body;
            var instrs = body.Instructions;

            for (int i = 0; i < instrs.Count; i++)
            {
                var instr = instrs[i];
                if (instr.OpCode != OpCodes.Call && instr.OpCode != OpCodes.Callvirt)
                    continue;

                if (instr.Operand is not IMethod target)
                    continue;

                if (!IsProcessStartStringOverload(target))
                    continue;

                safeStartMethod ??= EnsureSafeProcessStartMethod(module, target);

                instr.OpCode = OpCodes.Call;
                instr.Operand = safeStartMethod;
                rewritten++;
            }
        }

        log?.Invoke($"Process.Start(string) rewrites applied: {rewritten}");
        return rewritten;
    }

    private static bool IsProcessStartStringOverload(IMethod target)
    {
        if (target.Name != "Start")
            return false;

        if (target.MethodSig is null || target.MethodSig.Params.Count != 1)
            return false;

        if (target.MethodSig.Params[0].GetElementType() != ElementType.String)
            return false;

        var declaringTypeName = target.DeclaringType?.FullName;
        return string.Equals(declaringTypeName, "System.Diagnostics.Process", StringComparison.Ordinal);
    }

    private static MethodDef EnsureSafeProcessStartMethod(ModuleDefMD module, IMethod originalProcessStart)
    {
        const string helperTypeName = "CrossPlatformPatcherCompat";
        const string helperMethodName = "SafeProcessStart";

        var helperType = module.Types.FirstOrDefault(t => t.Name == helperTypeName)
            ?? CreateHelperType(module, helperTypeName);

        var existing = helperType.Methods.FirstOrDefault(m => m.Name == helperMethodName);
        if (existing is not null)
            return existing;

        var processType = originalProcessStart.DeclaringType;
        var processSig = new ClassSig(processType);
        var methodSig = MethodSig.CreateStatic(processSig, module.CorLibTypes.String);

        var method = new MethodDefUser(
            helperMethodName,
            methodSig,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        method.Body = BuildSafeStartBody(module, originalProcessStart, processSig);

        helperType.Methods.Add(method);
        return method;
    }

    private static TypeDef CreateHelperType(ModuleDefMD module, string helperTypeName)
    {
        var type = new TypeDefUser(
            string.Empty,
            helperTypeName,
            module.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = TypeAttributes.NotPublic |
                         TypeAttributes.AutoLayout |
                         TypeAttributes.AnsiClass |
                         TypeAttributes.Class |
                         TypeAttributes.Sealed |
                         TypeAttributes.Abstract
        };

        module.Types.Add(type);
        return type;
    }

    private static CilBody BuildSafeStartBody(ModuleDefMD module, IMethod originalProcessStart, TypeSig processSig)
    {
        var body = new CilBody { InitLocals = true, MaxStack = 2 };

        var resultLocal = new Local(processSig);
        body.Variables.Add(resultLocal);

        var i0 = Instruction.Create(OpCodes.Ldarg_0);
        var i1 = Instruction.Create(OpCodes.Call, originalProcessStart);
        var i2 = Instruction.Create(OpCodes.Stloc, resultLocal);
        var i3 = Instruction.Create(OpCodes.Leave_S, Instruction.Create(OpCodes.Nop));

        var i4 = Instruction.Create(OpCodes.Pop);
        var i5 = Instruction.Create(OpCodes.Ldnull);
        var i6 = Instruction.Create(OpCodes.Stloc, resultLocal);
        var i7 = Instruction.Create(OpCodes.Leave_S, Instruction.Create(OpCodes.Nop));

        var i8 = Instruction.Create(OpCodes.Nop);
        var i9 = Instruction.Create(OpCodes.Ldloc, resultLocal);
        var i10 = Instruction.Create(OpCodes.Ret);

        i3.Operand = i8;
        i7.Operand = i8;

        body.Instructions.Add(i0);
        body.Instructions.Add(i1);
        body.Instructions.Add(i2);
        body.Instructions.Add(i3);
        body.Instructions.Add(i4);
        body.Instructions.Add(i5);
        body.Instructions.Add(i6);
        body.Instructions.Add(i7);
        body.Instructions.Add(i8);
        body.Instructions.Add(i9);
        body.Instructions.Add(i10);

        var exceptionType = module.CorLibTypes.GetTypeRef("System", "Exception");
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = i0,
            TryEnd = i4,
            HandlerStart = i4,
            HandlerEnd = i8,
            CatchType = exceptionType
        });

        body.OptimizeBranches();
        body.OptimizeMacros();

        return body;
    }
}
