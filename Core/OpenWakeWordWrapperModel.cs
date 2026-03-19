using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// ONNX Runtime wrapper for OpenWakeWord model inference.
/// 
/// Lazy-loads the hey_pie_com.quant.onnx model from embedded resources.
/// Provides thread-safe inference on precomputed OpenWakeWord feature windows:
/// RunInference(float[,,] features, threshold) → (bool detected, float confidence).
/// 
/// Model Details:
/// - Input: FloatTensor shape [1, 16, 96] (OpenWakeWord feature window)
/// - Output: FloatTensor shape [1] (confidence score 0.0-1.0)
/// - Inference: ~50-200ms per 1024-sample chunk on standard hardware
/// 
/// ONNX Runtime handles platform-specific native libraries (libonnxruntime.so etc).
/// External caller must ensure native libraries are extracted before use.
/// </summary>
public sealed class OpenWakeWordWrapperModel : IDisposable
{
    private static volatile OpenWakeWordWrapperModel? _instance;
    private static readonly object _lockObj = new();
    
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private bool _disposed;

    /// <summary>
    /// Private constructor. Use GetOrCreateSession() for lazy singleton access.
    /// </summary>
    private OpenWakeWordWrapperModel(byte[] modelBytes)
    {
        try
        {
            // Create session with model bytes (no file I/O)
            var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = 1, // Single thread; ThreadPool inference orchestrates background work
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
            };

            _session = new InferenceSession(modelBytes, sessionOptions);
            
            // Get input/output names from model metadata
            _inputName = _session.InputNames[0]; // Usually "input" or similar
            _outputName = _session.OutputNames[0]; // Usually "output" or "scores"
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to create ONNX InferenceSession. Ensure ONNX Runtime native libraries are available.", ex);
        }
    }

    /// <summary>
    /// Get or create lazy-singleton instance.
    /// Loads model from embedded resource on first call.
    /// Thread-safe: double-checked locking.
    /// 
    /// Throws if model not found or ONNX Runtime unavailable.
    /// </summary>
    public static OpenWakeWordWrapperModel GetOrCreateSession(
        string modelResourceName = "oww.model.hey_pie_com.quant.onnx",
        Action<string>? logger = null)
    {
        if (_instance != null)
            return _instance;

        lock (_lockObj)
        {
            if (_instance != null)
                return _instance;

            logger?.Invoke($"[oww] Loading ONNX model from resource: {modelResourceName}");
            
            // Search all loaded assemblies for the embedded model resource
            // (It may be embedded in the entry application, not this dll)
            Stream? stream = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                stream = asm.GetManifestResourceStream(modelResourceName);
                if (stream != null) break;
            }
            
            if (stream == null)
                throw new FileNotFoundException($"Model resource not found: {modelResourceName}");

            var modelBytes = new byte[stream.Length];
            stream.Read(modelBytes, 0, modelBytes.Length);
            stream.Dispose();
            
            logger?.Invoke($"[oww] Model loaded, size={modelBytes.Length / (1024.0 * 1024.0):F2} MB");
            
            _instance = new(modelBytes);
            return _instance;
        }
    }

    /// <summary>
    /// Run ONNX inference on audio chunk.
    /// 
    /// Input:
    ///   featureWindow: OpenWakeWord feature tensor shaped [1, 16, 96]
    ///   threshold: confidence threshold [0.0, 1.0]
    /// 
    /// Returns:
    ///   (detected: true if confidence >= threshold, confidence: raw model output)
    /// 
    /// Thread-safe: ONNX Runtime calls are serialized.
    /// Inference time: ~50-200ms per 1024-sample chunk (OS-dependent).
    /// 
    /// Throws if inference fails.
    /// </summary>
    public (bool detected, float confidence) RunInference(float[,,] featureWindow, float threshold)
    {
        if (featureWindow == null)
            throw new ArgumentNullException(nameof(featureWindow));

        if (featureWindow.GetLength(0) != 1 || featureWindow.GetLength(1) != 16 || featureWindow.GetLength(2) != 96)
        {
            throw new ArgumentException(
                $"featureWindow must have shape [1, 16, 96], got [{featureWindow.GetLength(0)}, {featureWindow.GetLength(1)}, {featureWindow.GetLength(2)}]",
                nameof(featureWindow));
        }
        
        if (threshold < 0.0f || threshold > 1.0f)
            throw new ArgumentException($"threshold must be [0.0, 1.0], got {threshold}");

        lock (_lockObj)
        {
            try
            {
                var inputTensor = new DenseTensor<float>(new[] { 1, 16, 96 });
                for (int frame = 0; frame < 16; frame++)
                {
                    for (int feature = 0; feature < 96; feature++)
                    {
                        inputTensor[0, frame, feature] = featureWindow[0, frame, feature];
                    }
                }

                // Run inference
                var inputValues = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, inputTensor) };

                using var results = _session.Run(inputValues);
                
                // Extract confidence score from output tensor
                var outputTensor = results.First().Value as Tensor<float>;
                if (outputTensor == null || outputTensor.Length == 0)
                    throw new InvalidOperationException("Invalid ONNX output tensor");

                float confidence = outputTensor.ToArray()[0];

                // Clamp to [0.0, 1.0] in case of model quirks
                confidence = System.Math.Max(0.0f, System.Math.Min(1.0f, confidence));
                
                bool detected = confidence >= threshold;
                return (detected, confidence);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("ONNX inference failed", ex);
            }
        }
    }

    /// <summary>
    /// Reset singleton (for testing / re-initialization).
    /// </summary>
    internal static void ResetSingleton()
    {
        lock (_lockObj)
        {
            _instance?.Dispose();
            _instance = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _session?.Dispose();
        _disposed = true;
    }
}
