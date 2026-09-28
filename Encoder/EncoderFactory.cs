using System;
using System.Diagnostics;

namespace WinToRTSP.Encoder;

public static class EncoderFactory
{
    public static IVideoEncoder CreateOptimalEncoder(int width, int height, int fps, int bitrateKbps)
    {
        // 1. If FFmpeg is placed in app folder or explicitly available, check if preferred
        // Otherwise, prioritize Windows Media Foundation (hardware accelerated, 0 external dependencies)
        var mfEncoder = new MediaFoundationH264Encoder();
        if (mfEncoder.Initialize(width, height, fps, bitrateKbps))
        {
            Debug.WriteLine("[ENCODER] Using Windows Media Foundation H.264 Encoder (Hardware/Low-Latency).");
            return mfEncoder;
        }

        mfEncoder.Dispose();
        string mfError = mfEncoder.LastError ?? "initialization failed";
        Debug.WriteLine($"[ENCODER] Media Foundation encoder failed ({mfError}). Checking for FFmpeg...");

        if (FFmpegH264Encoder.IsFFmpegAvailable())
        {
            var ffmpegEncoder = new FFmpegH264Encoder();
            if (ffmpegEncoder.Initialize(width, height, fps, bitrateKbps))
            {
                Debug.WriteLine("[ENCODER] Using FFmpeg libx264 (ultrafast, zerolatency).");
                return ffmpegEncoder;
            }
            ffmpegEncoder.Dispose();
            throw new PlatformNotSupportedException(
                "No H.264 encoder could be started.\n" +
                $"Windows Media Foundation: {mfError}\n" +
                "FFmpeg: found ffmpeg.exe but it failed to start.");
        }

        throw new PlatformNotSupportedException(
            "No H.264 encoder could be started.\n" +
            $"Windows Media Foundation: {mfError}\n" +
            "FFmpeg: ffmpeg.exe not found.\n\n" +
            "Fix: Place ffmpeg.exe next to WinToRTSP.exe " +
            "(https://www.gyan.dev/ffmpeg/builds/ -> ffmpeg-release-essentials.zip -> bin\\ffmpeg.exe).");
    }
}
