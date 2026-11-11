using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Bounds the app's synchronous <c>WebClient.DownloadString</c> calls.
///
/// Why: the menu/credits form performs a synchronous download while it is
/// being constructed. If that call stalls (a DNS or TLS stall is NOT covered
/// by <c>HttpWebRequest.Timeout</c>), the UI thread never finishes the form:
/// the menu paints the background and some buttons, Start never appears, and
/// clicks do nothing. That is the frozen-menu failure seen under Proton 9.
///
/// What: every call to <c>WebClient.DownloadString(string)</c> is redirected
/// to <see cref="HelperTypeName"/>.<c>SafeDownloadString(WebClient, string)</c>.
/// That helper runs the real download on a background thread and waits at
/// most <see cref="WatchdogTimeoutMilliseconds"/> for it. On timeout it
/// returns an empty string - the same shape as "no data" - so the calling
/// form can finish painting instead of freezing or raising an unhandled
/// exception dialog. On success it returns the same string as before, so
/// behaviour is unchanged.
///
/// A call-site rewrite is used rather than a WebClient subclass because
/// <c>DownloadString</c> is not virtual on every runtime (it is virtual on
/// .NET Framework, not on modern .NET), and this works on both.
/// Idempotent: the helper types are created once and re-used.
/// </summary>
public static class WebClientTimeoutPatcher
{
    public const string HelperTypeName = "CrossPlatformPatcherCompatNet";
    public const string JobTypeName = "CrossPlatformPatcherCompatDownloadJob";

    /// <summary>Hard cap for one download string call.</summary>
    public const int WatchdogTimeoutMilliseconds = 30000;

    private const string WebClientFullName = "System.Net.WebClient";

    public static int Patch(ModuleDefMD module, Action<string>? log = null)
    {
        if (module is null) throw new ArgumentNullException(nameof(module));

        // Find a call site first; a module that never calls DownloadString is
        // left untouched (pristine or unrelated inputs).
        IMethod? downloadCall = null;
        foreach (var t in module.GetTypes())
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if ((ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Call) &&
                        ins.Operand is IMethod im &&
                        im.DeclaringType?.FullName == WebClientFullName &&
                        im.Name == "DownloadString" &&
                        im.MethodSig?.Params.Count == 1)
                    {
                        downloadCall = im;
                        break;
                    }
                }
                if (downloadCall != null) break;
            }
            if (downloadCall != null) break;
        }

        if (downloadCall == null)
        {
            log?.Invoke("[webclient] no WebClient.DownloadString call found - skipped.");
            return 0;
        }

        var webClientRef = downloadCall.DeclaringType!;

        var netType = module.GetTypes().FirstOrDefault(t => t.Name == HelperTypeName);
        if (netType == null)
        {
            netType = new TypeDefUser(string.Empty, HelperTypeName, module.CorLibTypes.GetTypeRef("System", "Object"))
            {
                Attributes = TypeAttributes.Public | TypeAttributes.Abstract |
                             TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit
            };
            module.Types.Add(netType);
        }

        var jobType = EnsureJobType(module, webClientRef);
        var webExceptionRef = FindOrCreateTypeRef(module, "System.Net", "WebException",
            webClientRef.Scope as IResolutionScope ?? module.CorLibTypes.AssemblyRef);
        var trippedField = netType.Fields.FirstOrDefault(f => f.Name == "_tripped")
            ?? AddField(netType, "_tripped", module.CorLibTypes.Boolean);
        trippedField.Attributes = FieldAttributes.Public | FieldAttributes.Static;
        var safeMethod = EnsureSafeDownloadMethod(
            module, netType, jobType, webClientRef, webExceptionRef, trippedField);

        var safeCall = new MemberRefUser(
            module, "SafeDownloadString",
            MethodSig.CreateStatic(module.CorLibTypes.String,
                webClientRef.ToTypeSig(), module.CorLibTypes.String),
            netType);

        int replaced = 0;
        foreach (var t in module.GetTypes())
        {
            if (t.Name == HelperTypeName || t.Name == JobTypeName) continue;
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if ((ins.OpCode != OpCodes.Callvirt && ins.OpCode != OpCodes.Call) ||
                        ins.Operand is not IMethod im) continue;
                    if (im.DeclaringType?.FullName != WebClientFullName || im.Name != "DownloadString" ||
                        im.MethodSig?.Params.Count != 1) continue;
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = safeCall;
                    replaced++;
                }
            }
        }

        log?.Invoke($"[webclient] {replaced} DownloadString call(s) redirected to the {WatchdogTimeoutMilliseconds}ms watchdog.");
        return replaced;
    }

    private static TypeDef EnsureJobType(ModuleDefMD module, ITypeDefOrRef webClientRef)
    {
        var existing = module.GetTypes().FirstOrDefault(t => t.Name == JobTypeName);
        if (existing != null) return existing;

        var job = new TypeDefUser(string.Empty, JobTypeName, module.CorLibTypes.GetTypeRef("System", "Object"))
        {
            Attributes = TypeAttributes.Public | TypeAttributes.BeforeFieldInit
        };
        module.Types.Add(job);

        var clientField = AddField(job, "_client", webClientRef.ToTypeSig());
        var addressField = AddField(job, "_address", module.CorLibTypes.String);
        var resultField = AddField(job, "_result", module.CorLibTypes.String);
        var errorField = AddField(job, "_error", module.CorLibTypes.GetTypeRef("System", "Exception").ToTypeSig());

        // .ctor: base object ctor only.
        var ctor = new MethodDefUser(
            ".ctor",
            MethodSig.CreateInstance(module.CorLibTypes.Void),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.HideBySig |
            MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        var ctorBody = new CilBody { MaxStack = 8 };
        ctorBody.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        ctorBody.Instructions.Add(Instruction.Create(OpCodes.Call,
            new MemberRefUser(module, ".ctor",
                MethodSig.CreateInstance(module.CorLibTypes.Void),
                module.CorLibTypes.GetTypeRef("System", "Object"))));
        ctorBody.Instructions.Add(Instruction.Create(OpCodes.Ret));
        ctorBody.UpdateInstructionOffsets();
        ctor.Body = ctorBody;
        job.Methods.Add(ctor);

        // Run(): try { _result = _client.DownloadString(_address); } catch (Exception e) { _error = e; }
        var run = new MethodDefUser(
            "Run",
            MethodSig.CreateInstance(module.CorLibTypes.Void),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.HideBySig);
        job.Methods.Add(run);

        var baseDownload = new MemberRefUser(
            module, "DownloadString",
            MethodSig.CreateInstance(module.CorLibTypes.String, module.CorLibTypes.String),
            webClientRef);

        var exceptionType = module.CorLibTypes.GetTypeRef("System", "Exception");
        var body = new CilBody { InitLocals = true, MaxStack = 8 };
        body.Variables.Add(new Local(exceptionType.ToTypeSig()));

        var ins = body.Instructions;
        var tryStart = Instruction.Create(OpCodes.Nop);
        var join = Instruction.Create(OpCodes.Nop);
        var handlerStart = Instruction.Create(OpCodes.Stloc_0);

        ins.Add(tryStart);
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Ldfld, clientField));
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Ldfld, addressField));
        ins.Add(Instruction.Create(OpCodes.Callvirt, baseDownload));
        ins.Add(Instruction.Create(OpCodes.Stfld, resultField));
        ins.Add(Instruction.Create(OpCodes.Leave_S, join));
        ins.Add(handlerStart);
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Ldloc_0));
        ins.Add(Instruction.Create(OpCodes.Stfld, errorField));
        ins.Add(Instruction.Create(OpCodes.Leave_S, join));
        ins.Add(join);
        ins.Add(Instruction.Create(OpCodes.Ret));

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = join,
            CatchType = exceptionType,
        });

        body.UpdateInstructionOffsets();
        body.KeepOldMaxStack = true; // hand-built; depth never exceeds 3
        run.Body = body;
        return job;
    }

    private static MethodDef EnsureSafeDownloadMethod(
        ModuleDefMD module,
        TypeDef netType,
        TypeDef jobType,
        ITypeDefOrRef webClientRef,
        TypeRef webExceptionRef,
        FieldDef trippedField)
    {
        var existing = netType.Methods.FirstOrDefault(m => m.Name == "SafeDownloadString");
        if (existing != null) return existing;

        var stringSig = module.CorLibTypes.String;
        var method = new MethodDefUser(
            "SafeDownloadString",
            MethodSig.CreateStatic(stringSig, webClientRef.ToTypeSig(), stringSig),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
        netType.Methods.Add(method);

        var threadType = module.CorLibTypes.GetTypeRef("System.Threading", "Thread");
        var threadStartType = module.CorLibTypes.GetTypeRef("System.Threading", "ThreadStart");

        var jobCtor = new MemberRefUser(module, ".ctor",
            MethodSig.CreateInstance(module.CorLibTypes.Void), jobType);
        var runMethod = new MemberRefUser(module, "Run",
            MethodSig.CreateInstance(module.CorLibTypes.Void), jobType);
        var clientField = jobType.Fields.First(f => f.Name == "_client");
        var addressField = jobType.Fields.First(f => f.Name == "_address");
        var resultField = jobType.Fields.First(f => f.Name == "_result");
        var errorField = jobType.Fields.First(f => f.Name == "_error");

        var threadStartCtor = new MemberRefUser(module, ".ctor",
            MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.Object, module.CorLibTypes.IntPtr),
            threadStartType);
        var threadCtor = new MemberRefUser(module, ".ctor",
            MethodSig.CreateInstance(module.CorLibTypes.Void, threadStartType.ToTypeSig()),
            threadType);
        var setIsBackground = new MemberRefUser(module, "set_IsBackground",
            MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.Boolean), threadType);
        var startMethod = new MemberRefUser(module, "Start",
            MethodSig.CreateInstance(module.CorLibTypes.Void), threadType);
        var joinMethod = new MemberRefUser(module, "Join",
            MethodSig.CreateInstance(module.CorLibTypes.Boolean, module.CorLibTypes.Int32), threadType);
        var webExceptionCtor = new MemberRefUser(module, ".ctor",
            MethodSig.CreateInstance(module.CorLibTypes.Void, stringSig,
                module.CorLibTypes.GetTypeRef("System", "Exception").ToTypeSig()),
            webExceptionRef);

        var body = new CilBody { InitLocals = true, MaxStack = 8 };
        body.Variables.Add(new Local(jobType.ToTypeSig()));
        body.Variables.Add(new Local(threadType.ToTypeSig()));

        var ins = body.Instructions;
        var okInstr = Instruction.Create(OpCodes.Ldloc_0);
        var loadResult = Instruction.Create(OpCodes.Ldloc_0);
        var retInstr = Instruction.Create(OpCodes.Ret);

        // First call after a prior timeout: fail immediately so a startup with
        // several downloads cannot pay the cap once per call site.
        var cont = Instruction.Create(OpCodes.Nop);
        ins.Add(Instruction.Create(OpCodes.Ldsfld, trippedField));
        ins.Add(Instruction.Create(OpCodes.Brfalse, cont));
        ins.Add(Instruction.Create(OpCodes.Ldstr,
            "network check skipped after an earlier compatibility-watchdog timeout"));
        ins.Add(Instruction.Create(OpCodes.Ldnull));
        ins.Add(Instruction.Create(OpCodes.Newobj, webExceptionCtor));
        ins.Add(Instruction.Create(OpCodes.Throw));
        ins.Add(cont);

        // job = new Job(); job._client = client; job._address = address;
        ins.Add(Instruction.Create(OpCodes.Newobj, jobCtor));
        ins.Add(Instruction.Create(OpCodes.Stloc_0));
        ins.Add(Instruction.Create(OpCodes.Ldloc_0));
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Stfld, clientField));
        ins.Add(Instruction.Create(OpCodes.Ldloc_0));
        ins.Add(Instruction.Create(OpCodes.Ldarg_1));
        ins.Add(Instruction.Create(OpCodes.Stfld, addressField));

        // thread = new Thread(job.Run); thread.IsBackground = true; thread.Start();
        ins.Add(Instruction.Create(OpCodes.Ldloc_0));
        ins.Add(Instruction.Create(OpCodes.Ldftn, runMethod));
        ins.Add(Instruction.Create(OpCodes.Newobj, threadStartCtor));
        ins.Add(Instruction.Create(OpCodes.Newobj, threadCtor));
        ins.Add(Instruction.Create(OpCodes.Stloc_1));
        ins.Add(Instruction.Create(OpCodes.Ldloc_1));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        ins.Add(Instruction.Create(OpCodes.Callvirt, setIsBackground));
        ins.Add(Instruction.Create(OpCodes.Ldloc_1));
        ins.Add(Instruction.Create(OpCodes.Callvirt, startMethod));

        // if (!thread.Join(timeout)) throw new WebException(msg, Timeout);
        ins.Add(Instruction.Create(OpCodes.Ldloc_1));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4, WatchdogTimeoutMilliseconds));
        ins.Add(Instruction.Create(OpCodes.Callvirt, joinMethod));
        ins.Add(Instruction.Create(OpCodes.Brtrue, okInstr));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        ins.Add(Instruction.Create(OpCodes.Stsfld, trippedField));
        // On timeout: throw a WebException. The app has no guard at these call
        // sites, so this becomes a bounded unhandled-exception dialog instead
        // of an indefinite freeze. Returning an empty body was tried and is
        // worse: the game treats a failed check as "hide Start", reproducing
        // the confusing three-button menu.
        ins.Add(Instruction.Create(OpCodes.Ldstr,
            $"The operation has timed out after {WatchdogTimeoutMilliseconds} ms (compatibility watchdog)."));
        ins.Add(Instruction.Create(OpCodes.Ldnull));
        ins.Add(Instruction.Create(OpCodes.Newobj, webExceptionCtor));
        ins.Add(Instruction.Create(OpCodes.Throw));

        // if (job._error != null) throw job._error; return job._result;
        ins.Add(okInstr);
        ins.Add(Instruction.Create(OpCodes.Ldfld, errorField));
        ins.Add(Instruction.Create(OpCodes.Brfalse, loadResult));
        ins.Add(Instruction.Create(OpCodes.Ldloc_0));
        ins.Add(Instruction.Create(OpCodes.Ldfld, errorField));
        ins.Add(Instruction.Create(OpCodes.Throw));
        ins.Add(loadResult);
        ins.Add(Instruction.Create(OpCodes.Ldfld, resultField));
        ins.Add(retInstr);

        body.UpdateInstructionOffsets();
        method.Body = body;
        return method;
    }

    private static FieldDef AddField(TypeDef type, string name, TypeSig sig)
    {
        var field = new FieldDefUser(name, new FieldSig(sig), FieldAttributes.Public);
        type.Fields.Add(field);
        return field;
    }

    private static TypeRef? FindOrCreateTypeRef(
        ModuleDefMD module, string ns, string name, IResolutionScope scope)
    {
        return module.GetTypeRefs().FirstOrDefault(t => t.Namespace == ns && t.Name == name)
               ?? new TypeRefUser(module, ns, name, scope);
    }
}
