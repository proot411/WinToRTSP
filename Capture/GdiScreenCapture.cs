using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinToRTSP.Capture;

public class GdiScreenCapture : IScreenCapture
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

    private IntPtr _hDesktopDC = IntPtr.Zero;
    private IntPtr _hMemDC = IntPtr.Zero;
    private IntPtr _hBitmap = IntPtr.Zero;
    private IntPtr _hOldBitmap = IntPtr.Zero;
    private IntPtr _pDIBBits = IntPtr.Zero;

    public bool IsRunning => _isRunning;
    public int OutputWidth => _scaledWidth;
    public int OutputHeight => _scaledHeight;
    public string CaptureMethodName => "GDI Screen Fallback";

    public bool Initialize(int targetFps, double scalePercent)
    {
        _targetFps = Math.Clamp(targetFps, 5, 60);
        _scalePercent = Math.Clamp(scalePercent, 25.0, 100.0);

        DisposeGdi();

        try
        {
            _screenWidth = GetSystemMetrics(SM_CXSCREEN);
            _screenHeight = GetSystemMetrics(SM_CYSCREEN);

            if (_screenWidth <= 0 || _screenHeight <= 0)
            {
                _screenWidth = 1920;
                _screenHeight = 1080;
            }

            _scaledWidth = ((int)(_screenWidth * (_scalePercent / 100.0))) & ~1;
            _scaledHeight = ((int)(_screenHeight * (_scalePercent / 100.0))) & ~1;
            if (_scaledWidth < 64) _scaledWidth = 64;
            if (_scaledHeight < 64) _scaledHeight = 64;

            _hDesktopDC = GetDC(IntPtr.Zero);
            _hMemDC = CreateCompatibleDC(_hDesktopDC);

            BITMAPINFO bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = _screenWidth;
            bmi.bmiHeader.biHeight = -_screenHeight; // Top-down DIB
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;

            _hBitmap = CreateDIBSection(_hMemDC, ref bmi, DIB_RGB_COLORS, out _pDIBBits, IntPtr.Zero, 0);
            if (_hBitmap == IntPtr.Zero || _pDIBBits == IntPtr.Zero)
            {
                DisposeGdi();
                return false;
            }

            _hOldBitmap = SelectObject(_hMemDC, _hBitmap);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GDI] Initialization failed: {ex.Message}");
            DisposeGdi();
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
            Name = "GdiCaptureThread",
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

    private unsafe void CaptureLoop()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        long frameDurationTicks = Stopwatch.Frequency / _targetFps;
        var stopwatch = new Stopwatch();

        int srcPitch = _screenWidth * 4;
        int dstWidth = _scaledWidth;
        int dstHeight = _scaledHeight;
        int dstStride = dstWidth * 4;
        int totalBytes = dstStride * dstHeight;

        while (!token.IsCancellationRequested && _isRunning)
        {
            stopwatch.Restart();

            if (_hMemDC != IntPtr.Zero && _hDesktopDC != IntPtr.Zero && _pDIBBits != IntPtr.Zero)
            {
                // BitBlt from screen DC to DIB section
                BitBlt(_hMemDC, 0, 0, _screenWidth, _screenHeight, _hDesktopDC, 0, 0, SRCCOPY | CAPTUREBLT);

                byte* srcPtr = (byte*)_pDIBBits;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(totalBytes);

                fixed (byte* dstPtr = buffer)
                {
                    if (dstWidth == _screenWidth && dstHeight == _screenHeight)
                    {
                        Buffer.MemoryCopy(srcPtr, dstPtr, totalBytes, totalBytes);
                    }
                    else
                    {
                        ScaleBilinear(srcPtr, _screenWidth, _screenHeight, srcPitch, dstPtr, dstWidth, dstHeight, dstStride);
                    }
                }

                var frame = new CapturedFrame(buffer, totalBytes, dstWidth, dstHeight, dstStride, Stopwatch.GetTimestamp());
                FrameCaptured?.Invoke(frame);
            }

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

                int dstIdx = x * 4;
                dstRow[dstIdx + 0] = (byte)(p00[0] * w00 + p01[0] * w01 + p10[0] * w10 + p11[0] * w11);
                dstRow[dstIdx + 1] = (byte)(p00[1] * w00 + p01[1] * w01 + p10[1] * w10 + p11[1] * w11);
                dstRow[dstIdx + 2] = (byte)(p00[2] * w00 + p01[2] * w01 + p10[2] * w10 + p11[2] * w11);
                dstRow[dstIdx + 3] = 255;
            }
        }
    }

    private void DisposeGdi()
    {
        if (_hOldBitmap != IntPtr.Zero && _hMemDC != IntPtr.Zero)
        {
            SelectObject(_hMemDC, _hOldBitmap);
            _hOldBitmap = IntPtr.Zero;
        }

        if (_hBitmap != IntPtr.Zero)
        {
            DeleteObject(_hBitmap);
            _hBitmap = IntPtr.Zero;
        }

        if (_hMemDC != IntPtr.Zero)
        {
            DeleteDC(_hMemDC);
            _hMemDC = IntPtr.Zero;
        }

        if (_hDesktopDC != IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, _hDesktopDC);
            _hDesktopDC = IntPtr.Zero;
        }
        _pDIBBits = IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();
        DisposeGdi();
        _cts?.Dispose();
    }

    // P/Invoke definitions
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int BI_RGB = 0;
    private const int DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        [In] ref BITMAPINFO pbmi,
        uint pila,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);
}
