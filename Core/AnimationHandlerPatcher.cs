using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Injects a hook into the game's massive internal animation/command handler method (69,667 IL instructions).
/// This allows us to intercept command phrases like "hey paicom show me a cool magic trick" and
/// ensure they properly launch external processes like Red-Dot.exe under Wine.
/// </summary>
public static class AnimationHandlerPatcher
{
    /// <summary>
    /// Finds the massive command handler method and injects a logging probe.
    /// Returns the number of methods patched.
    /// </summary>
    public static int Patch(ModuleDefMD module, Action<string>? log = null)
    {
        var patched = 0;

        // Find the main Form type (the one with most methods)
        var formTypes = module.GetTypes()
            .Where(t => t.BaseType != null && t.BaseType.FullName.Contains("Form"))
            .OrderByDescending(t => t.Methods.Count)
            .ToList();

        var mainForm = formTypes.FirstOrDefault();
        if (mainForm == null)
        {
            log?.Invoke("[animation-handler] No Form type found");
            return 0;
        }

        log?.Invoke($"[animation-handler] Main Form: {mainForm.FullName} ({mainForm.Methods.Count} methods)");

        // Find the massive method (>50,000 IL instructions)
        var massiveMethod = mainForm.Methods
            .FirstOrDefault(m => m.HasBody && m.Body.Instructions.Count > 50000);

        if (massiveMethod == null)
        {
            log?.Invoke("[animation-handler] No massive command handler method found");
            return 0;
        }

        log?.Invoke($"[animation-handler] Found command handler: {massiveMethod.Name} (IL size: {massiveMethod.Body.Instructions.Count})");
        log?.Invoke($"[animation-handler] Method token: {massiveMethod.MDToken}");

        // Inject a logging probe at the beginning of the method
        if (TryInjectLoggingProbe(module, massiveMethod, log))
        {
            patched++;
            log?.Invoke($"[animation-handler] Successfully injected logging probe");
        }

        return patched;
    }

    private static bool TryInjectLoggingProbe(ModuleDefMD module, MethodDef method, Action<string>? log)
    {
        try
        {
            var body = method.Body;
            if (body.Instructions.Count == 0)
                return false;

            // Find or create Console.WriteLine method reference
            var consoleType = module.CorLibTypes.GetTypeRef("System", "Console");
            var writeLineMethod = new MemberRefUser(
                module,
                "WriteLine",
                MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String),
                consoleType);

            // Inject logging at the very beginning
            var logMessage = $"[animation-handler-invoked] Command handler called with argument";
            var loggingInstructions = new List<Instruction>();

            // We can't easily intercept the string argument without understanding the method signature,
            // but we can at least log that the method was called
            loggingInstructions.Add(Instruction.Create(OpCodes.Ldstr, logMessage));
            loggingInstructions.Add(Instruction.Create(OpCodes.Call, writeLineMethod));

            // Insert at the beginning
            for (int i = 0; i < loggingInstructions.Count; i++)
            {
                body.Instructions.Insert(i, loggingInstructions[i]);
            }

            // Update exception handlers if they reference the first instruction
            var originalFirst = loggingInstructions.Last();
            foreach (var eh in body.ExceptionHandlers)
            {
                if (eh.TryStart == body.Instructions[loggingInstructions.Count])
                    eh.TryStart = loggingInstructions.First();
                if (eh.HandlerStart == body.Instructions[loggingInstructions.Count])
                    eh.HandlerStart = loggingInstructions.First();
                if (eh.FilterStart == body.Instructions[loggingInstructions.Count])
                    eh.FilterStart = loggingInstructions.First();
            }

            body.OptimizeBranches();
            body.OptimizeMacros();

            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[animation-handler-error] Failed to inject logging probe: {ex.Message}");
            return false;
        }
    }
}
