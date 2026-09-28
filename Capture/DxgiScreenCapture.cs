using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinToRTSP.Services;

namespace WinToRTSP.Capture;

public class DxgiScreenCapture : IScreenCapture
{
    public event Action<CapturedFrame>? FrameCaptured;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;

    private Thread? _captureThread;
    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;

    private int _targetFps = 30;
    private double _scalePercent = 100.0;
    private int _screenWidth;
    private int _screenHeight;
    private int _scaledWidth;
    private int _scaledHeight;

    // Non-pooled copy of the most recent frame, re-emitted while the desktop is
    // static so viewers never starve (players treat prolonged silence as a dead
    // stream and turn black). Only touched on the capture thread.
    private byte[]? _lastFrameData;
    private int _lastFrameLength;
    private int _lastFrameWidth;
    private int _lastFrameHeight;
    private int _lastFrameStride;
    private bool _hasLastFrame;

    private long _lastErrorLogTicks;

    public bool IsRunning => _isRunning;
    public int OutputWidth => _scaledWidth;
    public int OutputHeight => _scaledHeight;
    public string CaptureMethodName => "DXGI Desktop Duplication";

    public bool Initialize(int targetFps, double scalePercent)
    {
        _targetFps = Math.Clamp(targetFps, 5, 60);
        _scalePercent = Math.Clamp(scalePercent, 25.0, 100.0);

        try
        {
            DisposeResources();

            // Create Direct3D 11 device with BGRA support for desktop duplication
            var creationFlags = DeviceCreationFlags.BgraSupport;
            var featureLevels = new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            };

            var res = D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                creationFlags,
                featureLevels,
                out _device,
                out _context);

            if (res.Failure || _device == null || _context == null)
            {
                // Fallback to WARP software renderer if hardware fails
                res = D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Warp,
                    creationFlags,
                    featureLevels,
                    out _device,
                    out _context);

                if (res.Failure || _device == null || _context == null)
                {
                    return false;
                }
            }

            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            if (dxgiDevice == null) return false;

            dxgiDevice.GetAdapter(out var adapter);
            if (adapter == null) return false;

            using (adapter)
            {
                var outRes = adapter.EnumOutputs(0, out var output);
                if (outRes.Failure || output == null) return false;

                using (output)
                {
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    if (output1 == null) return false;

                    _duplication = output1.DuplicateOutput(_device);
                    if (_duplication == null)
                    {
                        return false;
                    }

                    var desc = output.Description;
                    _screenWidth = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                    _screenHeight = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

                    // Ensure dimensions are even numbers (required by H.264 encoders)
                    _scaledWidth = ((int)(_screenWidth * (_scalePercent / 100.0))) & ~1;
                    _scaledHeight = ((int)(_screenHeight * (_scalePercent / 100.0))) & ~1;
                    if (_scaledWidth < 64) _scaledWidth = 64;
                    if (_scaledHeight < 64) _scaledHeight = 64;

                    // Create staging texture for CPU access
                    var stagingDesc = new Texture2DDescription
                    {
                        Width = (uint)_screenWidth,
                        Height = (uint)_screenHeight,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None
                    };

                    _stagingTexture = _device.CreateTexture2D(stagingDesc);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DXGI initialization failed: {ex.Message}");
            LogThrottled($"[DXGI] Initialization failed: {ex.Message}");
            DisposeResources();
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
            Name = "DxgiCaptureThread",
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
        {
            _captureThread.Join(500);
        }
        _captureThread = null;
    }

    private void CaptureLoop()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        long frameDurationTicks = Stopwatch.Frequency / _targetFps;
        var stopwatch = new Stopwatch();

        while (!token.IsCancellationRequested && _isRunning)
        {
            stopwatch.Restart();
            bool frameAcquired = false;

            try
            {
                if (_duplication == null || _context == null || _stagingTexture == null)
                {
                    // Re-initialize if lost
                    if (!Initialize(_targetFps, _scalePercent))
                    {
                        LogThrottled("[DXGI] Re-initialization failed, retrying...");
                        Thread.Sleep(100);
                        continue;
                    }
                }

                var res = _duplication!.AcquireNextFrame(100, out _, out var desktopResource);
                if (res.Success && desktopResource != null)
                {
                    frameAcquired = true;
                    using (desktopResource)
                    {
                        using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                        if (desktopTexture != null)
                        {
                            _context!.CopyResource(_stagingTexture!, desktopTexture);
                            ProcessStagingTexture();
                        }
                    }
                }
                else if (res.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                {
                    // Desktop hasn't changed (idle screen). Repeat the last frame so
                    // the stream keeps flowing; without this, viewers starve and go black.
                    EmitLastFrame();
                }
                else
                {
                    // Anything else (ACCESS_LOST, DEVICE_REMOVED after a GPU driver
                    // reset, INVALID_CALL, ...) previously fell through here silently,
                    // stalling capture forever with no trace in the log. Recreate.
                    LogThrottled($"[DXGI] AcquireNextFrame failed (0x{res.Code:X8}), reinitializing capture...");
                    DisposeResources();
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                // GPU device removal (driver reset/TDR) surfaces here. Previously this
                // was Debug-only and the loop spun forever producing nothing.
                LogThrottled($"[DXGI] Capture loop exception: {ex.Message} — reinitializing capture...");
                DisposeResources();
                Thread.Sleep(50);
            }
            finally
            {
                // A successfully acquired frame MUST be released even when processing
                // throws — otherwise every later AcquireNextFrame fails forever.
                if (frameAcquired)
                {
                    try { _duplication?.ReleaseFrame(); } catch { /* device already gone */ }
                }
            }

            // High precision frame pacing
            long elapsedTicks = stopwatch.ElapsedTicks;
            long remainingTicks = frameDurationTicks - elapsedTicks;
            if (remainingTicks > 0)
            {
                int sleepMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
                if (sleepMs > 1)
                {
                    Thread.Sleep(sleepMs - 1);
                }
                while (stopwatch.ElapsedTicks < frameDurationTicks)
                {
                    Thread.SpinWait(10);
                }
            }
        }
    }

    private unsafe void ProcessStagingTexture()
    {
        if (_context == null || _stagingTexture == null) return;

        var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* srcPtr = (byte*)mapped.DataPointer;
            int srcPitch = (int)mapped.RowPitch;

            int dstWidth = _scaledWidth;
            int dstHeight = _scaledHeight;
            int dstStride = dstWidth * 4;
            int totalBytes = dstStride * dstHeight;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(totalBytes);

            fixed (byte* dstPtr = buffer)
            {
                if (dstWidth == _screenWidth && dstHeight == _screenHeight)
                {
                    // 1:1 Scale - Row-by-row memory copy (fast zero unnecessary copies)
                    for (int y = 0; y < dstHeight; y++)
                    {
                        Buffer.MemoryCopy(
                            srcPtr + (y * srcPitch),
                            dstPtr + (y * dstStride),
                            dstStride,
                            dstStride);
                    }
                }
                else
                {
                    // High-performance bilinear downsample (50% or 75%)
                    ScaleBilinear(srcPtr, _screenWidth, _screenHeight, srcPitch, dstPtr, dstWidth, dstHeight, dstStride);
                }
            }

            // Keep a non-pooled copy for idle-time frame repetition (see EmitLastFrame).
            // MUST happen before FrameCaptured: the handler disposes the frame, which
            // returns the pooled buffer to the ArrayPool.
            if (_lastFrameData == null || _lastFrameData.Length != totalBytes)
            {
                _lastFrameData = new byte[totalBytes];
            }
            Buffer.BlockCopy(buffer, 0, _lastFrameData, 0, totalBytes);
            _lastFrameLength = totalBytes;
            _lastFrameWidth = dstWidth;
            _lastFrameHeight = dstHeight;
            _lastFrameStride = dstStride;
            _hasLastFrame = true;

            var frame = new CapturedFrame(buffer, totalBytes, dstWidth, dstHeight, dstStride, Stopwatch.GetTimestamp());
            FrameCaptured?.Invoke(frame);
        }
        finally
        {
            _context.Unmap(_stagingTexture, 0);
        }
    }

    /// <summary>
    /// Re-emits the most recent frame while the desktop is static. Uses a non-pooled
    /// cached buffer (fromPool: false) because handlers dispose the frame, which would
    /// otherwise return the buffer to the shared ArrayPool while we still reuse it.
    /// </summary>
    private void EmitLastFrame()
    {
        if (!_hasLastFrame || _lastFrameData == null) return;

        try
        {
            var frame = new CapturedFrame(
                _lastFrameData, _lastFrameLength, _lastFrameWidth, _lastFrameHeight,
                _lastFrameStride, Stopwatch.GetTimestamp(), fromPool: false);
            FrameCaptured?.Invoke(frame);
            frame.Dispose();
        }
        catch (Exception ex)
        {
            LogThrottled($"[DXGI] Failed to repeat last frame: {ex.Message}");
        }
    }

    /// <summary>Logs to the persistent app log at most once every 5 seconds.</summary>
    private void LogThrottled(string message)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _lastErrorLogTicks < Stopwatch.Frequency * 5) return;
        _lastErrorLogTicks = now;
        AppLog.Write(message);
    }

    private static unsafe void ScaleBilinear(
        byte* src, int srcW, int srcH, int srcPitch,
        byte* dst, int dstW, int dstH, int dstPitch)
    {
        float xRatio = (float)(srcW - 1) / dstW;
        float yRatio = (float)(srcH - 1) / dstH;

        for (int y = 0; y < dstH; y++)
        {
            int srcY = (int)(y * yRatio);
            float yDiff = (y * yRatio) - srcY;
            byte* srcRow1 = src + (srcY * srcPitch);
            byte* srcRow2 = srcRow1 + (srcY < srcH - 1 ? srcPitch : 0);
            byte* dstRow = dst + (y * dstPitch);

            for (int x = 0; x < dstW; x++)
            {
                int srcX = (int)(x * xRatio);
                float xDiff = (x * xRatio) - srcX;

                int x0 = srcX * 4;
                int x1 = (srcX < srcW - 1 ? srcX + 1 : srcX) * 4;

                byte* p00 = srcRow1 + x0;
                byte* p01 = srcRow1 + x1;
                byte* p10 = srcRow2 + x0;
                byte* p11 = srcRow2 + x1;

                float w00 = (1.0f - xDiff) * (1.0f - yDiff);
                float w01 = xDiff * (1.0f - yDiff);
                float w10 = (1.0f - xDiff) * yDiff;
                float w11 = xDiff * yDiff;

                // BGRA channels
                int dstIdx = x * 4;
                dstRow[dstIdx + 0] = (byte)(p00[0] * w00 + p01[0] * w01 + p10[0] * w10 + p11[0] * w11); // B
                dstRow[dstIdx + 1] = (byte)(p00[1] * w00 + p01[1] * w01 + p10[1] * w10 + p11[1] * w11); // G
                dstRow[dstIdx + 2] = (byte)(p00[2] * w00 + p01[2] * w01 + p10[2] * w10 + p11[2] * w11); // R
                dstRow[dstIdx + 3] = 255; // Alpha
            }
        }
    }

    private void DisposeResources()
    {
        _stagingTexture?.Dispose();
        _stagingTexture = null;

        _duplication?.Dispose();
        _duplication = null;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        Stop();
        DisposeResources();
        _cts?.Dispose();
    }
}
