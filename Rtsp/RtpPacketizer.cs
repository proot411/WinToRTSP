using System;
using System.Collections.Generic;
using WinToRTSP.Encoder;

namespace WinToRTSP.Rtsp;

public class RtpPacketizer
{
    private const int MaxRtpPayloadSize = 1400; // MTU safe payload
    private const byte H264PayloadType = 96;
    private const byte AudioPayloadType = 97;

    private ushort _videoSeq;
    private ushort _audioSeq;
    private readonly uint _videoSsrc;
    private readonly uint _audioSsrc;

    public RtpPacketizer()
    {
        _videoSeq = (ushort)Random.Shared.Next(0, 65535);
        _audioSeq = (ushort)Random.Shared.Next(0, 65535);
        _videoSsrc = (uint)Random.Shared.Next();
        _audioSsrc = (uint)Random.Shared.Next();
    }

    public List<byte[]> PacketizeH264(byte[] nalData, int offset, int length, uint rtpTimestamp, byte channel = 0)
    {
        var packets = new List<byte[]>();
        if (length <= 0) return packets;

        byte nalHeader = nalData[offset];
        byte nalType = (byte)(nalHeader & 0x1F);

        if (length <= MaxRtpPayloadSize)
        {
            // Single NAL Unit Packet
            // 4 bytes TCP Interleaved header + 12 bytes RTP header + length
            int rtpLen = 12 + length;
            byte[] packet = new byte[4 + rtpLen];

            // TCP Interleaved frame
            packet[0] = 0x24; // '$'
            packet[1] = channel;
            packet[2] = (byte)((rtpLen >> 8) & 0xFF);
            packet[3] = (byte)(rtpLen & 0xFF);

            // RTP Header
            BuildRtpHeader(packet, 4, H264PayloadType, _videoSeq++, rtpTimestamp, _videoSsrc, marker: true);

            // Payload
            Buffer.BlockCopy(nalData, offset, packet, 16, length);
            packets.Add(packet);
        }
        else
        {
            // FU-A Fragmentation Units
            int dataOffset = offset + 1;
            int remaining = length - 1;
            bool isFirst = true;

            byte fuIndicator = (byte)((nalHeader & 0xE0) | 28); // Type 28 = FU-A

            while (remaining > 0)
            {
                int chunkSize = Math.Min(remaining, MaxRtpPayloadSize - 2);
                bool isLast = (remaining - chunkSize == 0);

                int rtpLen = 12 + 2 + chunkSize;
                byte[] packet = new byte[4 + rtpLen];

                // TCP Interleaved frame
                packet[0] = 0x24;
                packet[1] = channel;
                packet[2] = (byte)((rtpLen >> 8) & 0xFF);
                packet[3] = (byte)(rtpLen & 0xFF);

                // RTP Header (Marker bit set on the last fragment)
                BuildRtpHeader(packet, 4, H264PayloadType, _videoSeq++, rtpTimestamp, _videoSsrc, marker: isLast);

                // FU Header
                byte fuHeader = (byte)(nalType & 0x1F);
                if (isFirst) fuHeader |= 0x80; // Start bit
                if (isLast) fuHeader |= 0x40;  // End bit

                packet[16] = fuIndicator;
                packet[17] = fuHeader;

                Buffer.BlockCopy(nalData, dataOffset, packet, 18, chunkSize);

                packets.Add(packet);

                dataOffset += chunkSize;
                remaining -= chunkSize;
                isFirst = false;
            }
        }

        return packets;
    }

    public List<byte[]> PacketizeAudioL16(byte[] pcmData, int offset, int length, uint rtpTimestamp, byte channel = 2)
    {
        var packets = new List<byte[]>();
        if (length <= 0) return packets;

        int curOffset = offset;
        int remaining = length;

        while (remaining > 0)
        {
            int chunkSize = Math.Min(remaining, MaxRtpPayloadSize);
            int rtpLen = 12 + chunkSize;
            byte[] packet = new byte[4 + rtpLen];

            // TCP Interleaved header
            packet[0] = 0x24;
            packet[1] = channel;
            packet[2] = (byte)((rtpLen >> 8) & 0xFF);
            packet[3] = (byte)(rtpLen & 0xFF);

            // RTP Header
            BuildRtpHeader(packet, 4, AudioPayloadType, _audioSeq++, rtpTimestamp, _audioSsrc, marker: false);

            // Payload
            Buffer.BlockCopy(pcmData, curOffset, packet, 16, chunkSize);
            packets.Add(packet);

            curOffset += chunkSize;
            remaining -= chunkSize;
        }

        return packets;
    }

    private static void BuildRtpHeader(byte[] buffer, int offset, byte payloadType, ushort seq, uint timestamp, uint ssrc, bool marker)
    {
        buffer[offset + 0] = 0x80; // Version 2, No padding, No extension
        buffer[offset + 1] = (byte)(payloadType | (marker ? 0x80 : 0x00));
        buffer[offset + 2] = (byte)((seq >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(seq & 0xFF);
        buffer[offset + 4] = (byte)((timestamp >> 24) & 0xFF);
        buffer[offset + 5] = (byte)((timestamp >> 16) & 0xFF);
        buffer[offset + 6] = (byte)((timestamp >> 8) & 0xFF);
        buffer[offset + 7] = (byte)(timestamp & 0xFF);
        buffer[offset + 8] = (byte)((ssrc >> 24) & 0xFF);
        buffer[offset + 9] = (byte)((ssrc >> 16) & 0xFF);
        buffer[offset + 10] = (byte)((ssrc >> 8) & 0xFF);
        buffer[offset + 11] = (byte)(ssrc & 0xFF);
    }
}
