using dnlib.DotNet;
using System.Reflection;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Reads the Vosk native libraries and managed wrapper DLLs that were embedded
/// into this patcher binary, then injects them as named resource streams into
/// the target module.
///
/// At runtime inside the patched exe, the injected HotSwapRuntime reads these
/// resources via Assembly.GetExecutingAssembly().GetManifestResourceStream()
/// and self-extracts the correct one for its OS before loading Vosk.
///
/// Resource naming convention (matches HotSwapTemplate expectations):
///   vosk.native.win-x64.dll
///   vosk.native.linux-x64.so
///   vosk.native.linux-arm64.so
///   vosk.native.osx.dylib
///   vosk.managed.dll
///   naudio.core.dll
///   naudio.winmm.dll
///   newtonsoft.json.dll
/// </summary>
public static class VoskResourceEmbedder
{
    // All resource names the injected runtime may look for.
    // The patcher may not have all of them (conditional EmbeddedResource in .csproj).
    private static readonly string[] KnownResources =
    [
        "vosk.native.win-x64.dll",
        "vosk.native.win-gcc.dll",
        "vosk.native.win-stdc.dll",
        "vosk.native.win-pthread.dll",
        "vosk.native.linux-x64.so",
        "vosk.native.linux-arm64.so",
        "vosk.native.osx.dylib",
        "vosk.managed.dll",
        "naudio.core.dll",
        "naudio.winmm.dll",
        "newtonsoft.json.dll",
    ];

    /// <summary>
    /// Copies all Vosk/NAudio resources from this patcher exe into
    /// <paramref name="targetModule"/> as embedded module resources.
    /// Resources that are not present in the patcher (built without them)
    /// are silently skipped — the injected runtime handles missing natives
    /// gracefully by falling back to file-only command dispatch.
    /// </summary>
    public static void EmbedIntoModule(ModuleDef targetModule, bool verbose = false)
    {
        var patcherAsm = Assembly.GetExecutingAssembly();
        int count = 0;

        foreach (var name in KnownResources)
        {
            using var stream = patcherAsm.GetManifestResourceStream(name);
            if (stream is null)
            {
                if (verbose)
                    Console.WriteLine($"    [vosk-embed] {name} — not present in patcher, skipping.");
                continue;
            }

            // Read the resource bytes
            byte[] data;
            using (var ms = new MemoryStream((int)stream.Length))
            {
                stream.CopyTo(ms);
                data = ms.ToArray();
            }

            // Check if already injected (idempotent for re-patch scenarios)
            bool alreadyPresent = targetModule.Resources
                .Any(r => r.Name == name);
            if (alreadyPresent)
            {
                if (verbose)
                    Console.WriteLine($"    [vosk-embed] {name} — already present in target, skipping.");
                continue;
            }

            // Add as an EmbeddedResource in the target module
            var resource = new EmbeddedResource(
                name,
                data,
                dnlib.DotNet.ManifestResourceAttributes.Public);

            targetModule.Resources.Add(resource);
            count++;

            if (verbose)
                Console.WriteLine($"    [vosk-embed] {name} — embedded ({data.Length:N0} bytes).");
        }

        Console.WriteLine($"    [vosk-embed] Embedded {count} resource(s) into target module.");
    }
}

