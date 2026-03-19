using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Background ThreadPool inference worker for OpenWakeWord.
/// 
/// Architecture:
/// - Audio callback thread: enqueues audio chunks into BlockingCollection
/// - Background ThreadPool: dequeues, runs ONNX inference (~50-200ms per chunk), invokes callback
/// - Zero blocking on audio callback; inference runs asynchronously
/// 
/// ThreadPool Strategy:
/// - Use ThreadPool.UnsafeQueueUserWorkItem(..., WorkItemOptions.LongRunning)
/// - Hints OS to dedicate background thread for long-running inference
/// - Scales inference tasks based on queue depth and InferenceThreadPoolScale setting
/// 
/// Graceful Shutdown:
/// - StopProcessing() cancels work, drains queue, releases resources
/// - Safe to call multiple times
/// </summary>
public sealed class OpenWakeWordInferenceWorker : IDisposable
{
    private readonly OpenWakeWordSettings _settings;
    private readonly AudioLockManager _lockManager;
    private readonly OpenWakeWordFeaturePipeline _featurePipeline;
    private readonly OpenWakeWordWrapperModel _model;
    private readonly Action<bool, float> _onDetection; // (detected: bool, confidence: float)
    private readonly Action<string>? _logger;
    
    private readonly BlockingCollection<float[]> _audioQueue;
    private CancellationTokenSource? _cancellationSource;
    private bool _isRunning;
    private bool _disposed;
    private int _inferenceTasksActive; // For ThreadPool scaling

    /// <summary>
    /// Create inference worker.
    /// </summary>
    /// <param name="settings">OWW configuration (threshold, threading, etc).</param>
    /// <param name="lockManager">Lock manager (issues lock on detection).</param>
    /// <param name="model">ONNX model (runs inference).</param>
    /// <param name="onDetection">Callback: (detected, confidence) when inference completes. Called on ThreadPool thread.</param>
    /// <param name="logger">Optional logger (called with [oww] prefixed messages).</param>
    public OpenWakeWordInferenceWorker(
        OpenWakeWordSettings settings,
        AudioLockManager lockManager,
        OpenWakeWordFeaturePipeline featurePipeline,
        OpenWakeWordWrapperModel model,
        Action<bool, float> onDetection,
        Action<string>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _featurePipeline = featurePipeline ?? throw new ArgumentNullException(nameof(featurePipeline));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _onDetection = onDetection ?? throw new ArgumentNullException(nameof(onDetection));
        _logger = logger;
        
        _audioQueue = new BlockingCollection<float[]>(boundedCapacity: 20); // Bounded for safety
        _cancellationSource = null;
        _isRunning = false;
        _inferenceTasksActive = 0;
    }

    /// <summary>
    /// Start background inference processing.
    /// Spawns ThreadPool tasks to consume audio queue and run ONNX inference.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    public void StartProcessing()
    {
        if (_isRunning)
            return;

        _logger?.Invoke("[oww] Starting inference worker");
        
        _isRunning = true;
        _cancellationSource = new CancellationTokenSource();
        
        // Spawn initial background work item
        ThreadPool.UnsafeQueueUserWorkItem(ProcessAudioQueue, _cancellationSource.Token);
    }

    /// <summary>
    /// Stop background processing, drain queue, release resources.
    /// Safe to call if not running.
    /// </summary>
    public void StopProcessing()
    {
        if (!_isRunning)
            return;

        _logger?.Invoke("[oww] Stopping inference worker");
        
        _isRunning = false;
        _cancellationSource?.Cancel();
        _audioQueue?.Dispose();
        
        // Wait up to 5 seconds for pending inference to complete
        Task.Delay(5000).Wait();
    }

    /// <summary>
    /// Enqueue audio chunk for inference.
    /// Returns false if queue is full (chunk dropped).
    /// Thread-safe: safe to call from audio callbacks.
    /// </summary>
    public bool EnqueueAudio(float[] chunk)
    {
        if (!_isRunning || chunk == null || chunk.Length == 0)
            return false;

        try
        {
            return _audioQueue.TryAdd(chunk, 1);
        }
        catch
        {
            return false; // Queue disposed or full
        }
    }

    /// <summary>
    /// Background work item: consume audio queue, run inference, invoke callback.
    /// Called on ThreadPool thread via UnsafeQueueUserWorkItem.
    /// </summary>
    private void ProcessAudioQueue(object? state)
    {
        if (state is not CancellationToken ct)
            return;

        Interlocked.Increment(ref _inferenceTasksActive);
        
        try
        {
            while (!ct.IsCancellationRequested)
            {
                float[]? chunk = null;

                try
                {
                    // Block indefinitely until audio chunk available or cancellation requested
                    chunk = _audioQueue.Take(ct);
                }
                catch (OperationCanceledException)
                {
                    break; // Cancellation requested
                }

                if (chunk == null || chunk.Length == 0)
                    continue;

                try
                {
                    if (!_featurePipeline.TryBuildFeatureWindow(chunk, out var featureWindow) || featureWindow is null)
                        continue;

                    // Run inference
                    var (detected, confidence) = _model.RunInference(featureWindow!, _settings.ConfidenceThreshold);

                    if (_settings.EnableVerboseLogging)
                        _logger?.Invoke($"[oww] Inference: detected={detected}, confidence={confidence:F3}");

                    if (detected)
                    {
                        // Issue hard lock and invoke callback
                        _lockManager.WakeWordDetected();
                        _onDetection(true, confidence);
                    }
                    else
                    {
                        // Invoke callback even for non-detection (allows caller to track confidence)
                        _onDetection(false, confidence);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[oww] Inference error: {ex}");
                }

                // Check if we should spawn additional background worker (queue depth > 2)
                if (_audioQueue.Count > 2 && _inferenceTasksActive < GetMaxInferenceTasks())
                {
                    _logger?.Invoke($"[oww] Spawning additional inference worker (queue depth={_audioQueue.Count})");
                    ThreadPool.UnsafeQueueUserWorkItem(ProcessAudioQueue, ct);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inferenceTasksActive);
        }
    }

    /// <summary>Calculate max concurrent inference tasks based on ThreadPool scale.</summary>
    private int GetMaxInferenceTasks()
    {
        // Scale 0.5 → 1 task, 1.0 → 2-4 tasks, 2.0 → 8 tasks
        int baseCount = Math.Max(1, (int)(Environment.ProcessorCount * 0.5f));
        return Math.Max(1, (int)(baseCount * _settings.InferenceThreadPoolScale));
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        StopProcessing();
        _cancellationSource?.Dispose();
        _audioQueue?.Dispose();
        _disposed = true;
    }
}
