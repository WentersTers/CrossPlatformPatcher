using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// IL patcher for OpenWakeWord integration into target assembly.
///
/// This patcher emits a concrete helper type into the target module and injects
/// calls at audio-related method entry sites:
/// 1) InitializeOpenWakeWord()
/// 2) if (!IsLocked()) OnAudioChunkAvailable(object audioArg)     [For OWW inference]
/// 3) EnqueueAudioForVosk(object audioArg)         [Always called, even during lock] [For Vosk STT during lock window]
/// </summary>
public static class OpenWakeWordCompatibilityPatcher
{
    private const string HelperTypeName = "CrossPlatformPatcherOWW";

    private sealed record HelperMembers(
        TypeDef HelperType,
        FieldDef InitializedField,
        FieldDef LockUntilTicksField,
        FieldDef LockDurationMsField,
        FieldDef FirstAudioLoggedField,
        MethodDef InitMethod,
        MethodDef IsLockedMethod,
        MethodDef ShouldTriggerWakeMethod,
        MethodDef OnAudioMethod,
        MethodDef DescribeAudioArgMethod,
        MethodDef LogMethod);

    public static int Patch(ModuleDefMD module, Action<string>? log = null)
    {
        var patched = 0;
        var helper = EnsureHelperMembers(module);

        var methods = module.GetTypes()
            .SelectMany(t => t.Methods)
            .Where(m => m.HasBody)
            .ToList();

        foreach (var method in methods)
        {
            if (method.DeclaringType == helper.HelperType)
                continue;

            if (!IsAudioRelatedMethod(method))
                continue;

            if (TryInjectAudioHook(method, helper))
            {
                patched++;
                var signature = string.Join(", ", method.Parameters
                    .Where(p => !p.IsHiddenThisParameter)
                    .Select(p => p.Type?.FullName ?? "<unknown>"));
                log?.Invoke($"[oww] Hooked audio method: {method.DeclaringType.FullName}::{method.Name}({signature}) [0x{method.MDToken.Raw:X8}]");
            }
        }

        log?.Invoke($"[oww] Audio event injection points: {patched}");
        return patched;
    }

    private static bool TryInjectAudioHook(MethodDef method, HelperMembers helper)
    {
        if (!method.HasBody || method.Body.Instructions.Count == 0)
            return false;

        var body = method.Body;
        var instrs = body.Instructions;

        for (int i = 0; i < instrs.Count; i++)
        {
            if (instrs[i].OpCode == OpCodes.Call && instrs[i].Operand is IMethod called &&
                called.DeclaringType?.Name == HelperTypeName &&
                called.Name == helper.OnAudioMethod.Name)
            {
                return false;
            }
        }

        body.SimplifyBranches();
        body.SimplifyMacros(method.Parameters);

        var first = instrs[0];
        var audioParam = PickAudioParameter(method);

        // Inject audio hook WITHOUT lock guard - let EnqueueAudio() decide what to do
        // (queue to Vosk if locked, queue to OWW if not)
        var injected = new List<Instruction>
        {
            Instruction.Create(OpCodes.Call, helper.InitMethod),
        };

        if (audioParam is null)
            injected.Add(Instruction.Create(OpCodes.Ldnull));
        else
            injected.Add(Instruction.Create(OpCodes.Ldarg, audioParam));

        injected.Add(Instruction.Create(OpCodes.Call, helper.OnAudioMethod));

        for (int i = injected.Count - 1; i >= 0; i--)
            instrs.Insert(0, injected[i]);

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

    private static Parameter? PickAudioParameter(MethodDef method)
    {
        var candidates = method.Parameters.Where(p => !p.IsHiddenThisParameter).ToList();
        if (candidates.Count == 0)
            return null;

        foreach (var p in candidates)
        {
            var typeName = p.Type?.FullName ?? string.Empty;
            if (typeName is "System.Byte[]" or "System.Single[]" or "System.Int16[]")
                return p;
        }

        foreach (var p in candidates)
        {
            var typeName = p.Type?.FullName ?? string.Empty;
            if (typeName.Contains("WaveInEventArgs", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("DataAvailableEventArgs", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("RecordingStoppedEventArgs", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Wave", StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }

        return candidates[0];
    }

    private static bool IsAudioRelatedMethod(MethodDef method)
    {
        if (!method.HasBody)
            return false;

        var methodName = method.Name.String ?? string.Empty;
        var nameLooksAudio = LooksLikeAudioCallbackName(methodName);
        var hasAudioTypedParam = false;
        var hasAudioEventArgsParam = false;
        var hasAudioCarrierParam = false;

        foreach (var instr in method.Body.Instructions)
        {
            if (instr.Operand is IMethod m)
            {
                var dt = m.DeclaringType?.FullName ?? string.Empty;
                if (IsAudioType(dt))
                    return true;
            }

            if (instr.Operand is IField f)
            {
                var dt = f.DeclaringType?.FullName ?? string.Empty;
                if (IsAudioType(dt))
                    return true;
            }
        }

        foreach (var param in method.Parameters)
        {
            if (param.IsHiddenThisParameter)
                continue;

            var fullName = param.Type?.FullName ?? string.Empty;
            if (fullName is "System.Byte[]" or "System.Single[]" or "System.Int16[]")
            {
                hasAudioTypedParam = true;
                continue;
            }

            if (fullName.Contains("WaveInEventArgs", StringComparison.OrdinalIgnoreCase) ||
                fullName.Contains("DataAvailableEventArgs", StringComparison.OrdinalIgnoreCase) ||
                fullName.Contains("RecordingStoppedEventArgs", StringComparison.OrdinalIgnoreCase) ||
                fullName.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
                fullName.Contains("Wave", StringComparison.OrdinalIgnoreCase))
            {
                hasAudioEventArgsParam = true;
            }

            if (!hasAudioCarrierParam && IsAudioCarrierType(param.Type))
                hasAudioCarrierParam = true;
        }

        if (nameLooksAudio && (hasAudioTypedParam || hasAudioEventArgsParam || hasAudioCarrierParam))
            return true;

        // Obfuscated builds often hide method names; allow signature-driven hooks.
        if (hasAudioCarrierParam)
            return true;

        return hasAudioTypedParam && method.Parameters.Count <= 4;
    }

    private static bool IsAudioCarrierType(TypeSig? typeSig)
    {
        if (typeSig == null)
            return false;

        var typeRef = typeSig.ToTypeDefOrRef();
        var typeDef = typeRef?.ResolveTypeDef();
        if (typeDef == null)
            return false;

        var hasBufferField = false;
        var hasSizeField = false;

        foreach (var field in typeDef.Fields)
        {
            var fieldType = field.FieldType?.FullName ?? string.Empty;
            if (fieldType is "System.Byte[]" or "System.Int16[]" or "System.Single[]")
                hasBufferField = true;

            if (fieldType is "System.Int32" or "System.UInt32")
                hasSizeField = true;
        }

        return hasBufferField && hasSizeField;
    }

    private static bool IsAudioType(string fullTypeName)
    {
        return fullTypeName.Contains("NAudio", StringComparison.OrdinalIgnoreCase) ||
               fullTypeName.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
               fullTypeName.Contains("Wave", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAudioCallbackName(string methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName))
            return false;

        if (methodName.StartsWith("get_", StringComparison.Ordinal) ||
            methodName.StartsWith("set_", StringComparison.Ordinal) ||
            methodName.StartsWith("add_", StringComparison.Ordinal) ||
            methodName.StartsWith("remove_", StringComparison.Ordinal))
        {
            return false;
        }

        return methodName.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
               methodName.Contains("wave", StringComparison.OrdinalIgnoreCase) ||
               methodName.Contains("buffer", StringComparison.OrdinalIgnoreCase) ||
               methodName.Contains("dataavailable", StringComparison.OrdinalIgnoreCase) ||
               methodName.Contains("record", StringComparison.OrdinalIgnoreCase) ||
               methodName.Contains("mic", StringComparison.OrdinalIgnoreCase);
    }

    private static HelperMembers EnsureHelperMembers(ModuleDefMD module)
    {
        var helperType = module.Find(HelperTypeName, isReflectionName: false) as TypeDef;
        if (helperType is null)
        {
            helperType = new TypeDefUser(
                string.Empty,
                HelperTypeName,
                module.CorLibTypes.Object.TypeDefOrRef)
            {
                Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
            };
            module.Types.Add(helperType);
        }

        var initializedField = helperType.Fields.FirstOrDefault(f => f.Name == "_initialized")
            ?? AddField(helperType, "_initialized", new FieldSig(module.CorLibTypes.Boolean));

        var lockUntilTicksField = helperType.Fields.FirstOrDefault(f => f.Name == "_lockUntilTicks")
            ?? AddField(helperType, "_lockUntilTicks", new FieldSig(module.CorLibTypes.Int64));

        var lockDurationMsField = helperType.Fields.FirstOrDefault(f => f.Name == "_lockDurationMs")
            ?? AddField(helperType, "_lockDurationMs", new FieldSig(module.CorLibTypes.Int32));

        var firstAudioLoggedField = helperType.Fields.FirstOrDefault(f => f.Name == "_firstAudioLogged")
            ?? AddField(helperType, "_firstAudioLogged", new FieldSig(module.CorLibTypes.Boolean));

        var logMethod = helperType.FindMethod("LogOWWEvent")
            ?? AddLogMethod(module, helperType);

        var initMethod = helperType.FindMethod("InitializeOpenWakeWord")
            ?? AddInitMethod(module, helperType, initializedField, lockDurationMsField, logMethod);

        var isLockedMethod = helperType.FindMethod("IsLocked")
            ?? AddIsLockedMethod(module, helperType, lockUntilTicksField);

        var shouldTriggerWakeMethod = helperType.FindMethod("ShouldTriggerWake")
            ?? AddShouldTriggerWakeMethod(module, helperType);

        var describeAudioArgMethod = helperType.FindMethod("DescribeAudioArg")
            ?? AddDescribeAudioArgMethod(module, helperType);

        var onAudioMethod = helperType.FindMethod("OnAudioChunkAvailable")
            ?? AddOnAudioMethod(module, helperType, initMethod, isLockedMethod, shouldTriggerWakeMethod, logMethod, describeAudioArgMethod, firstAudioLoggedField, lockUntilTicksField, lockDurationMsField);

        return new HelperMembers(
            helperType,
            initializedField,
            lockUntilTicksField,
            lockDurationMsField,
            firstAudioLoggedField,
            initMethod,
            isLockedMethod,
            shouldTriggerWakeMethod,
            onAudioMethod,
                describeAudioArgMethod,
            logMethod);
    }

    private static FieldDef AddField(TypeDef type, string name, FieldSig sig)
    {
        var field = new FieldDefUser(
            name,
            sig,
            FieldAttributes.Private | FieldAttributes.Static);
        type.Fields.Add(field);
        return field;
    }

    private static MethodDef AddLogMethod(ModuleDefMD module, TypeDef helperType)
    {
        var method = new MethodDefUser(
            "LogOWWEvent",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        var consoleType = module.CorLibTypes.GetTypeRef("System", "Console");
        var stringType = module.CorLibTypes.String.TypeDefOrRef;

        var concat = new MemberRefUser(
            module,
            "Concat",
            MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String),
            stringType);

        var writeLine = new MemberRefUser(
            module,
            "WriteLine",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String),
            consoleType);

        method.Body = new CilBody { InitLocals = false, MaxStack = 2 };
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "[oww] "));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, concat));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, writeLine));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        method.Body.OptimizeBranches();
        method.Body.OptimizeMacros();

        helperType.Methods.Add(method);
        return method;
    }

    private static MethodDef AddInitMethod(
        ModuleDefMD module,
        TypeDef helperType,
        FieldDef initializedField,
        FieldDef lockDurationMsField,
        IMethod logMethod)
    {
        var method = new MethodDefUser(
            "InitializeOpenWakeWord",
            MethodSig.CreateStatic(module.CorLibTypes.Void),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        var ret = Instruction.Create(OpCodes.Ret);
        method.Body = new CilBody { InitLocals = false, MaxStack = 2 };

        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, initializedField));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Brtrue_S, ret));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, initializedField));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 3000));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, lockDurationMsField));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "OpenWakeWord helper initialized"));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, logMethod));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Wake-word bridge active (audio callback -> wake lock gate)"));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, logMethod));
        method.Body.Instructions.Add(ret);

        method.Body.OptimizeBranches();
        method.Body.OptimizeMacros();

        helperType.Methods.Add(method);
        return method;
    }

    private static MethodDef AddIsLockedMethod(
        ModuleDefMD module,
        TypeDef helperType,
        FieldDef lockUntilTicksField)
    {
        var method = new MethodDefUser(
            "IsLocked",
            MethodSig.CreateStatic(module.CorLibTypes.Boolean),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        var dateTimeType = module.CorLibTypes.GetTypeRef("System", "DateTime");
        var getUtcNow = new MemberRefUser(
            module,
            "get_UtcNow",
            MethodSig.CreateStatic(new ValueTypeSig(dateTimeType)),
            dateTimeType);

        var getTicks = new MemberRefUser(
            module,
            "get_Ticks",
            MethodSig.CreateInstance(module.CorLibTypes.Int64),
            dateTimeType);

        var dateTimeLocal = new Local(new ValueTypeSig(dateTimeType));
        method.Body = new CilBody { InitLocals = true, MaxStack = 2 };
        method.Body.Variables.Add(dateTimeLocal);

        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, lockUntilTicksField));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, getUtcNow));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc, dateTimeLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloca_S, dateTimeLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, getTicks));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Cgt));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        method.Body.OptimizeBranches();
        method.Body.OptimizeMacros();

        helperType.Methods.Add(method);
        return method;
    }

    private static MethodDef AddOnAudioMethod(
        ModuleDefMD module,
        TypeDef helperType,
        IMethod initMethod,
        IMethod isLockedMethod,
        IMethod shouldTriggerWakeMethod,
        IMethod logMethod,
        IMethod describeAudioArgMethod,
        FieldDef firstAudioLoggedField,
        FieldDef lockUntilTicksField,
        FieldDef lockDurationMsField)
    {
        var method = new MethodDefUser(
            "OnAudioChunkAvailable",
            MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Object),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        var ret = Instruction.Create(OpCodes.Ret);

        var skipFirstAudioLog = Instruction.Create(OpCodes.Nop);
        var skipWakeLock = Instruction.Create(OpCodes.Nop);

        var dateTimeType = module.CorLibTypes.GetTypeRef("System", "DateTime");
        var getUtcNow = new MemberRefUser(
            module,
            "get_UtcNow",
            MethodSig.CreateStatic(new ValueTypeSig(dateTimeType)),
            dateTimeType);

        var getTicks = new MemberRefUser(
            module,
            "get_Ticks",
            MethodSig.CreateInstance(module.CorLibTypes.Int64),
            dateTimeType);

        var utcNowLocal = new Local(new ValueTypeSig(dateTimeType));

        // Get or create reference to external OpenWakeWordHelper class from PAIcom.OWW assembly
        // This is the runtime helper that receives audio and routes it to OWW or Vosk
        IMethod? enqueueAudioMethod = null;
        
        // Look for existing assembly reference to PAIcom.OWW
        var owwAssemblyRef = module.GetAssemblyRefs()
            .FirstOrDefault(a => a.Name == "PAIcom.OWW");
        
        // If no reference exists, create one
        // Dnlib will handle emitting the reference metadata when writing the module
        if (owwAssemblyRef == null)
        {
            owwAssemblyRef = new AssemblyRefUser("PAIcom.OWW", new Version(1, 0, 0, 0));
        }
        
        if (owwAssemblyRef != null)
        {
            // Create a TypeRef pointing to OpenWakeWordHelper in the external assembly
            var owwHelperTypeRef = new TypeRefUser(
                module,
                "CrossPlatformPatcher.Core",
                "OpenWakeWordHelper",
                owwAssemblyRef);
            
            // Create MemberRef to EnqueueAudio method in external type
            enqueueAudioMethod = new MemberRefUser(
                module,
                "EnqueueAudio",
                MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Object),
                owwHelperTypeRef);
        }

        method.Body = new CilBody { InitLocals = true, MaxStack = 4 };
        method.Body.Variables.Add(utcNowLocal);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, initMethod));
        // Removed: lock check that prevented audio processing during lock window
        // Now OnAudioChunkAvailable always runs, allowing Vosk to get audio during lock
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, firstAudioLoggedField));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Brtrue_S, skipFirstAudioLog));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, firstAudioLoggedField));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Audio callback path active (first chunk observed)"));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, logMethod));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, describeAudioArgMethod));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, logMethod));
        method.Body.Instructions.Add(skipFirstAudioLog);

        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, shouldTriggerWakeMethod));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Brfalse_S, skipWakeLock));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, getUtcNow));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc, utcNowLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloca_S, utcNowLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, getTicks));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, lockDurationMsField));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Conv_I8));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I8, 10000L));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Mul));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Add));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, lockUntilTicksField));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Wake-word gate triggered; lock armed"));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, logMethod));
        method.Body.Instructions.Add(skipWakeLock);
        
        // Call the real OpenWakeWordHelper.EnqueueAudio() unconditionally with the audio argument
        if (enqueueAudioMethod != null)
        {
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, enqueueAudioMethod));
        }
        
        method.Body.Instructions.Add(ret);

        method.Body.OptimizeBranches();
        method.Body.OptimizeMacros();

        helperType.Methods.Add(method);
        return method;
    }

    private static MethodDef AddShouldTriggerWakeMethod(ModuleDefMD module, TypeDef helperType)
    {
        var method = new MethodDefUser(
            "ShouldTriggerWake",
            MethodSig.CreateStatic(module.CorLibTypes.Boolean, module.CorLibTypes.Object),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        var typeRef = module.CorLibTypes.GetTypeRef("System", "Type");
        var arrayRef = module.CorLibTypes.GetTypeRef("System", "Array");
        var stringRef = module.CorLibTypes.String.TypeDefOrRef;

        var objGetType = new MemberRefUser(
            module,
            "GetType",
            MethodSig.CreateInstance(new ClassSig(typeRef)),
            module.CorLibTypes.Object.TypeDefOrRef);

        var typeGetFullName = new MemberRefUser(
            module,
            "get_FullName",
            MethodSig.CreateInstance(module.CorLibTypes.String),
            typeRef);

        var stringEqualsOp = new MemberRefUser(
            module,
            "op_Equality",
            MethodSig.CreateStatic(module.CorLibTypes.Boolean, module.CorLibTypes.String, module.CorLibTypes.String),
            stringRef);

        var arrayGetLength = new MemberRefUser(
            module,
            "get_Length",
            MethodSig.CreateInstance(module.CorLibTypes.Int32),
            arrayRef);

        var typeLocal = new Local(new ClassSig(typeRef));
        var fullNameLocal = new Local(module.CorLibTypes.String);
        var arrayLocal = new Local(new ClassSig(arrayRef));
        var lengthLocal = new Local(module.CorLibTypes.Int32);

        var nonNull = Instruction.Create(OpCodes.Nop);
        var checkArray = Instruction.Create(OpCodes.Nop);
        var haveArray = Instruction.Create(OpCodes.Nop);
        var checkByteArray = Instruction.Create(OpCodes.Nop);
        var checkInt16Array = Instruction.Create(OpCodes.Nop);
        var checkSingleArray = Instruction.Create(OpCodes.Nop);
        var falseRet = Instruction.Create(OpCodes.Ldc_I4_0);
        var trueRet = Instruction.Create(OpCodes.Ldc_I4_1);
        var done = Instruction.Create(OpCodes.Ret);

        method.Body = new CilBody { InitLocals = true, MaxStack = 3 };
        method.Body.Variables.Add(typeLocal);
        method.Body.Variables.Add(fullNameLocal);
        method.Body.Variables.Add(arrayLocal);
        method.Body.Variables.Add(lengthLocal);

        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Brtrue, nonNull));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Br, falseRet));

        method.Body.Instructions.Add(nonNull);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, objGetType));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc, typeLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, typeLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, typeGetFullName));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc, fullNameLocal));

        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, fullNameLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "System.Speech.Recognition.SpeechRecognizedEventArgs"));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, stringEqualsOp));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Brtrue, trueRet));

        method.Body.Instructions.Add(checkArray);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Isinst, arrayRef));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc, arrayLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, arrayLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Brtrue, haveArray));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Br, falseRet));

        method.Body.Instructions.Add(haveArray);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, arrayLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, arrayGetLength));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc, lengthLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lengthLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Bgt, checkByteArray));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Br, falseRet));

        method.Body.Instructions.Add(checkByteArray);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, fullNameLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "System.Byte[]"));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, stringEqualsOp));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, checkInt16Array));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lengthLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 3200));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Blt, falseRet));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lengthLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 65536));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ble, trueRet));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Br, falseRet));

        method.Body.Instructions.Add(checkInt16Array);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, fullNameLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "System.Int16[]"));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, stringEqualsOp));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, checkSingleArray));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lengthLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 512));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Blt, falseRet));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lengthLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 32768));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ble, trueRet));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Br, falseRet));

        method.Body.Instructions.Add(checkSingleArray);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, fullNameLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "System.Single[]"));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, stringEqualsOp));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, falseRet));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lengthLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 512));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Blt, falseRet));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lengthLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 32768));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ble, trueRet));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Br, falseRet));

        method.Body.Instructions.Add(falseRet);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Br, done));
        method.Body.Instructions.Add(trueRet);
        method.Body.Instructions.Add(done);

        method.Body.OptimizeBranches();
        method.Body.OptimizeMacros();

        helperType.Methods.Add(method);
        return method;
    }

    private static MethodDef AddDescribeAudioArgMethod(ModuleDefMD module, TypeDef helperType)
    {
        var method = new MethodDefUser(
            "DescribeAudioArg",
            MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.Object),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

        var typeRef = module.CorLibTypes.GetTypeRef("System", "Type");
        var arrayRef = module.CorLibTypes.GetTypeRef("System", "Array");
        var stringRef = module.CorLibTypes.String.TypeDefOrRef;

        var objGetType = new MemberRefUser(
            module,
            "GetType",
            MethodSig.CreateInstance(new ClassSig(typeRef)),
            module.CorLibTypes.Object.TypeDefOrRef);

        var typeGetFullName = new MemberRefUser(
            module,
            "get_FullName",
            MethodSig.CreateInstance(module.CorLibTypes.String),
            typeRef);

        var arrayGetLength = new MemberRefUser(
            module,
            "get_Length",
            MethodSig.CreateInstance(module.CorLibTypes.Int32),
            arrayRef);

        var intToString = new MemberRefUser(
            module,
            "ToString",
            MethodSig.CreateInstance(module.CorLibTypes.String),
            module.CorLibTypes.Int32.TypeDefOrRef);

        var concat2 = new MemberRefUser(
            module,
            "Concat",
            MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String),
            stringRef);

        var concat4 = new MemberRefUser(
            module,
            "Concat",
            MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String, module.CorLibTypes.String),
            stringRef);

        var typeLocal = new Local(new ClassSig(typeRef));
        var fullNameLocal = new Local(module.CorLibTypes.String);
        var arrayLocal = new Local(new ClassSig(arrayRef));

        var nonNull = Instruction.Create(OpCodes.Nop);
        var hasArray = Instruction.Create(OpCodes.Nop);

        method.Body = new CilBody { InitLocals = true, MaxStack = 5 };
        method.Body.Variables.Add(typeLocal);
        method.Body.Variables.Add(fullNameLocal);
        method.Body.Variables.Add(arrayLocal);

        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Brtrue_S, nonNull));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Audio callback arg: <null>"));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        method.Body.Instructions.Add(nonNull);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, objGetType));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc, typeLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, typeLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, typeGetFullName));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc, fullNameLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Isinst, arrayRef));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc, arrayLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, arrayLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Brtrue_S, hasArray));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Audio callback arg type="));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, fullNameLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, concat2));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        method.Body.Instructions.Add(hasArray);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Audio callback arg type="));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, fullNameLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, " length="));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, arrayLocal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, arrayGetLength));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, intToString));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, concat4));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        method.Body.OptimizeBranches();
        method.Body.OptimizeMacros();

        helperType.Methods.Add(method);
        return method;
    }
}
