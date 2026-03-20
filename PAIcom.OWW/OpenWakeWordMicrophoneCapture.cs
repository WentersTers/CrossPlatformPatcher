using System;
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
    private WaveInEvent? _waveIn;
    private bool _started;

    public OpenWakeWordMicrophoneCapture(Action<float[]> onAudio, Action<string>? logger = null)
    {
        _onAudio = onAudio ?? throw new ArgumentNullException(nameof(onAudio));
        _logger = logger;
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
                BufferMilliseconds = 80,
                NumberOfBuffers = 3,
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
            _started = true;

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

        int samples = e.BytesRecorded / 2;
        if (samples <= 0)
            return;

        var chunk = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            short pcm = BitConverter.ToInt16(e.Buffer, i * 2);
            chunk[i] = pcm / 32768.0f;
        }

        _onAudio(chunk);
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
}
