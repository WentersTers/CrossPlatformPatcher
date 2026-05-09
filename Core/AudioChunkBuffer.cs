using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Audio buffer for accumulating chunks during lock window.
/// Collects float[] chunks and merges them into larger buffers before enqueueing.
/// This ensures Vosk receives complete audio instead of small fragments.
/// </summary>
public sealed class AudioChunkBuffer
{
    private readonly List<float> _buffer = new();
    private readonly int _targetChunkSize;  // Minimum samples before flushing
    private readonly object _lock = new object();
    private readonly Action<float[]>? _onChunkReady;
    private readonly Action<string>? _logger;

    /// <summary>
    /// Create a buffer that accumulates audio chunks.
    /// When accumulated samples >= targetChunkSize, calls onChunkReady with the merged chunk.
    /// </summary>
    public AudioChunkBuffer(int targetChunkSize, Action<float[]>? onChunkReady, Action<string>? logger = null)
    {
        _targetChunkSize = Math.Max(512, targetChunkSize);  // Minimum 512 samples
        _onChunkReady = onChunkReady;
        _logger = logger;
    }

    /// <summary>
    /// Add a chunk to the buffer.
    /// If accumulated size >= target, flushes a merged chunk immediately.
    /// </summary>
    public void AddChunk(float[] chunk)
    {
        if (chunk == null || chunk.Length == 0)
            return;

        lock (_lock)
        {
            _buffer.AddRange(chunk);

            // If we have enough samples, flush a chunk
            while (_buffer.Count >= _targetChunkSize)
            {
                var mergedChunk = new float[_targetChunkSize];
                for (int i = 0; i < _targetChunkSize; i++)
                    mergedChunk[i] = _buffer[i];

                _buffer.RemoveRange(0, _targetChunkSize);

                try
                {
                    _onChunkReady?.Invoke(mergedChunk);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[oww-buffer] Error in onChunkReady: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Flush any remaining buffered audio as a final chunk.
    /// Call this when done collecting audio (e.g., lock window end).
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_buffer.Count > 0)
            {
                var finalChunk = _buffer.ToArray();
                _buffer.Clear();

                try
                {
                    _onChunkReady?.Invoke(finalChunk);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[oww-buffer] Error in flush: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Clear all buffered audio without flushing.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }

    /// <summary>Get current buffer size in samples.</summary>
    public int BufferSize
    {
        get
        {
            lock (_lock)
            {
                return _buffer.Count;
            }
        }
    }
}
