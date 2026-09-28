using System;
using System.Buffers;

namespace WinToRTSP.Capture;

public sealed class CapturedFrame : IDisposable
{
    public byte[] Data { get; }
    public int Length { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public long TimestampTicks { get; }

    private readonly bool _fromPool;
    private bool _disposed;

    public CapturedFrame(byte[] data, int length, int width, int height, int stride, long timestampTicks, bool fromPool = true)
    {
        Data = data;
        Length = length;
        Width = width;
        Height = height;
        Stride = stride;
        TimestampTicks = timestampTicks;
        _fromPool = fromPool;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_fromPool && Data != null)
            {
                ArrayPool<byte>.Shared.Return(Data);
            }
        }
    }
}
