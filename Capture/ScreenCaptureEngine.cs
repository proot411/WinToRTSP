using System;
using System.Diagnostics;
using WinToRTSP.Services;

namespace WinToRTSP.Capture;

public class ScreenCaptureEngine : IDisposable
{
    public event Action<CapturedFrame>? FrameCaptured;

    private IScreenCapture? _activeCapture;
    private bool _isDisposed;

    public bool IsRunning => _activeCapture?.IsRunning ?? false;
    public int OutputWidth => _activeCapture?.OutputWidth ?? 0;
    public int OutputHeight => _activeCapture?.OutputHeight ?? 0;
    public string ActiveCaptureMethod => _activeCapture?.CaptureMethodName ?? "None";

    public bool Start(int targetFps, double scalePercent)
    {
        Stop();

        // 1. Attempt DXGI Desktop Duplication (GPU accelerated)
        var dxgi = new DxgiScreenCapture();
        if (dxgi.Initialize(targetFps, scalePercent))
        {
            _activeCapture = dxgi;
            _activeCapture.FrameCaptured += OnFrameCaptured;
            _activeCapture.Start();
            Debug.WriteLine("[CAPTURE] Started using DXGI Desktop Duplication.");
            return true;
        }

        dxgi.Dispose();
        Debug.WriteLine("[CAPTURE] DXGI Desktop Duplication failed, falling back to GDI capture...");
        AppLog.Write("[CAPTURE] DXGI Desktop Duplication init failed — falling back to GDI BitBlt.");

        // 2. Fallback to GDI BitBlt capture
        var gdi = new GdiScreenCapture();
        if (gdi.Initialize(targetFps, scalePercent))
        {
            _activeCapture = gdi;
            _activeCapture.FrameCaptured += OnFrameCaptured;
            _activeCapture.Start();
            Debug.WriteLine("[CAPTURE] Started using GDI Screen Fallback.");
            return true;
        }

        gdi.Dispose();
        Debug.WriteLine("[CAPTURE] GDI capture also failed. Falling back to WinForms Screen.CopyFromScreen (VM-compatible)...");
        AppLog.Write("[CAPTURE] GDI BitBlt init failed — falling back to WinForms CopyFromScreen.");

        // 3. Last resort: WinForms Screen.CopyFromScreen (works in VMs, Hyper-V, VMware, VirtualBox)
        var winForms = new WinFormsScreenCapture();
        if (winForms.Initialize(targetFps, scalePercent))
        {
            _activeCapture = winForms;
            _activeCapture.FrameCaptured += OnFrameCaptured;
            _activeCapture.Start();
            Debug.WriteLine("[CAPTURE] Started using WinForms Screen.CopyFromScreen (VM-Compatible Mode).");
            return true;
        }

        winForms.Dispose();
        Debug.WriteLine("[CAPTURE] All capture methods failed (DXGI, GDI, WinForms).");
        AppLog.Write("[CAPTURE] All capture methods failed (DXGI, GDI, WinForms).");
        return false;
    }

    private void OnFrameCaptured(CapturedFrame frame)
    {
        FrameCaptured?.Invoke(frame);
    }

    public void Stop()
    {
        if (_activeCapture != null)
        {
            _activeCapture.FrameCaptured -= OnFrameCaptured;
            _activeCapture.Stop();
            _activeCapture.Dispose();
            _activeCapture = null;
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            Stop();
        }
    }
}
