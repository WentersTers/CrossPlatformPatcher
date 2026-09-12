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
            if (IsCompatHelper(method))
                continue;

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

        // Second pass: harden EmulateRecognize call sites so text dispatch
        // survives a missing/dead recognizer. The whole-method catch above
        // stays as the backstop; this runs after wrapping on purpose so the
        // backstop is in place before speech calls are rewritten.
        var hardened = 0;
        foreach (var method in methods)
        {
            if (IsCompatHelper(method))
                continue;

            hardened += TryHardenEmulateCalls(module, method, ref logMethod);
        }

        log?.Invoke($"System.Speech safety wrappers applied: {patched}");
        log?.Invoke($"System.Speech event probes applied: {eventLogs}");
        log?.Invoke($"System.Speech emulate hardening applied: {hardened}");
        return patched;
    }

    private static bool IsCompatHelper(MethodDef method)
    {
        return string.Equals(method.DeclaringType?.Name, "CrossPlatformPatcherCompat", StringComparison.Ordinal);
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

    /// <summary>
    /// Rewrites direct <c>EmulateRecognize(string)</c> calls to a resilient
    /// helper that tolerates a missing or idle recognizer (null engine,
    /// no installed SAPI voices, recognition not started). The helper mirrors
    /// the proven voice-dispatch pattern: stop, emulate, restart, each step
    /// guarded, null engine short-circuits to a null result.
    /// The whole-method catch installed by <see cref="TryWrapMethodWithCatch"/>
    /// stays in place as the backstop, so a later null dereference degrades
    /// to today's abort instead of a crash. Methods living on the injected
    /// helper type are skipped (the helper itself calls EmulateRecognize).
    /// Returns the number of call sites rewritten.
    /// </summary>
    private static int TryHardenEmulateCalls(ModuleDefMD module, MethodDef method, ref MethodDef? logMethod)
    {
        if (!method.HasBody)
            return 0;

        var body = method.Body;
        var rewritten = 0;

        for (int i = 0; i < body.Instructions.Count; i++)
        {
            var instr = body.Instructions[i];
            if (instr.OpCode != OpCodes.Call && instr.OpCode != OpCodes.Callvirt)
                continue;

            if (instr.Operand is not IMethod called)
                continue;

            if (!IsEmulateRecognizeCall(called))
                continue;

            // Never rewrite tail calls: the callee shape changes.
            if (i > 0 && body.Instructions[i - 1].OpCode == OpCodes.Tailcall)
                continue;

            var helper = EnsureEmulateResilientHelper(module, called, ref logMethod);
            if (helper is null)
                continue;

            instr.OpCode = OpCodes.Call;
            instr.Operand = helper;
            rewritten++;
        }

        if (rewritten > 0)
        {
            body.OptimizeBranches();
            body.OptimizeMacros();
        }

        return rewritten;
    }

    private static bool IsEmulateRecognizeCall(IMethod called)
    {
        if (!string.Equals(called.Name, "EmulateRecognize", StringComparison.Ordinal))
            return false;

        var sig = called.MethodSig;
        if (sig is null || !sig.HasThis || sig.Params.Count != 1)
            return false;

        if (sig.Params[0].GetElementType() != ElementType.String)
            return false;

        // RecognitionResult-style reference return; void and value returns
        // cannot carry a recognition outcome through the helper.
        var ret = sig.RetType.GetElementType();
        if (ret != ElementType.Class && ret != ElementType.Object &&
            ret != ElementType.String && ret != ElementType.SZArray)
            return false;

        var declaring = called.DeclaringType?.FullName;
        if (string.IsNullOrEmpty(declaring) ||
            !declaring.StartsWith("System.Speech.", StringComparison.Ordinal))
            return false;

        return true;
    }

    private static MethodDef? EnsureEmulateResilientHelper(ModuleDefMD module, IMethod emulateCall, ref MethodDef? logMethod)
    {
        var sig = emulateCall.MethodSig;
        var engineRef = emulateCall.DeclaringType;
        if (sig is null || engineRef is null)
            return null;

        var engineSig = engineRef.ToTypeSig();
        var retSig = sig.RetType;
        var stringSig = module.CorLibTypes.String;

        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat")
            ?? CreateHelperType(module, "CrossPlatformPatcherCompat");

        var helperName = "EmulateRecognizeResilient";
        var existing = helperType.Methods.FirstOrDefault(m => m.Name == helperName
            && m.MethodSig is not null
            && m.MethodSig.Params.Count == 2
            && m.MethodSig.Params[0].FullName == engineSig.FullName
            && m.MethodSig.RetType.FullName == retSig.FullName);
        if (existing is not null)
            return existing;

        var methodSig = MethodSig.CreateStatic(retSig, engineSig, stringSig);
        var helper = new MethodDefUser(
            helperName,
            methodSig,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        logMethod ??= EnsureSpeechLogMethod(module);
        helper.Body = BuildEmulateResilientBody(module, engineRef, retSig, logMethod);
        helperType.Methods.Add(helper);
        return helper;
    }

    private static CilBody BuildEmulateResilientBody(
        ModuleDefMD module,
        ITypeDefOrRef engineRef,
        TypeSig retSig,
        MethodDef logMethod)
    {
        var body = new CilBody { InitLocals = true, MaxStack = 3 };

        var result = new Local(retSig);
        body.Variables.Add(result);
        var exLocal = new Local(module.CorLibTypes.GetTypeRef("System", "Exception").ToTypeSig());
        body.Variables.Add(exLocal);

        var ins = body.Instructions;
        var exceptionType = module.CorLibTypes.GetTypeRef("System", "Exception");

        // Emits try { emitBody(); Leave join; } catch { Pop; Leave join; } join:.
        // Every edge is an explicit branch so the stack calculator never sees
        // fallthrough into a handler with the wrong depth.
        void EmitGuarded(Action emitBody)
        {
            var tryStart = Instruction.Create(OpCodes.Nop);
            var join = Instruction.Create(OpCodes.Nop);
            var handlerStart = Instruction.Create(OpCodes.Pop);
            ins.Add(tryStart);
            emitBody();
            ins.Add(Instruction.Create(OpCodes.Leave_S, join));
            ins.Add(handlerStart);
            ins.Add(Instruction.Create(OpCodes.Leave_S, join));
            ins.Add(join);
            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart,
                TryEnd = handlerStart,
                HandlerStart = handlerStart,
                HandlerEnd = join,
                CatchType = exceptionType,
            });
        }

        // Null engine: the text path has no recognizer to emulate through.
        // Bridge the phrase into the patcher's own phrase dispatcher as a
        // side effect (same treatment voice gets), then return null to honor
        // the caller contract. The outcome is logged by the bridge itself.
        var hasEngine = Instruction.Create(OpCodes.Nop);
        var postNullReturn = Instruction.Create(OpCodes.Nop);
        var bridgeMethod = EnsureBridgeTextDispatchHelper(module, ref logMethod);
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Brtrue_S, hasEngine));
        ins.Add(Instruction.Create(OpCodes.Ldarg_1));
        ins.Add(Instruction.Create(OpCodes.Call, bridgeMethod));
        ins.Add(Instruction.Create(OpCodes.Pop));
        ins.Add(Instruction.Create(OpCodes.Ldnull));
        ins.Add(Instruction.Create(OpCodes.Stloc, result));
        ins.Add(Instruction.Create(OpCodes.Br_S, postNullReturn));
        ins.Add(hasEngine);

        // Guarded pre-steps: RecognizeAsyncCancel(); RecognizeAsyncStop();
        // Missing members resolve to tolerance (each in its own handler).
        foreach (var name in new[] { "RecognizeAsyncCancel", "RecognizeAsyncStop" })
        {
            var target = FindEngineMethod(module, engineRef, name, 0) ??
                new MemberRefUser(module, name, MethodSig.CreateInstance(module.CorLibTypes.Void), engineRef);
            EmitGuarded(() =>
            {
                ins.Add(Instruction.Create(OpCodes.Ldarg_0));
                ins.Add(Instruction.Create(OpCodes.Callvirt, target));
            });
        }

        // try { result = engine.EmulateRecognize(text); restart; }
        // catch { log; result = null; }
        var emulateTarget = FindEngineMethod(module, engineRef, "EmulateRecognize", 1) ??
            new MemberRefUser(
                module,
                "EmulateRecognize",
                MethodSig.CreateInstance(retSig, module.CorLibTypes.String),
                engineRef);
        var startTarget = FindEngineMethod(module, engineRef, "RecognizeAsync", 0) ??
            new MemberRefUser(module, "RecognizeAsync", MethodSig.CreateInstance(module.CorLibTypes.Void), engineRef);

        var mainTryStart = Instruction.Create(OpCodes.Nop);
        var mainJoin = Instruction.Create(OpCodes.Nop);
        var mainHandlerStart = Instruction.Create(OpCodes.Stloc, exLocal);
        ins.Add(mainTryStart);
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Ldarg_1));
        ins.Add(Instruction.Create(OpCodes.Callvirt, emulateTarget));
        ins.Add(Instruction.Create(OpCodes.Stloc, result));

        // Best-effort restart inside the protected region.
        var restartTryStart = Instruction.Create(OpCodes.Nop);
        var restartJoin = Instruction.Create(OpCodes.Nop);
        var restartHandlerStart = Instruction.Create(OpCodes.Pop);
        ins.Add(restartTryStart);
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Callvirt, startTarget));
        ins.Add(Instruction.Create(OpCodes.Leave_S, restartJoin));
        ins.Add(restartHandlerStart);
        ins.Add(Instruction.Create(OpCodes.Leave_S, restartJoin));
        ins.Add(restartJoin);
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = restartTryStart,
            TryEnd = restartHandlerStart,
            HandlerStart = restartHandlerStart,
            HandlerEnd = restartJoin,
            CatchType = exceptionType,
        });

        ins.Add(Instruction.Create(OpCodes.Leave_S, mainJoin));
        ins.Add(mainHandlerStart);
        ins.Add(Instruction.Create(OpCodes.Ldstr, "CrossPlatformPatcherCompat.EmulateRecognizeResilient"));
        ins.Add(Instruction.Create(OpCodes.Ldloc, exLocal));
        ins.Add(Instruction.Create(OpCodes.Call, logMethod));
        ins.Add(Instruction.Create(OpCodes.Ldnull));
        ins.Add(Instruction.Create(OpCodes.Stloc, result));
        ins.Add(Instruction.Create(OpCodes.Leave_S, mainJoin));
        ins.Add(mainJoin);
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = mainTryStart,
            TryEnd = mainHandlerStart,
            HandlerStart = mainHandlerStart,
            HandlerEnd = mainJoin,
            CatchType = exceptionType,
        });

        ins.Add(postNullReturn);
        ins.Add(Instruction.Create(OpCodes.Ldloc, result));
        ins.Add(Instruction.Create(OpCodes.Ret));

        body.OptimizeBranches();
        body.OptimizeMacros();
        return body;
    }

    private static IMethod? FindEngineMethod(ModuleDefMD module, ITypeDefOrRef engineRef, string name, int paramCount)
    {
        var engineName = engineRef.FullName;
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.Operand is IMethod called &&
                        string.Equals(called.Name, name, StringComparison.Ordinal) &&
                        called.MethodSig is not null &&
                        called.MethodSig.Params.Count == paramCount &&
                        string.Equals(called.DeclaringType?.FullName, engineName, StringComparison.Ordinal))
                    {
                        return called;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Injects (once) a helper that routes a raw command phrase through the
    /// patcher's own phrase dispatcher (<c>HandleRecognizedSpeech</c> in the
    /// PAIcom.OWW assembly, resolved at runtime so the product module needs
    /// no new static references). Returns the assistant line, or null when
    /// the bridge is unavailable; every failure is logged through the shared
    /// compat log method.
    /// </summary>
    private static MethodDef EnsureBridgeTextDispatchHelper(ModuleDefMD module, ref MethodDef? logMethod)
    {
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat")
            ?? CreateHelperType(module, "CrossPlatformPatcherCompat");

        const string helperName = "BridgeTextDispatch";
        var existing = helperType.Methods.FirstOrDefault(m => m.Name == helperName);
        if (existing is not null)
            return existing;

        var stringSig = module.CorLibTypes.String;
        var helper = new MethodDefUser(
            helperName,
            MethodSig.CreateStatic(stringSig, stringSig),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        logMethod ??= EnsureSpeechLogMethod(module);
        helper.Body = BuildBridgeTextDispatchBody(module, logMethod);
        helperType.Methods.Add(helper);
        return helper;
    }

    private static CilBody BuildBridgeTextDispatchBody(ModuleDefMD module, MethodDef logMethod)
    {
        var body = new CilBody { InitLocals = true, MaxStack = 5 };

        var stringSig = module.CorLibTypes.String;
        var objectSig = module.CorLibTypes.Object;
        var voidSig = module.CorLibTypes.Void;
        var exSig = module.CorLibTypes.GetTypeRef("System", "Exception").ToTypeSig();

        var result = new Local(stringSig);
        body.Variables.Add(result);
        var exLocal = new Local(exSig);
        body.Variables.Add(exLocal);

        var runtimeAssemblies = new[] { "System.Runtime", "mscorlib", "System.Private.CoreLib", "netstandard" };
        var assemblyRef = ResolveFrameworkType(module, "System.Reflection", "Assembly", runtimeAssemblies);
        var typeRef = ResolveFrameworkType(module, "System", "Type", runtimeAssemblies);
        var methodBaseRef = ResolveFrameworkType(module, "System.Reflection", "MethodBase", runtimeAssemblies);
        var methodInfoRef = ResolveFrameworkType(module, "System.Reflection", "MethodInfo", runtimeAssemblies);
        var runtimeTypeHandleRef = ResolveFrameworkType(module, "System", "RuntimeTypeHandle", runtimeAssemblies);

        var loadMethod = new MemberRefUser(module, "Load",
            MethodSig.CreateStatic(assemblyRef.ToTypeSig(), stringSig),
            assemblyRef);
        var getTypeMethod = new MemberRefUser(module, "GetType",
            MethodSig.CreateStatic(typeRef.ToTypeSig(), stringSig), typeRef);
        var getMethodMethod = new MemberRefUser(module, "GetMethod",
            MethodSig.CreateInstance(methodInfoRef.ToTypeSig(), stringSig),
            typeRef);
        var invokeMethod = new MemberRefUser(module, "Invoke",
            MethodSig.CreateInstance(objectSig, objectSig,
                new SZArraySig(objectSig)),
            methodBaseRef);
        var getTypeFromHandle = new MemberRefUser(module, "GetTypeFromHandle",
            MethodSig.CreateStatic(typeRef.ToTypeSig(),
                runtimeTypeHandleRef.ToTypeSig()),
            typeRef);

        var ins = body.Instructions;
        var exceptionType = module.CorLibTypes.GetTypeRef("System", "Exception");

        var tryStart = Instruction.Create(OpCodes.Nop);
        var join = Instruction.Create(OpCodes.Nop);
        var handlerStart = Instruction.Create(OpCodes.Stloc, exLocal);
        ins.Add(tryStart);
        ins.Add(Instruction.Create(OpCodes.Ldstr, "PAIcom.OWW"));
        ins.Add(Instruction.Create(OpCodes.Call, loadMethod));
        ins.Add(Instruction.Create(OpCodes.Ldstr, "CrossPlatformPatcher.Core.OpenWakeWordHelper"));
        ins.Add(Instruction.Create(OpCodes.Call, getTypeMethod));
        ins.Add(Instruction.Create(OpCodes.Dup));
        ins.Add(Instruction.Create(OpCodes.Ldstr, "HandleRecognizedSpeech"));
        ins.Add(Instruction.Create(OpCodes.Callvirt, getMethodMethod));
        ins.Add(Instruction.Create(OpCodes.Ldnull));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        ins.Add(Instruction.Create(OpCodes.Newarr, module.CorLibTypes.Object));
        ins.Add(Instruction.Create(OpCodes.Dup));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Stelem_Ref));
        ins.Add(Instruction.Create(OpCodes.Callvirt, invokeMethod));
        ins.Add(Instruction.Create(OpCodes.Isinst, module.CorLibTypes.String.TypeDefOrRef));
        ins.Add(Instruction.Create(OpCodes.Stloc, result));
        ins.Add(Instruction.Create(OpCodes.Leave_S, join));
        ins.Add(handlerStart);
        ins.Add(Instruction.Create(OpCodes.Ldstr, "CrossPlatformPatcherCompat.BridgeTextDispatch"));
        ins.Add(Instruction.Create(OpCodes.Ldloc, exLocal));
        ins.Add(Instruction.Create(OpCodes.Call, logMethod));
        ins.Add(Instruction.Create(OpCodes.Leave_S, join));
        ins.Add(join);
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = join,
            CatchType = exceptionType,
        });

        // Log the outcome (assistant line or empty) for validation readability.
        var consoleRef = ResolveFrameworkType(module, "System", "Console",
            "System.Console", "System.Runtime", "mscorlib", "System.Private.CoreLib", "netstandard");
        var writeLine = new MemberRefUser(module, "WriteLine",
            MethodSig.CreateStatic(voidSig, stringSig),
            consoleRef);
        var concat = new MemberRefUser(module, "Concat",
            MethodSig.CreateStatic(stringSig, stringSig, stringSig),
            stringSig.TypeDefOrRef);
        ins.Add(Instruction.Create(OpCodes.Ldstr, "[compat][speech] Bridged text dispatch done: "));
        ins.Add(Instruction.Create(OpCodes.Ldloc, result));
        ins.Add(Instruction.Create(OpCodes.Call, concat));
        ins.Add(Instruction.Create(OpCodes.Call, writeLine));
        ins.Add(Instruction.Create(OpCodes.Ldloc, result));
        ins.Add(Instruction.Create(OpCodes.Ret));

        body.OptimizeBranches();
        body.OptimizeMacros();
        return body;
    }

    /// <summary>
    /// Resolves a framework type by searching the module's referenced
    /// assemblies. Framework/Mono modules (mscorlib present) resolve from
    /// mscorlib, where these types canonically live; modern modules resolve
    /// from their specific split assemblies. Getting the scope wrong produces
    /// a runtime TypeLoad that can even break console output itself, so the
    /// mscorlib check comes first whenever that reference exists.
    /// </summary>
    private static ITypeDefOrRef ResolveFrameworkType(
        ModuleDefMD module, string @namespace, string name, params string[] assemblyNames)
    {
        var refs = module.GetAssemblyRefs().ToList();
        bool isFramework = refs.Any(a =>
            string.Equals(a.Name, "mscorlib", StringComparison.OrdinalIgnoreCase));

        var ordered = isFramework
            ? new[] { "mscorlib" }.Concat(assemblyNames).Distinct(StringComparer.OrdinalIgnoreCase)
            : assemblyNames.Concat(new[] { "mscorlib" }).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var assemblyName in ordered)
        {
            var assemblyRef = refs.FirstOrDefault(a =>
                string.Equals(a.Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            if (assemblyRef is null)
                continue;

            return new TypeRefUser(module, @namespace, name, assemblyRef);
        }

        return module.CorLibTypes.GetTypeRef(@namespace, name);
    }

    private static MethodDef EnsureSpeechLogMethod(ModuleDefMD module)    {
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
        // Prints: <prefix><label> :: <ExceptionType>: <message>
        // The exception detail is load-bearing: several compat failures were
        // diagnosed only after this line started naming the inner error.
        var body = new CilBody { InitLocals = false, MaxStack = 4 };

        var consoleType = ResolveFrameworkType(module, "System", "Console", "System.Console", "System.Runtime", "mscorlib", "System.Private.CoreLib", "netstandard");
        var stringType = module.CorLibTypes.String.TypeDefOrRef;
        var objectType = module.CorLibTypes.Object.TypeDefOrRef;
        var exceptionType = module.CorLibTypes.GetTypeRef("System", "Exception");
        var typeType = module.CorLibTypes.GetTypeRef("System", "Type");

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

        var getType = new MemberRefUser(
            module,
            "GetType",
            MethodSig.CreateInstance(typeType.ToTypeSig()),
            objectType);

        var getName = new MemberRefUser(
            module,
            "get_Name",
            MethodSig.CreateInstance(module.CorLibTypes.String),
            typeType);

        var getMessage = new MemberRefUser(
            module,
            "get_Message",
            MethodSig.CreateInstance(module.CorLibTypes.String),
            exceptionType);

        // t = prefix + label
        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "[compat][speech][fallback] System.Speech unavailable in: "));
        body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, concat2));
        // t += " :: "
        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, " :: "));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, concat2));
        // t += ex.GetType().Name
        body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getType));
        body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getName));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, concat2));
        // t += ": "
        body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, ": "));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, concat2));
        // t += ex.Message; WriteLine(t)
        body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getMessage));
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

        var consoleType = ResolveFrameworkType(module, "System", "Console", "System.Console", "System.Runtime", "mscorlib", "System.Private.CoreLib", "netstandard");
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

        var consoleType = ResolveFrameworkType(module, "System", "Console", "System.Console", "System.Runtime", "mscorlib", "System.Private.CoreLib", "netstandard");
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
