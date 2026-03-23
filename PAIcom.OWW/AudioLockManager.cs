using System;
using System.Collections.Generic;
using System.Threading;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Thread-safe lock manager for OpenWakeWord detection.
/// 
/// Prevents duplicate detections within a hard lock window (default 3 seconds).
/// Maintains a bounded audio queue for processing during lock period.
/// 
/// Thread Safety:
/// - ReaderWriterLockSlim: Many readers (audio callbacks), few writers (lock state)
/// - IsLocked is a fast non-blocking read (lock state check)
/// - TryEnqueueAudio is a fast non-blocking write (queue management)
/// 
/// Hard Lock Behavior:
/// 1. Wake word detected → lock issued → IsLocked = true for 3000ms
/// 2. During lock: incoming audio queued, inference paused
/// 3. After lock expires: audio queue processed, inference resumes
/// 
/// Queue Bounded: Max 10 chunks (~1.3 MB @ 16-bit stereo int16 per chunk = 130KB/chunk)
/// </summary>
public sealed class AudioLockManager : IDisposable
{
    private readonly int _lockDurationMs;
    private readonly int _maxQueuedChunks;
    private readonly ReaderWriterLockSlim _lockSlim;
    private readonly Queue<float[]> _audioQueue;
    private DateTime _lockExpireTime;
    private bool _disposed;

    /// <summary>
    /// Create lock manager.
    /// </summary>
    /// <param name="lockDurationMs">Hard lock duration in milliseconds.</param>
    /// <param name="maxQueuedChunks">Maximum audio chunks to queue (default 10).</param>
    public AudioLockManager(int lockDurationMs, int maxQueuedChunks = 10)
    {
        if (lockDurationMs < 100 || lockDurationMs > 10000)
            throw new ArgumentException($"lockDurationMs must be [100,10000], got {lockDurationMs}");
        
        if (maxQueuedChunks < 1 || maxQueuedChunks > 100)
            throw new ArgumentException($"maxQueuedChunks must be [1,100], got {maxQueuedChunks}");

        _lockDurationMs = lockDurationMs;
        _maxQueuedChunks = maxQueuedChunks;
        _lockSlim = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        _audioQueue = new();
        _lockExpireTime = DateTime.MinValue; // Not locked initially
    }

    /// <summary>Fast check: is lock active? Non-blocking read.</summary>
    public bool IsLocked
    {
        get
        {
            _lockSlim.EnterReadLock();
            try
            {
                return DateTime.UtcNow < _lockExpireTime;
            }
            finally
            {
                _lockSlim.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Record a wake word detection, issue hard lock.
    /// Call this when detection confidence passes threshold.
    /// </summary>
    public void WakeWordDetected()
    {
        _lockSlim.EnterWriteLock();
        try
        {
            _lockExpireTime = DateTime.UtcNow.AddMilliseconds(_lockDurationMs);
            // Keep audio queue; drain in background processor
        }
        finally
        {
            _lockSlim.ExitWriteLock();
        }
    }

    /// <summary>
    /// Try enqueue audio chunk for later processing.
    /// Returns false if queue is full (dropped chunk).
    /// Thread-safe: safe to call from audio callbacks.
    /// </summary>
    public bool TryEnqueueAudio(float[] chunk)
    {
        if (chunk == null || chunk.Length == 0)
            return false;

        _lockSlim.EnterWriteLock();
        try
        {
            if (_audioQueue.Count >= _maxQueuedChunks)
                return false; // Queue full, drop chunk
            
            _audioQueue.Enqueue(chunk);
            return true;
        }
        finally
        {
            _lockSlim.ExitWriteLock();
        }
    }

    /// <summary>
    /// Try dequeue next audio chunk for inference.
    /// Returns null if queue is empty.
    /// Thread-safe: safe to call from inference worker thread.
    /// </summary>
    public float[]? TryDequeueAudio()
    {
        _lockSlim.EnterWriteLock();
        try
        {
            return _audioQueue.Count > 0 ? _audioQueue.Dequeue() : null;
        }
        finally
        {
            _lockSlim.ExitWriteLock();
        }
    }

    /// <summary>Current queue depth (snapshot). For diagnostics.</summary>
    public int QueueDepth
    {
        get
        {
            _lockSlim.EnterReadLock();
            try
            {
                return _audioQueue.Count;
            }
            finally
            {
                _lockSlim.ExitReadLock();
            }
        }
    }

    /// <summary>Clear queued audio and reset lock.</summary>
    public void Reset()
    {
        _lockSlim.EnterWriteLock();
        try
        {
            _audioQueue.Clear();
            _lockExpireTime = DateTime.MinValue;
        }
        finally
        {
            _lockSlim.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _lockSlim?.Dispose();
        _disposed = true;
    }
}
