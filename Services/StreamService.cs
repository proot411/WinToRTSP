using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using WinToRTSP.Audio;
using WinToRTSP.Capture;
using WinToRTSP.Config;
using WinToRTSP.Encoder;
using WinToRTSP.Rtsp;

namespace WinToRTSP.Services;

public class StreamStatusModel
{
    public bool IsStreaming { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;
    public int ActiveViewers { get; set; }
    public double CurrentFps { get; set; }
    public double CurrentBitrateKbps { get; set; }
    public int TargetFps { get; set; }
    public int TargetBitrateKbps { get; set; }
    public int ResolutionWidth { get; set; }
    public int ResolutionHeight { get; set; }
    public string CaptureMethod { get; set; } = "None";
    public string EncoderName { get; set; } = "None";
    public bool AudioEnabled { get; set; }
    public double CpuPercent { get; set; }
    public double RamMb { get; set; }
    public TimeSpan Uptime { get; set; }
}

public class StreamService : IDisposable
{
    private static StreamService? _instance;
    public static StreamService Instance => _instance ??= new StreamService();

    private readonly ScreenCaptureEngine _captureEngine = new();
    private readonly WasapiAudioCapture _audioCapture = new();
    private readonly RtspServer _rtspServer = new();
    private IVideoEncoder? _videoEncoder;

    private readonly object _stateLock = new();
    private DateTime? _startedAt;
    private volatile bool _isStreaming;

    // CPU monitoring
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private TimeSpan _prevCpuTime = TimeSpan.Zero;
    private DateTime _prevCpuCheck = DateTime.UtcNow;
    private double _cpuUsagePercent;

    public event Action<StreamStatusModel>? StatusChanged;

    public bool IsStreaming => _isStreaming;
    public RtspServer RtspServer => _rtspServer;

    public StreamService()
    {
        _captureEngine.FrameCaptured += OnFrameCaptured;
        _audioCapture.AudioDataAvailable += OnAudioDataAvailable;
    }

    /// <summary>
    /// Media Foundation / WASAPI / DXGI COM objects must never be created or released on the
    /// WPF STA (UI) thread: using them later from worker (MTA) threads forces COM to marshal
    /// every call through an apartment proxy, which fails fast on this machine
    /// (0xc0000409 / CFG "invalid call target" inside mfps.dll). The MTA is process-wide, so
    /// running start/stop on any MTA thread keeps every object in the same apartment as the
    /// capture/audio worker threads that use them.
    /// </summary>
    public (bool Success, string Error) StartStream()
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return RunOnMta(() => StartStreamCore());
        return StartStreamCore();
    }

    public void StopStream()
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            RunOnMta(() => { StopStreamCore(); return true; });
            return;
        }
        StopStreamCore();
    }

    private static T RunOnMta<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.IsBackground = true;
        thread.Name = "WinToRTSP-stream";
        thread.Start();
        thread.Join();
        if (error != null) throw error;
        return result;
    }

    private (bool Success, string Error) StartStreamCore()
    {
        lock (_stateLock)
        {
            if (_isStreaming) return (true, string.Empty);

            var config = ConfigManager.Current;

            // 1. Start RTSP Server
            if (!_rtspServer.Start(config.RtspPort))
            {
                string err = $"RTSP server could not bind to port {config.RtspPort}. " +
                             "Try a different port in settings (e.g. 8555).";
                Debug.WriteLine($"[SERVICE] {err}");
                return (false, err);
            }

            // 2. Start Screen Capture (DXGI → GDI → WinForms)
            if (!_captureEngine.Start(config.TargetFps, config.ResolutionScalePercent))
            {
                _rtspServer.Stop();
                string err = "All screen capture methods failed (DXGI, GDI, WinForms). " +
                             "Ensure a display is attached and the app is running in a desktop session.";
                Debug.WriteLine($"[SERVICE] {err}");
                return (false, err);
            }

            int captureW = _captureEngine.OutputWidth;
            int captureH = _captureEngine.OutputHeight;
            Debug.WriteLine($"[SERVICE] Capture started: {captureW}x{captureH} via {_captureEngine.ActiveCaptureMethod}");

            // 3. Initialize H.264 Encoder (MF → FFmpeg)
            try
            {
                _videoEncoder = EncoderFactory.CreateOptimalEncoder(
                    captureW, captureH, config.TargetFps, config.BitrateKbps);

                _videoEncoder.PacketEncoded += OnPacketEncoded;
            }
            catch (Exception ex)
            {
                _captureEngine.Stop();
                _rtspServer.Stop();
                // ex.Message already carries the detailed reason from EncoderFactory
                // (Media Foundation diagnostics + optional ffmpeg.exe fallback).
                string err = $"H.264 encoder initialization failed.\n\n{ex.Message}";
                Debug.WriteLine($"[SERVICE] {err}");
                return (false, err);
            }

            // 4. Start WASAPI Audio loopback if enabled
            if (config.EnableAudio)
            {
                if (!_audioCapture.Start())
                {
                    Debug.WriteLine("[SERVICE] Audio capture failed, continuing without audio.");
                    AppLog.Write("[SERVICE] Audio capture failed, continuing without audio.");
                }
            }

            _startedAt = DateTime.UtcNow;
            _isStreaming = true;
            _prevCpuTime = _currentProcess.TotalProcessorTime;
            _prevCpuCheck = DateTime.UtcNow;

            NotifyStatus();
            return (true, string.Empty);
        }
    }

    private void StopStreamCore()
    {
        lock (_stateLock)
        {
            if (!_isStreaming) return;

            // Close the gates first so capture/audio callbacks stop feeding us...
            _isStreaming = false;

            _audioCapture.Stop();
            // ...and join the capture thread BEFORE disposing the encoder, so EncodeFrame
            // can never run against a disposed encoder (was a use-after-dispose race).
            _captureEngine.Stop();

            if (_videoEncoder != null)
            {
                _videoEncoder.PacketEncoded -= OnPacketEncoded;
                _videoEncoder.Dispose();
                _videoEncoder = null;
            }

            _rtspServer.Stop();

            _startedAt = null;

            NotifyStatus();
        }
    }

    private void OnFrameCaptured(CapturedFrame frame)
    {
        using (frame)
        {
            if (!_isStreaming || _videoEncoder == null) return;
            try
            {
                _videoEncoder.EncodeFrame(frame);
            }
            catch (Exception ex)
            {
                // Runs on the capture thread: an escaping exception would take the process down.
                Debug.WriteLine($"[SERVICE] EncodeFrame failed: {ex.Message}");
                AppLog.Write($"[SERVICE] EncodeFrame failed: {ex}");
            }
        }
    }

    private void OnPacketEncoded(EncodedPacket packet)
    {
        try
        {
            _rtspServer.BroadcastVideoPacket(packet);
        }
        catch (Exception ex)
        {
            AppLog.Write($"[SERVICE] BroadcastVideoPacket failed: {ex}");
        }
    }

    private void OnAudioDataAvailable(object? sender, AudioBufferEventArgs e)
    {
        if (!_isStreaming) return;
        try
        {
            _rtspServer.BroadcastAudioBuffer(e.Buffer, e.BytesRecorded, e.SampleRate);
        }
        catch (Exception ex)
        {
            // Runs on the WASAPI capture callback: never let it escape into native code.
            AppLog.Write($"[SERVICE] BroadcastAudioBuffer failed: {ex}");
        }
    }

    public StreamStatusModel GetStatus()
    {
        var config = ConfigManager.Current;
        string localIp = GetPrimaryLocalIpAddress();
        string rtspUrl = $"rtsp://{localIp}:{config.RtspPort}{config.StreamPath}";
        string webUrl = $"http://{localIp}:{config.WebPort}";

        UpdateSystemMetrics();

        return new StreamStatusModel
        {
            IsStreaming = _isStreaming,
            StreamUrl = rtspUrl,
            WebUrl = webUrl,
            ActiveViewers = _rtspServer.ActiveViewerCount,
            CurrentFps = _isStreaming ? _rtspServer.CurrentFps : 0,
            CurrentBitrateKbps = _isStreaming ? _rtspServer.CurrentBitrateKbps : 0,
            TargetFps = config.TargetFps,
            TargetBitrateKbps = config.BitrateKbps,
            ResolutionWidth = _captureEngine.OutputWidth,
            ResolutionHeight = _captureEngine.OutputHeight,
            CaptureMethod = _captureEngine.ActiveCaptureMethod,
            EncoderName = _videoEncoder?.EncoderName ?? "None",
            AudioEnabled = config.EnableAudio,
            CpuPercent = _cpuUsagePercent,
            RamMb = Math.Round(_currentProcess.WorkingSet64 / (1024.0 * 1024.0), 1),
            Uptime = _startedAt.HasValue ? DateTime.UtcNow - _startedAt.Value : TimeSpan.Zero
        };
    }

    private void UpdateSystemMetrics()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _prevCpuCheck).TotalMilliseconds;
        if (elapsed >= 500)
        {
            try
            {
                var curTime = _currentProcess.TotalProcessorTime;
                var cpuUsedMs = (curTime - _prevCpuTime).TotalMilliseconds;
                int totalCores = Math.Max(1, Environment.ProcessorCount);
                _cpuUsagePercent = Math.Round((cpuUsedMs / (elapsed * totalCores)) * 100.0, 1);

                _prevCpuTime = curTime;
                _prevCpuCheck = now;
                _currentProcess.Refresh();
            }
            catch { }
        }
    }

    private void NotifyStatus()
    {
        StatusChanged?.Invoke(GetStatus());
    }

    public static string GetPrimaryLocalIpAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(addr.Address))
                        {
                            return addr.Address.ToString();
                        }
                    }
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }

    public void Dispose()
    {
        StopStream();
        _captureEngine.Dispose();
        _audioCapture.Dispose();
        _rtspServer.Dispose();
        _currentProcess.Dispose();
    }
}
