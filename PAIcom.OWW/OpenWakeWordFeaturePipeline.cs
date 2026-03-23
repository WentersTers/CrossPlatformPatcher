using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// OpenWakeWord feature pipeline for converting raw 16 kHz PCM audio into the
/// 16 x 96 feature window expected by the wake-word classifier.
/// 
/// This uses the upstream OpenWakeWord preprocessing stages:
/// - melspectrogram ONNX model
/// - embedding ONNX model
/// - rolling feature buffer
/// </summary>
public sealed class OpenWakeWordFeaturePipeline : IDisposable
{
    private const int RawChunkSize = 1280;
    private const int MelWindowSize = 76;
    private const int MelBins = 32;
    private const int FeatureWindowSize = 16;
    private const int FeatureDim = 96;
    private const int MaxMelFrames = 120;
    private const int MaxFeatureFrames = 120;

    private readonly InferenceSession _melspecSession;
    private readonly InferenceSession _embeddingSession;
    private readonly string _melspecInputName;
    private readonly string _embeddingInputName;
    private readonly List<float> _rawBuffer = new();
    private readonly List<float[]> _melBuffer = new();
    private readonly List<float[]> _featureBuffer = new();
    private readonly object _sync = new();
    private readonly Action<string>? _logger;
    private bool _loggedAudioStarted;
    private bool _loggedMelPrimed;
    private bool _loggedFeaturePrimed;
    private bool _disposed;

    public OpenWakeWordFeaturePipeline(Action<string>? logger = null)
    {
        var assembly = Assembly.GetExecutingAssembly();
        _logger = logger;

        _melspecSession = LoadSession(assembly, "oww.feature.melspectrogram.onnx");
        _embeddingSession = LoadSession(assembly, "oww.feature.embedding.onnx");

        _melspecInputName = _melspecSession.InputNames[0];
        _embeddingInputName = _embeddingSession.InputNames[0];

        logger?.Invoke($"[oww] Feature pipeline loaded: mel={_melspecInputName}, embedding={_embeddingInputName}");
    }

    public bool TryBuildFeatureWindow(float[] audioChunk, out float[,,]? featureWindow)
    {
        if (audioChunk == null || audioChunk.Length == 0)
            throw new ArgumentException("audioChunk cannot be null or empty");

        lock (_sync)
        {
            if (!_loggedAudioStarted)
            {
                _loggedAudioStarted = true;
                _logger?.Invoke("[oww] Audio stream received; warming up feature extractor");
            }

            _rawBuffer.AddRange(audioChunk);

            while (_rawBuffer.Count >= RawChunkSize)
            {
                var rawBlock = _rawBuffer.GetRange(0, RawChunkSize).ToArray();
                _rawBuffer.RemoveRange(0, RawChunkSize);

                var melFrames = ComputeMelspectrogram(rawBlock);
                foreach (var frame in melFrames)
                {
                    _melBuffer.Add(frame);
                }
                TrimToMaxLength(_melBuffer, MaxMelFrames);

                if (!_loggedMelPrimed && _melBuffer.Count >= MelWindowSize)
                {
                    _loggedMelPrimed = true;
                    _logger?.Invoke("[oww] Mel feature buffer primed (76 frames)");
                }

                if (_melBuffer.Count >= MelWindowSize)
                {
                    var melWindow = _melBuffer.Skip(_melBuffer.Count - MelWindowSize).Take(MelWindowSize).ToArray();
                    var embedding = ComputeEmbedding(melWindow);
                    _featureBuffer.Add(embedding);
                    TrimToMaxLength(_featureBuffer, MaxFeatureFrames);

                    if (!_loggedFeaturePrimed && _featureBuffer.Count >= FeatureWindowSize)
                    {
                        _loggedFeaturePrimed = true;
                        _logger?.Invoke("[oww] Wake-word feature window primed (16 x 96); classifier ready");
                    }
                }
            }

            if (_featureBuffer.Count < FeatureWindowSize)
            {
                featureWindow = default;
                return false;
            }

            featureWindow = new float[1, FeatureWindowSize, FeatureDim];
            var start = _featureBuffer.Count - FeatureWindowSize;
            for (int frame = 0; frame < FeatureWindowSize; frame++)
            {
                var source = _featureBuffer[start + frame];
                for (int feature = 0; feature < FeatureDim; feature++)
                {
                    featureWindow[0, frame, feature] = source[feature];
                }
            }

            return true;
        }
    }

    private float[][] ComputeMelspectrogram(float[] rawBlock)
    {
        var input = new DenseTensor<float>(new[] { 1, rawBlock.Length });
        for (int i = 0; i < rawBlock.Length; i++)
        {
            input[0, i] = ClampToPcmRange(rawBlock[i] * 32767.0f);
        }

        using var results = _melspecSession.Run(new[] { NamedOnnxValue.CreateFromTensor(_melspecInputName, input) });
        var tensor = results.First().AsTensor<float>();
        var mels = ToJagged2D(tensor, MelBins);

        // Match upstream openWakeWord preprocessing scaling.
        for (int frame = 0; frame < mels.Length; frame++)
        {
            var row = mels[frame];
            for (int bin = 0; bin < row.Length; bin++)
            {
                row[bin] = row[bin] / 10.0f + 2.0f;
            }
        }

        return mels;
    }

    private float[] ComputeEmbedding(float[][] melWindow)
    {
        var input = new DenseTensor<float>(new[] { 1, MelWindowSize, MelBins, 1 });

        for (int frame = 0; frame < MelWindowSize; frame++)
        {
            for (int bin = 0; bin < MelBins; bin++)
            {
                input[0, frame, bin, 0] = melWindow[frame][bin];
            }
        }

        using var results = _embeddingSession.Run(new[] { NamedOnnxValue.CreateFromTensor(_embeddingInputName, input) });
        var tensor = results.First().AsTensor<float>();
        var values = tensor.ToArray();

        if (values.Length < FeatureDim)
        {
            throw new InvalidOperationException($"Embedding model returned {values.Length} values, expected at least {FeatureDim}.");
        }

        var embedding = new float[FeatureDim];
        Array.Copy(values, embedding, FeatureDim);
        return embedding;
    }

    private static float[][] ToJagged2D(Tensor<float> tensor, int expectedColumns)
    {
        var allDims = tensor.Dimensions.ToArray();
        var dims = new List<int>();
        foreach (var dim in allDims)
        {
            if (dim > 1)
                dims.Add(dim);
        }

        if (dims.Count != 2)
        {
            throw new InvalidOperationException($"Unexpected melspectrogram output rank. Expected a 2D tensor after squeezing singleton dimensions, got [{string.Join(", ", allDims)}].");
        }

        var rows = dims[0];
        var cols = dims[1];
        if (cols != expectedColumns)
        {
            throw new InvalidOperationException($"Unexpected melspectrogram width. Expected {expectedColumns}, got {cols}.");
        }

        var values = tensor.ToArray();
        var output = new float[rows][];
        for (int row = 0; row < rows; row++)
        {
            output[row] = new float[cols];
            Array.Copy(values, row * cols, output[row], 0, cols);
        }

        return output;
    }

    private static float ClampToPcmRange(float value)
    {
        if (value > short.MaxValue)
            return short.MaxValue;

        if (value < short.MinValue)
            return short.MinValue;

        return value;
    }

    private static InferenceSession LoadSession(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Missing embedded OpenWakeWord resource: {resourceName}");

        var modelBytes = new byte[stream.Length];
        stream.Read(modelBytes, 0, modelBytes.Length);

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
        };

        return new InferenceSession(modelBytes, options);
    }

    private static void TrimToMaxLength<T>(List<T> list, int maxLength)
    {
        if (list.Count <= maxLength)
            return;

        list.RemoveRange(0, list.Count - maxLength);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _melspecSession.Dispose();
        _embeddingSession.Dispose();
        _disposed = true;
    }
}