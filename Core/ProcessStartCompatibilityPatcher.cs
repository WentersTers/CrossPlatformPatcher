using System;
using System.Collections.Generic;
using System.Linq;
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
        var rewrittenString = 0;
        var rewrittenPsi = 0;
        MethodDef? safeStartMethod = null;
        MethodDef? safeStartPsiMethod = null;

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

                if (!IsProcessStartMethod(target))
                    continue;

                if (IsProcessStartStringOverload(target))
                {
                    safeStartMethod ??= EnsureSafeProcessStartMethod(module, target);
                    var contextInstruction = Instruction.Create(OpCodes.Ldstr, CreateCallSiteContext(method, i));
                    var insertionIndex = FindPrefixStartIndex(instrs, i);
                    instrs.Insert(insertionIndex, contextInstruction);
                    if (insertionIndex <= i)
                        i++;

                    instr = instrs[i];
                    instr.OpCode = OpCodes.Call;
                    instr.Operand = safeStartMethod;
                    rewrittenString++;
                }
                else if (IsProcessStartPsiOverload(target))
                {
                    safeStartPsiMethod ??= EnsureSafeProcessStartPsiMethod(module, target);
                    var contextInstruction = Instruction.Create(OpCodes.Ldstr, CreateCallSiteContext(method, i));
                    var insertionIndex = FindPrefixStartIndex(instrs, i);
                    instrs.Insert(insertionIndex, contextInstruction);
                    if (insertionIndex <= i)
                        i++;

                    instr = instrs[i];
                    instr.OpCode = OpCodes.Call;
                    instr.Operand = safeStartPsiMethod;
                    rewrittenPsi++;
                }
            }
        }

        log?.Invoke($"Process.Start(string) rewrites applied: {rewrittenString}");
        log?.Invoke($"Process.Start(ProcessStartInfo) rewrites applied: {rewrittenPsi}");
        return rewrittenString + rewrittenPsi;
    }

    private static string CreateCallSiteContext(MethodDef method, int instructionIndex)
    {
        var typeName = method.DeclaringType?.FullName ?? "<unknown-type>";
        return $"{typeName}::{method.Name}@IL_{instructionIndex}";
    }

    private static int FindPrefixStartIndex(IList<Instruction> instructions, int callIndex)
    {
        var index = callIndex;
        while (index > 0 && IsPrefixOpCode(instructions[index - 1].OpCode))
            index--;

        return index;
    }

    private static bool IsPrefixOpCode(OpCode opcode)
    {
        return opcode == OpCodes.Constrained ||
               opcode == OpCodes.Tailcall ||
               opcode == OpCodes.Volatile ||
               opcode == OpCodes.Readonly ||
               opcode == OpCodes.Unaligned;
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

    private static bool IsProcessStartMethod(IMethod target)
    {
        if (target.Name != "Start")
            return false;

        var declaringTypeName = target.DeclaringType?.FullName;
        if (!string.Equals(declaringTypeName, "System.Diagnostics.Process", StringComparison.Ordinal))
            return false;

        return target.MethodSig is not null && target.MethodSig.Params.Count == 1;
    }

    private static bool IsProcessStartPsiOverload(IMethod target)
    {
        if (target.Name != "Start")
            return false;

        if (target.MethodSig is null || target.MethodSig.Params.Count != 1)
            return false;

        // Check if parameter is ProcessStartInfo
        var paramType = target.MethodSig.Params[0];
        var paramFullName = paramType?.FullName;
        if (!string.Equals(paramFullName, "System.Diagnostics.ProcessStartInfo", StringComparison.Ordinal))
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
        var methodSig = MethodSig.CreateStatic(processSig, module.CorLibTypes.String, module.CorLibTypes.String);

        var method = new MethodDefUser(
            helperMethodName,
            methodSig,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        method.Body = BuildSafeStartBody(module, originalProcessStart, processSig, "PROCESS_START_STRING_SUPPRESSED");

        helperType.Methods.Add(method);
        return method;
    }

    private static MethodDef EnsureSafeProcessStartPsiMethod(ModuleDefMD module, IMethod originalProcessStart)
    {
        const string helperTypeName = "CrossPlatformPatcherCompat";
        const string helperMethodName = "SafeProcessStartPsi";

        var helperType = module.Types.FirstOrDefault(t => t.Name == helperTypeName)
            ?? CreateHelperType(module, helperTypeName);

        var existing = helperType.Methods.FirstOrDefault(m => m.Name == helperMethodName);
        if (existing is not null)
            return existing;

        var processType = originalProcessStart.DeclaringType;
        var processSig = new ClassSig(processType);
        
        // Find ProcessStartInfo type
        var psiTypeRef = module.CorLibTypes.GetTypeRef("System.Diagnostics", "ProcessStartInfo");
        var psiTypeSig = psiTypeRef.ToTypeSig();
        var methodSig = MethodSig.CreateStatic(processSig, psiTypeSig, module.CorLibTypes.String);

        var method = new MethodDefUser(
            helperMethodName,
            methodSig,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        method.Body = BuildSafeStartBody(module, originalProcessStart, processSig, "PROCESS_START_PSI_SUPPRESSED");

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

    private static CilBody BuildSafeStartBody(ModuleDefMD module, IMethod originalProcessStart, TypeSig processSig, string reasonCode)
    {
        var body = new CilBody { InitLocals = true, MaxStack = 4 };

        var resultLocal = new Local(processSig);
        var exceptionLocal = new Local(module.CorLibTypes.GetTypeRef("System", "Exception").ToTypeSig());
        var messageLocal = new Local(module.CorLibTypes.String);
        body.Variables.Add(resultLocal);
        body.Variables.Add(exceptionLocal);
        body.Variables.Add(messageLocal);

        var stringTypeRef = module.CorLibTypes.String.TypeDefOrRef;
        var objectTypeRef = module.CorLibTypes.Object.TypeDefOrRef;
        var consoleTypeRef = module.CorLibTypes.GetTypeRef("System", "Console");
        var convertTypeRef = module.CorLibTypes.GetTypeRef("System", "Convert");

        var concatRef = new MemberRefUser(
            module,
            "Concat",
            MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String),
            stringTypeRef);

        var convertToStringRef = new MemberRefUser(
            module,
            "ToString",
            MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.Object),
            convertTypeRef);

        var objectToStringRef = new MemberRefUser(
            module,
            "ToString",
            MethodSig.CreateInstance(module.CorLibTypes.String),
            objectTypeRef);

        var consoleWriteLineRef = new MemberRefUser(
            module,
            "WriteLine",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String),
            consoleTypeRef);

        var i0 = Instruction.Create(OpCodes.Ldarg_0);
        var i1 = Instruction.Create(OpCodes.Call, originalProcessStart);
        var i2 = Instruction.Create(OpCodes.Stloc, resultLocal);
        var i3 = Instruction.Create(OpCodes.Leave_S, Instruction.Create(OpCodes.Nop));

        var i4 = Instruction.Create(OpCodes.Stloc, exceptionLocal);
        var i5 = Instruction.Create(OpCodes.Ldstr, "[oww-process-start] reason.code=");
        var i6 = Instruction.Create(OpCodes.Ldstr, reasonCode);
        var i7 = Instruction.Create(OpCodes.Call, concatRef);
        var i8 = Instruction.Create(OpCodes.Ldstr, " callsite=");
        var i9 = Instruction.Create(OpCodes.Call, concatRef);
        var i10 = Instruction.Create(OpCodes.Ldarg_1);
        var i11 = Instruction.Create(OpCodes.Call, concatRef);
        var i12 = Instruction.Create(OpCodes.Ldstr, " arg=");
        var i13 = Instruction.Create(OpCodes.Call, concatRef);
        var i14 = Instruction.Create(OpCodes.Ldarg_0);
        var i15 = Instruction.Create(OpCodes.Call, convertToStringRef);
        var i16 = Instruction.Create(OpCodes.Call, concatRef);
        var i17 = Instruction.Create(OpCodes.Ldstr, " ex=");
        var i18 = Instruction.Create(OpCodes.Call, concatRef);
        var i19 = Instruction.Create(OpCodes.Ldloc, exceptionLocal);
        var i20 = Instruction.Create(OpCodes.Callvirt, objectToStringRef);
        var i21 = Instruction.Create(OpCodes.Call, concatRef);
        var i22 = Instruction.Create(OpCodes.Stloc, messageLocal);
        var i23 = Instruction.Create(OpCodes.Ldloc, messageLocal);
        var i24 = Instruction.Create(OpCodes.Call, consoleWriteLineRef);
        var i25 = Instruction.Create(OpCodes.Ldnull);
        var i26 = Instruction.Create(OpCodes.Stloc, resultLocal);
        var i27 = Instruction.Create(OpCodes.Leave_S, Instruction.Create(OpCodes.Nop));

        var i28 = Instruction.Create(OpCodes.Nop);
        var i29 = Instruction.Create(OpCodes.Ldloc, resultLocal);
        var i30 = Instruction.Create(OpCodes.Ret);

        i3.Operand = i28;
        i27.Operand = i28;

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
        body.Instructions.Add(i11);
        body.Instructions.Add(i12);
        body.Instructions.Add(i13);
        body.Instructions.Add(i14);
        body.Instructions.Add(i15);
        body.Instructions.Add(i16);
        body.Instructions.Add(i17);
        body.Instructions.Add(i18);
        body.Instructions.Add(i19);
        body.Instructions.Add(i20);
        body.Instructions.Add(i21);
        body.Instructions.Add(i22);
        body.Instructions.Add(i23);
        body.Instructions.Add(i24);
        body.Instructions.Add(i25);
        body.Instructions.Add(i26);
        body.Instructions.Add(i27);
        body.Instructions.Add(i28);
        body.Instructions.Add(i29);
        body.Instructions.Add(i30);

        var exceptionType = module.CorLibTypes.GetTypeRef("System", "Exception");
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = i0,
            TryEnd = i4,
            HandlerStart = i4,
            HandlerEnd = i28,
            CatchType = exceptionType
        });

        body.OptimizeBranches();
        body.OptimizeMacros();

        return body;
    }
}
