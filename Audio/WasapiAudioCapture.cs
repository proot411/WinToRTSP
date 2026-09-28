using System;
using System.Diagnostics;
using NAudio.Wave;
using WinToRTSP.Services;

namespace WinToRTSP.Audio;

public class AudioBufferEventArgs : EventArgs
{
    public byte[] Buffer { get; }
    public int BytesRecorded { get; }
    public int SampleRate { get; }
    public int Channels { get; }

    public AudioBufferEventArgs(byte[] buffer, int bytesRecorded, int sampleRate, int channels)
    {
        Buffer = buffer;
        BytesRecorded = bytesRecorded;
        SampleRate = sampleRate;
        Channels = channels;
    }
}

public class WasapiAudioCapture : IDisposable
{
    public event EventHandler<AudioBufferEventArgs>? AudioDataAvailable;

    private WasapiLoopbackCapture? _capture;
    private bool _isRunning;
    private readonly object _lock = new();

    public bool IsRunning => _isRunning;
    public int TargetSampleRate { get; } = 48000;
    public int TargetChannels { get; } = 2;

    public bool Start()
    {
        lock (_lock)
        {
            if (_isRunning) return true;

            try
            {
                _capture = new WasapiLoopbackCapture();
                var waveFormat = _capture.WaveFormat;
                Debug.WriteLine($"[WASAPI] Device format: {waveFormat.SampleRate}Hz, {waveFormat.BitsPerSample}bit, {waveFormat.Channels}ch, {waveFormat.Encoding}");

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += (s, e) =>
                {
                    Debug.WriteLine("[WASAPI] Recording stopped.");
                    _isRunning = false;
                };

                _capture.StartRecording();
                _isRunning = true;
                AppLog.Write($"[WASAPI] audio loopback started: {waveFormat.SampleRate}Hz, {waveFormat.Channels}ch");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WASAPI] Failed to start audio loopback: {ex.Message}");
                AppLog.Write($"[WASAPI] Failed to start audio loopback: {ex.Message}");
                Stop();
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning && _capture == null) return;

            try
            {
                if (_capture != null)
                {
                    _capture.DataAvailable -= OnDataAvailable;
                    _capture.StopRecording();
                    _capture.Dispose();
                    _capture = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WASAPI] Error stopping capture: {ex.Message}");
            }
            finally
            {
                _isRunning = false;
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _capture == null) return;

        var format = _capture.WaveFormat;

        // Windows WASAPI loopback defaults to IEEE float 32-bit stereo (48000Hz or 44100Hz)
        // Convert to standard 16-bit PCM for RTP L16 packetization
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int floatCount = e.BytesRecorded / 4;
            int outByteCount = floatCount * 2;
            byte[] pcm16 = new byte[outByteCount];

            unsafe
            {
                fixed (byte* srcByte = e.Buffer)
                fixed (byte* dstByte = pcm16)
                {
                    float* src = (float*)srcByte;
                    short* dst = (short*)dstByte;

                    for (int i = 0; i < floatCount; i++)
                    {
                        float sample = Math.Clamp(src[i], -1.0f, 1.0f);
                        dst[i] = (short)(sample * 32767f);
                    }
                }
            }

            AudioDataAvailable?.Invoke(this, new AudioBufferEventArgs(pcm16, outByteCount, format.SampleRate, format.Channels));
        }
        else if (format.BitsPerSample == 16)
        {
            byte[] copy = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
            AudioDataAvailable?.Invoke(this, new AudioBufferEventArgs(copy, e.BytesRecorded, format.SampleRate, format.Channels));
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
