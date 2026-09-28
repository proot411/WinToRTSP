using System;

namespace WinToRTSP.Encoder;

public class EncodedPacket
{
    public byte[] Data { get; }
    public int Length { get; }
    public bool IsKeyFrame { get; }
    public long TimestampUs { get; }

    public EncodedPacket(byte[] data, int length, bool isKeyFrame, long timestampUs)
    {
        Data = data;
        Length = length;
        IsKeyFrame = isKeyFrame;
        TimestampUs = timestampUs;
    }
}

public interface IVideoEncoder : IDisposable
{
    event Action<EncodedPacket>? PacketEncoded;
    bool Initialize(int width, int height, int fps, int bitrateKbps);
    void EncodeFrame(Capture.CapturedFrame frame);
    byte[]? GetSps();
    byte[]? GetPps();
    string EncoderName { get; }
}
