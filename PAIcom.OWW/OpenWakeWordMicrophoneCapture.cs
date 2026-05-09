using System;
using System.Threading;
using NAudio.Wave;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Direct microphone capture fallback for OpenWakeWord.
///
/// This bypasses obfuscated game callback hooks and streams 16 kHz mono PCM
/// directly into the OWW inference queue.
/// </summary>
public sealed class OpenWakeWordMicrophoneCapture : IDisposable
{
    private readonly Action<float[]> _onAudio;
    private readonly Action<string>? _logger;
    private readonly int _bufferMilliseconds;
    private WaveInEvent? _waveIn;
    private bool _started;
    private int _dataAvailableEventCount;
    private long _lastDataAvailableUtcTicks;

    public OpenWakeWordMicrophoneCapture(Action<float[]> onAudio, Action<string>? logger = null, int bufferMilliseconds = 200)
    {
        _onAudio = onAudio ?? throw new ArgumentNullException(nameof(onAudio));
        _logger = logger;
        _bufferMilliseconds = bufferMilliseconds;
    }

    public bool Start()
    {
        if (_started)
            return true;

        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = _bufferMilliseconds,
                NumberOfBuffers = 3,
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
            _started = true;
            _dataAvailableEventCount = 0;
            Interlocked.Exchange(ref _lastDataAvailableUtcTicks, 0);

            _logger?.Invoke("[oww] Direct microphone capture started (NAudio WaveInEvent)");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[oww] Direct microphone capture failed to start: {ex.GetType().Name}: {ex.Message}");
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        if (_waveIn != null)
        {
            try
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.RecordingStopped -= OnRecordingStopped;
                if (_started)
                    _waveIn.StopRecording();
            }
            catch
            {
                // best-effort cleanup
            }

            _waveIn.Dispose();
            _waveIn = null;
        }

        _started = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;

        int callbackCount = Interlocked.Increment(ref _dataAvailableEventCount);
        Interlocked.Exchange(ref _lastDataAvailableUtcTicks, DateTime.UtcNow.Ticks);
        if (callbackCount <= 3 || callbackCount % 100 == 0)
        {
            _logger?.Invoke($"[oww-mic-capture] DataAvailable callback #{callbackCount}, bytes={e.BytesRecorded}");
        }

        int samples = e.BytesRecorded / 2;
        if (samples <= 0)
            return;

        var chunk = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            short pcm = BitConverter.ToInt16(e.Buffer, i * 2);
            chunk[i] = pcm / 32768.0f;
        }

        try
        {
            _onAudio(chunk);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[oww-mic-capture] ERROR in onAudio callback: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _logger?.Invoke($"[oww] Direct microphone capture stopped with error: {e.Exception}");
        }
        else
        {
            _logger?.Invoke("[oww] Direct microphone capture stopped");
        }
    }

    public void Dispose()
    {
        Stop();
    }

    public bool IsStarted => _started;

    public int DataAvailableEventCount => Volatile.Read(ref _dataAvailableEventCount);

    public DateTime? LastDataAvailableUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastDataAvailableUtcTicks);
            return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }
    }
}
