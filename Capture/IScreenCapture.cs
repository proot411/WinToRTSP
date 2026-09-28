using System;

namespace WinToRTSP.Capture;

public interface IScreenCapture : IDisposable
{
    event Action<CapturedFrame>? FrameCaptured;
    bool Initialize(int targetFps, double scalePercent);
    void Start();
    void Stop();
    bool IsRunning { get; }
    int OutputWidth { get; }
    int OutputHeight { get; }
    string CaptureMethodName { get; }
}
