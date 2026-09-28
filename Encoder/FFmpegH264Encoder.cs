using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinToRTSP.Capture;

namespace WinToRTSP.Encoder;

public class FFmpegH264Encoder : IVideoEncoder
{
    public event Action<EncodedPacket>? PacketEncoded;

    private Process? _ffmpegProcess;
    private Stream? _stdin;
    private Stream? _stdout;
    private Thread? _readerThread;
    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;

    private int _width;
    private int _height;
    private int _fps;
    private int _bitrateKbps;

    private byte[]? _sps;
    private byte[]? _pps;

    public string EncoderName => "FFmpeg libx264 (ultrafast, zerolatency)";

    public byte[]? GetSps() => _sps;
    public byte[]? GetPps() => _pps;

    public static bool IsFFmpegAvailable()
    {
        string? appDir = AppDomain.CurrentDomain.BaseDirectory;
        if (File.Exists(Path.Combine(appDir, "ffmpeg.exe")))
            return true;

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (var path in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(path.Trim(), "ffmpeg.exe")))
                        return true;
                }
                catch { }
            }
        }
        return false;
    }

    public static string FindFFmpegPath()
    {
        string? appDir = AppDomain.CurrentDomain.BaseDirectory;
        string localPath = Path.Combine(appDir, "ffmpeg.exe");
        if (File.Exists(localPath)) return localPath;

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (var path in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(path.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }
        }

        return "ffmpeg.exe";
    }

    public bool Initialize(int width, int height, int fps, int bitrateKbps)
    {
        _width = width & ~1;
        _height = height & ~1;
        _fps = Math.Clamp(fps, 5, 60);
        _bitrateKbps = Math.Clamp(bitrateKbps, 500, 25000);

        try
        {
            Dispose();

            string ffmpegExe = FindFFmpegPath();
            string args = $"-y -f rawvideo -pix_fmt bgra -s {_width}x{_height} -r {_fps} -i - " +
                          $"-c:v libx264 -preset ultrafast -tune zerolatency -b:v {_bitrateKbps}k " +
                          $"-g {_fps} -keyint_min {_fps} -sc_threshold 0 -f h264 -";

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _ffmpegProcess = Process.Start(psi);
            if (_ffmpegProcess == null) return false;

            _stdin = _ffmpegProcess.StandardInput.BaseStream;
            _stdout = _ffmpegProcess.StandardOutput.BaseStream;

            _cts = new CancellationTokenSource();
            _isRunning = true;

            _readerThread = new Thread(ReadH264Output)
            {
                Name = "FFmpegReaderThread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _readerThread.Start();

            // Background error reader for diagnostics
            Task.Run(() =>
            {
                try
                {
                    using var reader = _ffmpegProcess.StandardError;
                    while (!_ffmpegProcess.HasExited)
                    {
                        string? line = reader.ReadLine();
                        // Diagnostic logging if needed
                    }
                }
                catch { }
            });

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FFmpeg] Init failed: {ex.Message}");
            Dispose();
            return false;
        }
    }

    public void EncodeFrame(CapturedFrame frame)
    {
        if (!_isRunning || _stdin == null) return;

        try
        {
            _stdin.Write(frame.Data, 0, frame.Length);
            _stdin.Flush();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FFmpeg] Error writing frame: {ex.Message}");
        }
    }

    private void ReadH264Output()
    {
        if (_stdout == null) return;

        byte[] buffer = new byte[65536];
        var streamBuffer = new MemoryStream();

        while (_isRunning)
        {
            try
            {
                int read = _stdout.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                streamBuffer.Write(buffer, 0, read);
                byte[] rawData = streamBuffer.ToArray();

                var nals = H264Utils.SplitNalUnits(rawData, rawData.Length);
                if (nals.Count > 1)
                {
                    // Can dispatch all completed NALs except potentially partial last one
                    int lastDispatchedOffset = 0;
                    for (int i = 0; i < nals.Count - 1; i++)
                    {
                        var nal = nals[i];
                        if (nal.IsSps && _sps == null)
                        {
                            _sps = new byte[nal.Length];
                            Buffer.BlockCopy(rawData, nal.Offset, _sps, 0, nal.Length);
                        }
                        else if (nal.IsPps && _pps == null)
                        {
                            _pps = new byte[nal.Length];
                            Buffer.BlockCopy(rawData, nal.Offset, _pps, 0, nal.Length);
                        }

                        byte[] packetData = new byte[nal.Length + 4];
                        packetData[0] = 0; packetData[1] = 0; packetData[2] = 0; packetData[3] = 1;
                        Buffer.BlockCopy(rawData, nal.Offset, packetData, 4, nal.Length);

                        PacketEncoded?.Invoke(new EncodedPacket(packetData, packetData.Length, nal.IsKeyFrame, Stopwatch.GetTimestamp()));
                        lastDispatchedOffset = nal.Offset + nal.Length;
                    }

                    // Keep remaining un-dispatched bytes in buffer
                    streamBuffer.SetLength(0);
                    if (lastDispatchedOffset < rawData.Length)
                    {
                        streamBuffer.Write(rawData, lastDispatchedOffset, rawData.Length - lastDispatchedOffset);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FFmpeg] Read error: {ex.Message}");
                break;
            }
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts?.Cancel();

        try
        {
            _stdin?.Close();
            _stdout?.Close();

            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                _ffmpegProcess.Kill();
                _ffmpegProcess.WaitForExit(500);
            }
            _ffmpegProcess?.Dispose();
        }
        catch { }
        finally
        {
            _ffmpegProcess = null;
            _stdin = null;
            _stdout = null;
        }
    }
}
