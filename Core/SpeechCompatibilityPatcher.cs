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
        var eventLogs = 0;
        MethodDef? logMethod = null;
        MethodDef? eventLogMethod = null;
        MethodDef? eventDetailLogMethod = null;

        var methods = module.GetTypes()
            .SelectMany(t => t.Methods)
            .Where(m => m.HasBody)
            .ToList();

        foreach (var method in methods)
        {
            if (HasSpeechRecognitionEventArg(method))
            {
                eventLogMethod ??= EnsureSpeechEventLogMethod(module);
                eventDetailLogMethod ??= EnsureSpeechEventDetailLogMethod(module);
                if (TryInjectSpeechEventLog(method, eventLogMethod, eventDetailLogMethod))
                    eventLogs++;
            }

            if (!ReferencesSystemSpeech(method))
                continue;

            if (method.Body.ExceptionHandlers.Count > 0)
                continue;

            logMethod ??= EnsureSpeechLogMethod(module);

            if (TryWrapMethodWithCatch(module, method, logMethod))
                patched++;
        }

        log?.Invoke($"System.Speech safety wrappers applied: {patched}");
        log?.Invoke($"System.Speech event probes applied: {eventLogs}");
        return patched;
    }

    private static bool HasSpeechRecognitionEventArg(MethodDef method)
    {
        if (!method.HasBody)
            return false;

        foreach (var param in method.Parameters)
        {
            if (param.IsHiddenThisParameter)
                continue;

            var fullName = param.Type?.FullName;
            if (string.Equals(fullName, "System.Speech.Recognition.SpeechRecognizedEventArgs", StringComparison.Ordinal) ||
                string.Equals(fullName, "System.Speech.Recognition.SpeechHypothesizedEventArgs", StringComparison.Ordinal) ||
                string.Equals(fullName, "System.Speech.Recognition.RecognizeCompletedEventArgs", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryInjectSpeechEventLog(MethodDef method, IMethod eventLogMethod, IMethod eventDetailLogMethod)
    {
        if (!method.HasBody || method.Body.Instructions.Count == 0)
            return false;

        var body = method.Body;
        var instrs = body.Instructions;

        // Idempotence for re-patching: skip if already instrumented.
        for (int i = 0; i < instrs.Count; i++)
        {
            if (instrs[i].OpCode == OpCodes.Call &&
                instrs[i].Operand is IMethod called &&
                called.Name == "LogSpeechEvent")
            {
                return false;
            }
        }

        var first = instrs[0];
        var methodLabel = GetMethodLogLabel(method);

        instrs.Insert(0, Instruction.Create(OpCodes.Ldstr, methodLabel));
        instrs.Insert(1, Instruction.Create(OpCodes.Call, eventLogMethod));

        var eventArgParam = method.Parameters
            .FirstOrDefault(p => !p.IsHiddenThisParameter && IsSpeechRecognitionEventArgType(p.Type?.FullName));

        if (eventArgParam is not null)
        {
            var eventArgIlIndex = method.IsStatic ? eventArgParam.MethodSigIndex : eventArgParam.MethodSigIndex + 1;
            if (eventArgIlIndex >= 0 && eventArgIlIndex < method.Parameters.Count)
            {
                instrs.Insert(2, Instruction.Create(OpCodes.Ldstr, methodLabel));
                instrs.Insert(3, Instruction.Create(OpCodes.Ldarg, method.Parameters[eventArgIlIndex]));
                instrs.Insert(4, Instruction.Create(OpCodes.Call, eventDetailLogMethod));
            }
        }

        // Expand existing handlers to include the newly inserted probe if needed.
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.TryStart == first)
                eh.TryStart = instrs[0];
            if (eh.HandlerStart == first)
                eh.HandlerStart = instrs[0];
            if (eh.FilterStart == first)
                eh.FilterStart = instrs[0];
        }

        body.OptimizeBranches();
        body.OptimizeMacros();
        return true;
    }

    private static bool IsSpeechRecognitionEventArgType(string? fullName)
    {
        return string.Equals(fullName, "System.Speech.Recognition.SpeechRecognizedEventArgs", StringComparison.Ordinal) ||
               string.Equals(fullName, "System.Speech.Recognition.SpeechHypothesizedEventArgs", StringComparison.Ordinal) ||
               string.Equals(fullName, "System.Speech.Recognition.RecognizeCompletedEventArgs", StringComparison.Ordinal);
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

    private static bool TryWrapMethodWithCatch(ModuleDefMD module, MethodDef method, IMethod logMethod)
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

        var exceptionLocal = new Local(module.CorLibTypes.GetTypeRef("System", "Exception").ToTypeSig());
        body.Variables.Add(exceptionLocal);

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

        var catchStart = Instruction.Create(OpCodes.Stloc, exceptionLocal);
        var catchLeave = Instruction.Create(OpCodes.Leave, finalReturnNop);
        var methodLabel = GetMethodLogLabel(method);

        instructions.Add(catchStart);
        instructions.Add(Instruction.Create(OpCodes.Ldstr, methodLabel));
        instructions.Add(Instruction.Create(OpCodes.Ldloc, exceptionLocal));
        instructions.Add(Instruction.Create(OpCodes.Call, logMethod));

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

    private static MethodDef EnsureSpeechLogMethod(ModuleDefMD module)
    {
        const string helperTypeName = "CrossPlatformPatcherCompat";
        const string helperMethodName = "LogSuppressedSpeechException";

        var helperType = module.Types.FirstOrDefault(t => t.Name == helperTypeName)
            ?? CreateHelperType(module, helperTypeName);

        var existing = helperType.Methods.FirstOrDefault(m => m.Name == helperMethodName);
        if (existing is not null)
            return existing;

        var exceptionSig = module.CorLibTypes.GetTypeRef("System", "Exception").ToTypeSig();
        var methodSig = MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String, exceptionSig);

        var method = new MethodDefUser(
            helperMethodName,
            methodSig,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        method.Body = BuildSpeechLogMethodBody(module);
        helperType.Methods.Add(method);
        return method;
    }

    private static MethodDef EnsureSpeechEventLogMethod(ModuleDefMD module)
    {
        const string helperTypeName = "CrossPlatformPatcherCompat";
        const string helperMethodName = "LogSpeechEvent";

        var helperType = module.Types.FirstOrDefault(t => t.Name == helperTypeName)
            ?? CreateHelperType(module, helperTypeName);

        var existing = helperType.Methods.FirstOrDefault(m => m.Name == helperMethodName);
        if (existing is not null)
            return existing;

        var methodSig = MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String);
        var method = new MethodDefUser(
            helperMethodName,
            methodSig,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        method.Body = BuildSpeechEventLogMethodBody(module);
        helperType.Methods.Add(method);
        return method;
    }

    private static MethodDef EnsureSpeechEventDetailLogMethod(ModuleDefMD module)
    {
        const string helperTypeName = "CrossPlatformPatcherCompat";
        const string helperMethodName = "LogSpeechEventDetail";

        var helperType = module.Types.FirstOrDefault(t => t.Name == helperTypeName)
            ?? CreateHelperType(module, helperTypeName);

        var existing = helperType.Methods.FirstOrDefault(m => m.Name == helperMethodName);
        if (existing is not null)
            return existing;

        var methodSig = MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String, module.CorLibTypes.Object);
        var method = new MethodDefUser(
            helperMethodName,
            methodSig,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        method.Body = BuildSpeechEventDetailLogMethodBody(module);
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

    private static CilBody BuildSpeechLogMethodBody(ModuleDefMD module)
    {
        var body = new CilBody { InitLocals = false, MaxStack = 3 };

        var consoleType = module.CorLibTypes.GetTypeRef("System", "Console");
        var stringType = module.CorLibTypes.String.TypeDefOrRef;

        var writeLineString = new MemberRefUser(
            module,
            "WriteLine",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String),
            consoleType);

        var concat2 = new MemberRefUser(
            module,
            "Concat",
            MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String),
            stringType);

        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "[compat][speech][fallback] System.Speech unavailable in: "));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, concat2));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, writeLineString));
        body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        body.OptimizeBranches();
        body.OptimizeMacros();
        return body;
    }

    private static CilBody BuildSpeechEventLogMethodBody(ModuleDefMD module)
    {
        var body = new CilBody { InitLocals = false, MaxStack = 3 };

        var consoleType = module.CorLibTypes.GetTypeRef("System", "Console");
        var stringType = module.CorLibTypes.String.TypeDefOrRef;

        var writeLineString = new MemberRefUser(
            module,
            "WriteLine",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String),
            consoleType);

        var concat2 = new MemberRefUser(
            module,
            "Concat",
            MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String),
            stringType);

        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "[compat][speech] Event fired (wake->vosk path): "));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, concat2));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, writeLineString));
        body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        body.OptimizeBranches();
        body.OptimizeMacros();
        return body;
    }

    private static CilBody BuildSpeechEventDetailLogMethodBody(ModuleDefMD module)
    {
        var body = new CilBody { InitLocals = false, MaxStack = 5 };

        var consoleType = module.CorLibTypes.GetTypeRef("System", "Console");
        var stringType = module.CorLibTypes.String.TypeDefOrRef;
        var convertType = module.CorLibTypes.GetTypeRef("System", "Convert");

        var writeLineString = new MemberRefUser(
            module,
            "WriteLine",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String),
            consoleType);

        var convertToString = new MemberRefUser(
            module,
            "ToString",
            MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.Object),
            convertType);

        var concat4 = new MemberRefUser(
            module,
            "Concat",
            MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String),
            stringType);

        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "[compat][speech] Event args in "));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, " -> "));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, convertToString));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, concat4));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, writeLineString));
        body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        body.OptimizeBranches();
        body.OptimizeMacros();
        return body;
    }

    private static string GetMethodLogLabel(MethodDef method)
    {
        var typeName = method.DeclaringType?.FullName ?? "<unknown-type>";
        var methodName = method.Name.String ?? "<unknown-method>";
        return $"{typeName}::{methodName} [0x{method.MDToken.ToInt32():X8}]";
    }
}
