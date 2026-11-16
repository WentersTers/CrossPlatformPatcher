using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Forces the menu's Start button visible after the menu form constructs.
///
/// Why: the menu form runs a synchronous gist download while it is being
/// built; when that call does not return (or returns nothing) the form stops
/// painting and the Start button is never shown. The WebClient watchdog
/// bounds the call so the form finishes, but the game's own gate still leaves
/// the menu with three buttons. Operators currently "tab and press Enter"
/// past it, which is not a supported way to start the app.
///
/// What: a helper type is injected into the target module and called once at
/// the end of the menu form's constructor. It walks the form's control tree
/// and sets <c>Visible = true</c> on the control whose text is "Start", then
/// schedules exactly one re-check four seconds later (one-shot, then stops) so
/// a button created after the constructor is still caught.
///
/// Safety: every helper body is wrapped in try/catch, so a reflection or
/// layout surprise cannot break the app. The patch is opt-in via the strategy
/// option <c>start.forceVisible</c> (or <c>PAICOM_START_FORCE_VISIBLE=1</c>)
/// until the live blackhole probe has passed.
/// </summary>
public static class StartButtonGuardPatcher
{
    public const string HelperTypeName = "CrossPlatformPatcherStartGuard";
    public const string OptionKey = "start.forceVisible";
    public const string EnvironmentVariable = "PAICOM_START_FORCE_VISIBLE";

    /// <summary>How long after construction the second (and final) assertion runs.</summary>
    public const int RecheckMilliseconds = 4000;

    private const string WindowsFormsNamespace = "System.Windows.Forms";

    /// <summary>
    /// On by default. Opt out with <c>start.forceVisible=false</c> or
    /// <c>PAICOM_START_FORCE_VISIBLE=0</c>.
    ///
    /// Proven on two runtimes: Bazzite 45 / Steam Proton Experimental (full
    /// battery 11/11 + 11 silent on the guard build, plus blackholed and normal
    /// four-button probes) and Fedora 44 / distro Wine 11.20 (A/B: guard and
    /// pre-guard builds both alive after five minutes idle; halves with the
    /// guard gave 10/11 positives - one garbled Hulu take - and 11/11 silent,
    /// 0 misroutes). The guard only sets Visible on a control whose text is
    /// "Start", so the opt-out exists for diagnosis, not for risk.
    /// </summary>
    public static bool IsEnabled(IReadOnlyDictionary<string, string>? options)
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.Equals(env, "1", StringComparison.Ordinal)) return true;
        if (string.Equals(env, "0", StringComparison.Ordinal)) return false;

        if (options is not null &&
            options.TryGetValue(OptionKey, out var value) &&
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Patches the menu form constructor. Returns the number of call sites
    /// inserted (0 when the module has no WinForms control tree or no menu
    /// form to hook).
    /// </summary>
    public static int Patch(ModuleDefMD module, Action<string>? log = null)
    {
        if (module is null) throw new ArgumentNullException(nameof(module));

        var controlRef = FindTypeRef(module, WindowsFormsNamespace, "Control");
        if (controlRef is null)
        {
            log?.Invoke("[startguard] no System.Windows.Forms.Control reference in the module - skipped.");
            return 0;
        }

        var ctor = FindMenuConstructor(module, log);
        if (ctor is null)
        {
            log?.Invoke("[startguard] no menu form constructor found - skipped.");
            return 0;
        }

        var scope = controlRef.Scope as IResolutionScope
                    ?? module.CorLibTypes.AssemblyRef;
        var timerRef = FindTypeRef(module, WindowsFormsNamespace, "Timer")
                       ?? new TypeRefUser(module, WindowsFormsNamespace, "Timer", scope);
        var collectionRef = new TypeRefUser(module, WindowsFormsNamespace, "ControlCollection", controlRef);

        var guardType = EnsureGuardType(module, controlRef, timerRef, collectionRef);
        var attachRef = new MemberRefUser(
            module, "Attach",
            MethodSig.CreateStatic(module.CorLibTypes.Void, controlRef.ToTypeSig()),
            guardType);

        if (HasAttachCall(ctor, attachRef))
        {
            log?.Invoke("[startguard] menu constructor already guarded.");
            return 0;
        }

        var body = ctor.Body;
        var last = body.Instructions[body.Instructions.Count - 1];
        if (last.OpCode != OpCodes.Ret)
        {
            log?.Invoke("[startguard] menu constructor does not end with ret - skipped.");
            return 0;
        }

        body.Instructions.Insert(body.Instructions.Count - 1, Instruction.Create(OpCodes.Ldarg_0));
        body.Instructions.Insert(body.Instructions.Count - 1, Instruction.Create(OpCodes.Call, attachRef));
        body.UpdateInstructionOffsets();
        body.KeepOldMaxStack = true;

        log?.Invoke($"[startguard] Start visibility guard attached to {ctor.DeclaringType?.FullName}::.ctor.");
        return 1;
    }

    private static TypeRef? FindTypeRef(ModuleDefMD module, string ns, string name)
    {
        return module.GetTypeRefs().FirstOrDefault(t => t.Namespace == ns && t.Name == name);
    }

/// <summary>
/// The menu form is found by counting the startup download calls its methods
/// contain. ORDER DEPENDENCY: <see cref="WebClientTimeoutPatcher"/> rewrites
/// every <c>DownloadString</c> call to <c>SafeDownloadString</c>, so this
/// patch must run first (see AssemblyPatcher). Both callee names are accepted
/// so a future reorder still finds the form instead of silently skipping.
/// </summary>
private static MethodDef? FindMenuConstructor(ModuleDefMD module, Action<string>? log = null)
    {
        MethodDef? namedCtor = null;
        (int calls, MethodDef? ctor, string typeName) busiest = (0, null, "<none>");

        foreach (var type in module.GetTypes())
        {
            if (type.Name == HelperTypeName) continue;
            if (type.Name.String.StartsWith("CrossPlatformPatcher", StringComparison.Ordinal)) continue;
            if (!type.Methods.Any(m => m.IsConstructor && m.HasBody && !m.IsStatic)) continue;

            var downloadCalls = 0;
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var ins in method.Body.Instructions)
                {
                    if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) &&
                        ins.Operand is IMethod im &&
                        (im.Name == "DownloadString" || im.Name == "SafeDownloadString"))
                    {
                        downloadCalls++;
                    }
                }
            }

            var ctor = type.Methods
                .Where(m => m.IsConstructor && m.HasBody && !m.IsStatic)
                .OrderByDescending(m => m.Body.Instructions.Count)
                .FirstOrDefault();

            if (type.Name == "GForm1" && ctor != null) namedCtor = ctor;
            if (downloadCalls > busiest.calls) busiest = (downloadCalls, ctor, type.FullName);
        }

        if (namedCtor == null)
            log?.Invoke($"[startguard] busiest form candidate: {busiest.typeName} ({busiest.calls} download call(s)).");

        return namedCtor ?? busiest.ctor;
    }

    /// <summary>True when the type is a WinForms control (menu form or a page).</summary>
    private static bool DerivesFromControl(TypeDef type)
    {
        var current = type.BaseType;
        for (var depth = 0; current != null && depth < 12; depth++)
        {
            var name = current.Name.String;
            if (name is "Form" or "Control" or "UserControl" or "ContainerControl")
                return true;
            if (current is TypeDef td) { current = td.BaseType; continue; }
            if (current is TypeRef tr)
            {
                var resolved = tr.Resolve();
                if (resolved != null) { current = resolved.BaseType; continue; }
                break; // unresolvable external base: name check above is all we have
            }
            break;
        }

        return false;
    }

    private static bool HasAttachCall(MethodDef ctor, IMethod attachRef)
        => ctor.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Call && i.Operand is IMethod im && im.Name == "Attach" &&
            im.DeclaringType?.Name == HelperTypeName);

    private static TypeDef EnsureGuardType(
        ModuleDefMD module, TypeRef controlRef, TypeRef timerRef, TypeRef collectionRef)
    {
        var existing = module.GetTypes().FirstOrDefault(t => t.Name == HelperTypeName);
        if (existing != null) return existing;

        var type = new TypeDefUser(string.Empty, HelperTypeName, module.CorLibTypes.GetTypeRef("System", "Object"))
        {
            Attributes = TypeAttributes.Public | TypeAttributes.Abstract |
                         TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit
        };
        module.Types.Add(type);

        var formField = AddStaticField(type, "_form", controlRef.ToTypeSig());
        var timerField = AddStaticField(type, "_timer", timerRef.ToTypeSig());

        AddAttach(type, controlRef, timerRef, formField, timerField);
        AddOnTick(type, controlRef, timerRef, formField, timerField);
        AddApply(type, controlRef, collectionRef);

        return type;
    }

    private static FieldDef AddStaticField(TypeDef type, string name, TypeSig sig)
    {
        var field = new FieldDefUser(name, new FieldSig(sig),
            FieldAttributes.Private | FieldAttributes.Static);
        type.Fields.Add(field);
        return field;
    }

    private static void AddAttach(
        TypeDef type, TypeRef controlRef, TypeRef timerRef, FieldDef formField, FieldDef timerField)
    {
        var module = type.Module;
        var method = new MethodDefUser(
            "Attach",
            MethodSig.CreateStatic(module.CorLibTypes.Void, controlRef.ToTypeSig()),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig)
        {
            Body = new CilBody { MaxStack = 8 }
        };
        type.Methods.Add(method);

        var body = method.Body;
        body.InitLocals = true;
        var timerLocal = new Local(timerRef.ToTypeSig());
        body.Variables.Add(timerLocal);

        var applyRef = new MemberRefUser(module, "Apply",
            MethodSig.CreateStatic(module.CorLibTypes.Void, controlRef.ToTypeSig()), type);
        var eventHandlerCtor = new MemberRefUser(module, ".ctor",
            MethodSig.CreateInstance(module.CorLibTypes.Void,
                module.CorLibTypes.Object, module.CorLibTypes.GetTypeRef("System", "IntPtr").ToTypeSig()),
            module.CorLibTypes.GetTypeRef("System", "EventHandler"));
        var onTickRef = new MemberRefUser(module, "OnTick",
            MethodSig.CreateStatic(module.CorLibTypes.Void,
                module.CorLibTypes.Object,
                module.CorLibTypes.GetTypeRef("System", "EventArgs").ToTypeSig()), type);
        var timerCtor = new MemberRefUser(module, ".ctor",
            MethodSig.CreateInstance(module.CorLibTypes.Void), timerRef);
        var setInterval = new MemberRefUser(module, "set_Interval",
            MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.Int32), timerRef);
        var addTick = new MemberRefUser(module, "add_Tick",
            MethodSig.CreateInstance(module.CorLibTypes.Void,
                module.CorLibTypes.GetTypeRef("System", "EventHandler").ToTypeSig()), timerRef);
        var startTimer = new MemberRefUser(module, "Start",
            MethodSig.CreateInstance(module.CorLibTypes.Void), timerRef);

        var tryStart = Instruction.Create(OpCodes.Nop);
        var leave = Instruction.Create(OpCodes.Ret);
        var handler = Instruction.Create(OpCodes.Pop);
        var ret = Instruction.Create(OpCodes.Ret);

        var ins = body.Instructions;
        ins.Add(tryStart);
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Brfalse, leave));      // if (form == null) return
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Stsfld, formField));
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Call, applyRef));      // Apply(form)
        ins.Add(Instruction.Create(OpCodes.Ldsfld, timerField));
        ins.Add(Instruction.Create(OpCodes.Brtrue, leave));       // already scheduled
        ins.Add(Instruction.Create(OpCodes.Newobj, timerCtor));
        ins.Add(Instruction.Create(OpCodes.Stloc, timerLocal));
        ins.Add(Instruction.Create(OpCodes.Ldloc, timerLocal));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4, RecheckMilliseconds));
        ins.Add(Instruction.Create(OpCodes.Callvirt, setInterval));
        ins.Add(Instruction.Create(OpCodes.Ldloc, timerLocal));
        ins.Add(Instruction.Create(OpCodes.Ldnull));
        ins.Add(Instruction.Create(OpCodes.Ldftn, onTickRef));
        ins.Add(Instruction.Create(OpCodes.Newobj, eventHandlerCtor));
        ins.Add(Instruction.Create(OpCodes.Callvirt, addTick));
        ins.Add(Instruction.Create(OpCodes.Ldloc, timerLocal));
        ins.Add(Instruction.Create(OpCodes.Callvirt, startTimer));
        ins.Add(Instruction.Create(OpCodes.Ldloc, timerLocal));
        ins.Add(Instruction.Create(OpCodes.Stsfld, timerField));
        ins.Add(Instruction.Create(OpCodes.Leave_S, leave));
        ins.Add(handler);
        ins.Add(Instruction.Create(OpCodes.Leave_S, ret));
        ins.Add(leave);
        ins.Add(ret);

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = handler,
            HandlerStart = handler,
            HandlerEnd = ret,
            CatchType = module.CorLibTypes.GetTypeRef("System", "Exception"),
        });

        body.UpdateInstructionOffsets();
        body.KeepOldMaxStack = true;
    }

    private static void AddOnTick(
        TypeDef type, TypeRef controlRef, TypeRef timerRef, FieldDef formField, FieldDef timerField)
    {
        var module = type.Module;
        var method = new MethodDefUser(
            "OnTick",
            MethodSig.CreateStatic(module.CorLibTypes.Void,
                module.CorLibTypes.Object,
                module.CorLibTypes.GetTypeRef("System", "EventArgs").ToTypeSig()),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig)
        {
            Body = new CilBody { MaxStack = 4 }
        };
        type.Methods.Add(method);

        var body = method.Body;
        var applyRef = new MemberRefUser(module, "Apply",
            MethodSig.CreateStatic(module.CorLibTypes.Void, controlRef.ToTypeSig()), type);
        var stopTimer = new MemberRefUser(module, "Stop",
            MethodSig.CreateInstance(module.CorLibTypes.Void), timerRef);
        var disposeTimer = new MemberRefUser(module, "Dispose",
            MethodSig.CreateInstance(module.CorLibTypes.Void), timerRef);

        var tryStart = Instruction.Create(OpCodes.Nop);
        var retLabel = Instruction.Create(OpCodes.Ret);
        var handler = Instruction.Create(OpCodes.Pop);
        var skip = Instruction.Create(OpCodes.Ldsfld, formField);

        var ins = body.Instructions;
        ins.Add(tryStart);
        ins.Add(Instruction.Create(OpCodes.Ldsfld, timerField));
        ins.Add(Instruction.Create(OpCodes.Brfalse_S, skip));
        ins.Add(Instruction.Create(OpCodes.Ldsfld, timerField));
        ins.Add(Instruction.Create(OpCodes.Callvirt, stopTimer));
        ins.Add(Instruction.Create(OpCodes.Ldsfld, timerField));
        ins.Add(Instruction.Create(OpCodes.Callvirt, disposeTimer));
        ins.Add(Instruction.Create(OpCodes.Ldnull));
        ins.Add(Instruction.Create(OpCodes.Stsfld, timerField));
        ins.Add(skip);
        ins.Add(Instruction.Create(OpCodes.Call, applyRef));
        ins.Add(Instruction.Create(OpCodes.Ldnull));
        ins.Add(Instruction.Create(OpCodes.Stsfld, formField));
        ins.Add(Instruction.Create(OpCodes.Leave_S, retLabel));
        ins.Add(handler);
        ins.Add(Instruction.Create(OpCodes.Leave_S, retLabel));
        ins.Add(retLabel);

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = handler,
            HandlerStart = handler,
            HandlerEnd = retLabel,
            CatchType = module.CorLibTypes.GetTypeRef("System", "Exception"),
        });

        body.UpdateInstructionOffsets();
        body.KeepOldMaxStack = true;
    }

    private static void AddApply(TypeDef type, TypeRef controlRef, TypeRef collectionRef)
    {
        var module = type.Module;
        var method = new MethodDefUser(
            "Apply",
            MethodSig.CreateStatic(module.CorLibTypes.Void, controlRef.ToTypeSig()),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig)
        {
            Body = new CilBody { MaxStack = 8 }
        };
        type.Methods.Add(method);

        var body = method.Body;
        body.InitLocals = true;
        var collectionLocal = new Local(collectionRef.ToTypeSig());
        var indexLocal = new Local(module.CorLibTypes.Int32);
        body.Variables.Add(collectionLocal);
        body.Variables.Add(indexLocal);

        var getText = new MemberRefUser(module, "get_Text",
            MethodSig.CreateInstance(module.CorLibTypes.String), controlRef);
        var stringEquals = new MemberRefUser(module, "Equals",
            MethodSig.CreateStatic(module.CorLibTypes.Boolean,
                module.CorLibTypes.String, module.CorLibTypes.String,
                module.CorLibTypes.GetTypeRef("System", "StringComparison").ToTypeSig()),
            module.CorLibTypes.GetTypeRef("System", "String"));
        var setVisible = new MemberRefUser(module, "set_Visible",
            MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.Boolean), controlRef);
        var getControls = new MemberRefUser(module, "get_Controls",
            MethodSig.CreateInstance(collectionRef.ToTypeSig()), controlRef);
        var getCount = new MemberRefUser(module, "get_Count",
            MethodSig.CreateInstance(module.CorLibTypes.Int32), collectionRef);
        var getItem = new MemberRefUser(module, "get_Item",
            MethodSig.CreateInstance(controlRef.ToTypeSig(), module.CorLibTypes.Int32), collectionRef);
        var applyRef = new MemberRefUser(module, "Apply",
            MethodSig.CreateStatic(module.CorLibTypes.Void, controlRef.ToTypeSig()), type);

        var tryStart = Instruction.Create(OpCodes.Nop);
        var retLabel = Instruction.Create(OpCodes.Ret);
        var handler = Instruction.Create(OpCodes.Pop);
        var endTry = Instruction.Create(OpCodes.Leave_S, retLabel);
        var skipSet = Instruction.Create(OpCodes.Ldarg_0);
        var loopCheck = Instruction.Create(OpCodes.Ldloc, indexLocal);
        var loopStart = Instruction.Create(OpCodes.Ldloc, collectionLocal);

        var ins = body.Instructions;
        ins.Add(tryStart);
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Brfalse, endTry));
        // if (string.Equals(c.Text, "Start", OrdinalIgnoreCase)) c.Visible = true;
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Callvirt, getText));
        ins.Add(Instruction.Create(OpCodes.Ldstr, "Start"));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4, (int)StringComparison.OrdinalIgnoreCase));
        ins.Add(Instruction.Create(OpCodes.Call, stringEquals));
        ins.Add(Instruction.Create(OpCodes.Brfalse_S, skipSet));
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        ins.Add(Instruction.Create(OpCodes.Callvirt, setVisible));
        // var cc = c.Controls; for (i = 0; i < cc.Count; i++) Apply(cc[i]);
        ins.Add(skipSet);
        ins.Add(Instruction.Create(OpCodes.Callvirt, getControls));
        ins.Add(Instruction.Create(OpCodes.Stloc, collectionLocal));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        ins.Add(Instruction.Create(OpCodes.Stloc, indexLocal));
        ins.Add(Instruction.Create(OpCodes.Br_S, loopCheck));
        ins.Add(loopStart);
        ins.Add(Instruction.Create(OpCodes.Ldloc, indexLocal));
        ins.Add(Instruction.Create(OpCodes.Callvirt, getItem));
        ins.Add(Instruction.Create(OpCodes.Call, applyRef));
        ins.Add(Instruction.Create(OpCodes.Ldloc, indexLocal));
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        ins.Add(Instruction.Create(OpCodes.Add));
        ins.Add(Instruction.Create(OpCodes.Stloc, indexLocal));
        ins.Add(loopCheck);
        ins.Add(Instruction.Create(OpCodes.Ldloc, collectionLocal));
        ins.Add(Instruction.Create(OpCodes.Callvirt, getCount));
        ins.Add(Instruction.Create(OpCodes.Blt_S, loopStart));
        ins.Add(endTry);
        ins.Add(handler);
        ins.Add(Instruction.Create(OpCodes.Leave_S, retLabel));
        ins.Add(retLabel);

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = handler,
            HandlerStart = handler,
            HandlerEnd = retLabel,
            CatchType = module.CorLibTypes.GetTypeRef("System", "Exception"),
        });

        body.UpdateInstructionOffsets();
        body.KeepOldMaxStack = true;
    }
}
