using System;
using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WinToRTSP.Capture;

/// <summary>
/// Most compatible screen capture fallback using WinForms Screen.CopyFromScreen.
/// Works reliably in Hyper-V, VMware, VirtualBox, and WARP environments
/// where DXGI Desktop Duplication and raw GDI BitBlt both fail.
/// </summary>
public class WinFormsScreenCapture : IScreenCapture
{
    public event Action<CapturedFrame>? FrameCaptured;

    private Thread? _captureThread;
    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;

    private int _targetFps = 30;
    private double _scalePercent = 100.0;
    private int _screenWidth;
    private int _screenHeight;
    private int _scaledWidth;
    private int _scaledHeight;

    public bool IsRunning => _isRunning;
    public int OutputWidth => _scaledWidth;
    public int OutputHeight => _scaledHeight;
    public string CaptureMethodName => "WinForms Screen.CopyFromScreen (VM-Compatible)";

    public bool Initialize(int targetFps, double scalePercent)
    {
        _targetFps = Math.Clamp(targetFps, 5, 60);
        _scalePercent = Math.Clamp(scalePercent, 25.0, 100.0);

        try
        {
            // Get screen size from WinForms (most reliable cross-environment method)
            var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            _screenWidth = bounds.Width;
            _screenHeight = bounds.Height;

            if (_screenWidth <= 0) _screenWidth = 1920;
            if (_screenHeight <= 0) _screenHeight = 1080;

            _scaledWidth = ((int)(_screenWidth * (_scalePercent / 100.0))) & ~1;
            _scaledHeight = ((int)(_screenHeight * (_scalePercent / 100.0))) & ~1;
            if (_scaledWidth < 64) _scaledWidth = 64;
            if (_scaledHeight < 64) _scaledHeight = 64;

            Debug.WriteLine($"[WinForms Capture] Screen: {_screenWidth}x{_screenHeight}, Output: {_scaledWidth}x{_scaledHeight}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WinForms Capture] Init failed: {ex.Message}");
            return false;
        }
    }

    public void Start()
    {
        if (_isRunning) return;
        _cts = new CancellationTokenSource();
        _isRunning = true;

        _captureThread = new Thread(CaptureLoop)
        {
            Name = "WinFormsCaptureThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _captureThread.Start();
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _cts?.Cancel();
        if (_captureThread != null && _captureThread.IsAlive)
            _captureThread.Join(600);
        _captureThread = null;
    }

    private unsafe void CaptureLoop()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        long frameDurationTicks = Stopwatch.Frequency / _targetFps;
        var stopwatch = new Stopwatch();

        int dstStride = _scaledWidth * 4;
        int totalBytes = dstStride * _scaledHeight;

        while (!token.IsCancellationRequested && _isRunning)
        {
            stopwatch.Restart();
            try
            {
                CaptureOneFrame(totalBytes, dstStride);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WinForms Capture] Frame error: {ex.Message}");
                Thread.Sleep(50);
            }

            // Frame pacing
            long remainingTicks = frameDurationTicks - stopwatch.ElapsedTicks;
            if (remainingTicks > 0)
            {
                int sleepMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
                if (sleepMs > 1) Thread.Sleep(sleepMs - 1);
                while (stopwatch.ElapsedTicks < frameDurationTicks) Thread.SpinWait(10);
            }
        }
    }

    private unsafe void CaptureOneFrame(int totalBytes, int dstStride)
    {
        using var fullBitmap = new Bitmap(_screenWidth, _screenHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(fullBitmap);

        g.CopyFromScreen(0, 0, 0, 0, new Size(_screenWidth, _screenHeight), CopyPixelOperation.SourceCopy);

        // If scaling needed, resize
        Bitmap srcBitmap = fullBitmap;
        bool needsDispose = false;
        if (_scaledWidth != _screenWidth || _scaledHeight != _screenHeight)
        {
            srcBitmap = new Bitmap(_scaledWidth, _scaledHeight, PixelFormat.Format32bppArgb);
            using var gScale = Graphics.FromImage(srcBitmap);
            gScale.InterpolationMode = InterpolationMode.Bilinear;
            gScale.DrawImage(fullBitmap, 0, 0, _scaledWidth, _scaledHeight);
            needsDispose = true;
        }

        try
        {
            var bmpData = srcBitmap.LockBits(
                new Rectangle(0, 0, _scaledWidth, _scaledHeight),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(totalBytes);
                try
                {
                    // Copy from locked bitmap into pooled buffer
                    byte* src = (byte*)bmpData.Scan0;
                    int srcStride = bmpData.Stride;

                    fixed (byte* dst = buffer)
                    {
                        if (srcStride == dstStride)
                        {
                            Buffer.MemoryCopy(src, dst, totalBytes, totalBytes);
                        }
                        else
                        {
                            for (int y = 0; y < _scaledHeight; y++)
                            {
                                Buffer.MemoryCopy(
                                    src + (y * srcStride),
                                    dst + (y * dstStride),
                                    dstStride,
                                    dstStride);
                            }
                        }
                    }

                    // Note: WinForms Bitmap gives BGRA already - same as DXGI output
                    var frame = new CapturedFrame(buffer, totalBytes, _scaledWidth, _scaledHeight, dstStride, Stopwatch.GetTimestamp());
                    FrameCaptured?.Invoke(frame);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }
            }
            finally
            {
                srcBitmap.UnlockBits(bmpData);
            }
        }
        finally
        {
            if (needsDispose) srcBitmap.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
