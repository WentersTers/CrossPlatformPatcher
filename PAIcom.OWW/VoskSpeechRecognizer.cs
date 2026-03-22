using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Cross-platform Vosk speech recognizer for post-wake-word recognition.
/// 
/// Loads Vosk models at runtime, accepts audio chunks, and returns recognized speech.
/// Used as the fallback/replacement for System.Speech on non-Windows platforms (Wine/macOS/Linux).
/// </summary>
public class VoskSpeechRecognizer : IDisposable
{
    private readonly Action<string>? _logger;
    private dynamic? _voskModel;
    private dynamic? _voskRecognizer;
    private bool _disposed;

    public VoskSpeechRecognizer(Action<string>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize Vosk model and create recognizer instance.
    /// Returns true if successful, false if Vosk unavailable or model not found.
    /// </summary>
    public bool Initialize()
    {
        try
        {
            LogEvent("[vosk-speech] Initializing Vosk speech recognizer...");

            if (!Environment.Is64BitProcess)
            {
                LogEvent("[vosk-speech] Vosk disabled: running in 32-bit process; native Vosk initialization is unstable in this mode.");
                return false;
            }

            var migrationMode = Environment.GetEnvironmentVariable("PAICOM_MIGRATION_MODE") ?? "stable";
            var verifiedRuntime = Environment.GetEnvironmentVariable("PAICOM_RUNTIME_VERIFIED_64BIT") ?? "0";
            
            // In Stable mode, keep Vosk disabled for maximum safety
            if (string.Equals(migrationMode, "stable", StringComparison.OrdinalIgnoreCase))
            {
                LogEvent("[vosk-speech] Vosk disabled: stable mode requires no advanced features");
                return false;
            }
            
            // In Probe mode, require launcher verification of 64-bit runtime
            if (string.Equals(migrationMode, "probe", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(verifiedRuntime, "1", StringComparison.Ordinal))
                {
                    LogEvent("[vosk-speech] Vosk disabled: probe mode requires launcher verification of 64-bit runtime");
                    return false;
                }
                LogEvent("[vosk-speech] Vosk enabled: probe mode with runtime verification confirmed");
            }
            
            // In Full mode, allow Vosk if we're 64-bit (already checked above)
            if (string.Equals(migrationMode, "full", StringComparison.OrdinalIgnoreCase))
            {
                LogEvent("[vosk-speech] Vosk enabled: full 64-bit mode");
            }

            // Try to load Vosk.dll from embedded resources
            if (!LoadVoskAssembly())
            {
                LogEvent("[vosk-speech] Failed to load Vosk assembly");
                return false;
            }

            // Load or download model
            var modelPath = GetOrDownloadModel();
            if (string.IsNullOrEmpty(modelPath) || !Directory.Exists(modelPath))
            {
                LogEvent($"[vosk-speech] Model not found at: {modelPath}");
                return false;
            }

            LogEvent($"[vosk-speech] Model loaded from: {modelPath}");

            // Create Vosk model and recognizer
            try
            {
                var voskType = FindVoskType("Vosk.Vosk", "Vosk");
                var modelType = FindVoskType("Vosk.Model", "Model");
                var recognizerType = FindVoskType("Vosk.VoskRecognizer", "VoskRecognizer");

                if (modelType == null || recognizerType == null)
                {
                    LogEvent("[vosk-speech] Vosk types not found after loading assembly");
                    return false;
                }

                LogEvent($"[vosk-speech] Resolved types: Vosk={voskType?.FullName}, Model={modelType.FullName}, Recognizer={recognizerType.FullName}");

                // Initialize Vosk (required on some platforms)
                var initMethod = voskType?.GetMethod("SetLogLevel", BindingFlags.Public | BindingFlags.Static);
                if (initMethod != null)
                {
                    initMethod.Invoke(null, new object[] { 0 }); // 0 = no debug output
                    LogEvent("[vosk-speech] Vosk logging level set");
                }

                // Load model
                try
                {
                    _voskModel = Activator.CreateInstance(modelType, modelPath);
                    if (_voskModel == null)
                    {
                        LogEvent("[vosk-speech] Failed to create Vosk model instance");
                        return false;
                    }
                    LogEvent("[vosk-speech] Vosk model instance created successfully");
                }
                catch (Exception modelEx)
                {
                    LogEvent($"[vosk-speech] Error creating model instance: {modelEx.Message}");
                    if (modelEx.InnerException != null)
                        LogEvent($"[vosk-speech] (Inner: {modelEx.InnerException.Message})");
                    return false;
                }

                // Create recognizer
                try
                {
                    _voskRecognizer = Activator.CreateInstance(recognizerType, _voskModel, 16000.0f);
                    if (_voskRecognizer == null)
                    {
                        LogEvent("[vosk-speech] Failed to create Vosk recognizer instance");
                        return false;
                    }
                    LogEvent("[vosk-speech] Vosk recognizer instance created successfully");
                }
                catch (Exception recEx)
                {
                    LogEvent($"[vosk-speech] Error creating recognizer instance: {recEx.Message}");
                    if (recEx.InnerException != null)
                        LogEvent($"[vosk-speech] (Inner: {recEx.InnerException.Message})");
                    return false;
                }

                LogEvent("[vosk-speech] Vosk recognizer initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                var innerEx = ex.InnerException;
                var errorMsg = $"[vosk-speech] Exception creating Vosk instance: {ex.Message}";
                if (innerEx != null)
                    errorMsg += $" (Inner: {innerEx.Message})";
                LogEvent(errorMsg);
                LogEvent($"[vosk-speech] Stack: {ex.StackTrace}");
                return false;
            }
        }
        catch (Exception ex)
        {
            LogEvent($"[vosk-speech] Initialization failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Feed audio chunk to recognizer. Returns recognized text if available.
    /// </summary>
    public string? ProcessAudioChunk(byte[] audioData)
    {
        if (_voskRecognizer == null)
            return null;

        try
        {
            var recognizerType = _voskRecognizer.GetType();
            var acceptMethod = recognizerType.GetMethod("AcceptWaveform", new[] { typeof(byte[]), typeof(int) })
                              ?? recognizerType.GetMethod("AcceptWaveform");

            if (acceptMethod == null)
                return null;

            // Feed audio to recognizer
            acceptMethod.Invoke(_voskRecognizer, new object[] { audioData, audioData.Length });

            // Check if we have a final result
            var result = GetStringMemberValue(_voskRecognizer, "Result");
            if (!string.IsNullOrEmpty(result) && result != "{}")
            {
                LogEvent($"[vosk-speech] Result: {result}");
                return result;
            }

            // Check partial result for ongoing recognition
            var partial = GetStringMemberValue(_voskRecognizer, "PartialResult");
            if (!string.IsNullOrEmpty(partial) && partial != "{}")
            {
                LogEvent($"[vosk-speech] Partial: {partial}");
            }

            return null;
        }
        catch (Exception ex)
        {
            LogEvent($"[vosk-speech] Error processing audio: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get final recognition result.
    /// </summary>
    public string? GetFinalResult()
    {
        if (_voskRecognizer == null)
            return null;

        try
        {
            var result = GetStringMemberValue(_voskRecognizer, "Result");
            return (!string.IsNullOrEmpty(result) && result != "{}") ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private bool LoadVoskAssembly()
    {
        try
        {
            // Try resource load from executing, entry, then all loaded assemblies.
            var stream = FindVoskManagedResourceStream();
            if (stream != null)
            {
                using (stream)
                {
                    var buffer = new byte[stream.Length];
                    stream.Read(buffer, 0, buffer.Length);
                    Assembly.Load(buffer);
                }

                LogEvent("[vosk-speech] Loaded Vosk.dll from embedded resources");
                return true;
            }

            // Fallback: try to load from file system
            var voskDll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Vosk.dll");
            if (File.Exists(voskDll))
            {
                Assembly.LoadFrom(voskDll);
                LogEvent($"[vosk-speech] Loaded Vosk.dll from: {voskDll}");
                return true;
            }

            LogEvent("[vosk-speech] Vosk.dll not found");
            return false;
        }
        catch (Exception ex)
        {
            LogEvent($"[vosk-speech] Error loading Vosk assembly: {ex.Message}");
            return false;
        }
    }

    private static Stream? FindVoskManagedResourceStream()
    {
        var executing = Assembly.GetExecutingAssembly();
        var stream = executing.GetManifestResourceStream("vosk.managed.dll");
        if (stream != null)
            return stream;

        var entry = Assembly.GetEntryAssembly();
        if (entry != null)
        {
            stream = entry.GetManifestResourceStream("vosk.managed.dll");
            if (stream != null)
                return stream;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            stream = assembly.GetManifestResourceStream("vosk.managed.dll");
            if (stream != null)
                return stream;
        }

        return null;
    }

    private static Type? FindVoskType(params string[] candidateNames)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var candidate in candidateNames)
            {
                var resolved = assembly.GetType(candidate, throwOnError: false, ignoreCase: false);
                if (resolved != null)
                    return resolved;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                foreach (var candidate in candidateNames)
                {
                    if (string.Equals(type.FullName, candidate, StringComparison.Ordinal) ||
                        string.Equals(type.Name, candidate, StringComparison.Ordinal) ||
                        (type.FullName != null && type.FullName.EndsWith("." + candidate, StringComparison.Ordinal)))
                    {
                        return type;
                    }
                }
            }
        }

        return null;
    }

    private static string? GetStringMemberValue(object instance, string memberName)
    {
        var type = instance.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (property != null)
            return property.GetValue(instance) as string;

        var method = type.GetMethod(memberName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (method != null && method.ReturnType == typeof(string))
            return method.Invoke(instance, null) as string;

        return null;
    }

    private string? GetOrDownloadModel()
    {
        var explicitPath = Environment.GetEnvironmentVariable("PAICOM_VOSK_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath) && LooksLikeVoskModelDirectory(explicitPath))
        {
            LogEvent($"[vosk-speech] Model selected via PAICOM_VOSK_MODEL_PATH: {explicitPath}");
            return explicitPath;
        }

        var configuredName = Environment.GetEnvironmentVariable("PAICOM_VOSK_MODEL_NAME");
        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            foreach (var root in GetModelSearchRoots())
            {
                var configuredPath = Path.Combine(root, configuredName);
                if (Directory.Exists(configuredPath) && LooksLikeVoskModelDirectory(configuredPath))
                {
                    LogEvent($"[vosk-speech] Model selected via PAICOM_VOSK_MODEL_NAME: {configuredPath}");
                    return configuredPath;
                }
            }
        }

        foreach (var root in GetModelSearchRoots())
        {
            if (!Directory.Exists(root))
                continue;

            var modelDirs = Directory.GetDirectories(root);
            Array.Sort(modelDirs, StringComparer.OrdinalIgnoreCase);
            foreach (var modelDir in modelDirs)
            {
                if (!LooksLikeVoskModelDirectory(modelDir))
                    continue;

                LogEvent($"[vosk-speech] Auto-selected model: {modelDir}");
                return modelDir;
            }
        }

        LogEvent("[vosk-speech] No usable model found.");
        LogEvent("[vosk-speech] Place a model zip in ./VoskModels/zips and rebuild, or set PAICOM_VOSK_MODEL_PATH.");
        return null;
    }

    private static string[] GetModelSearchRoots()
    {
        var homeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".paicom", "models");
        var baseRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
        var cwdRoot = Path.Combine(Directory.GetCurrentDirectory(), "models");

        return new[] { baseRoot, cwdRoot, homeRoot };
    }

    private static bool LooksLikeVoskModelDirectory(string path)
    {
        return Directory.Exists(Path.Combine(path, "am")) &&
               Directory.Exists(Path.Combine(path, "conf")) &&
               Directory.Exists(Path.Combine(path, "graph"));
    }

    private void LogEvent(string message)
    {
        _logger?.Invoke(message);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            _voskRecognizer?.Dispose();
            _voskModel?.Dispose();
        }
        catch { }

        _disposed = true;
    }
}
