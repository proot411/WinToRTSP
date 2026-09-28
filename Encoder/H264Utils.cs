using System;
using System.Collections.Generic;

namespace WinToRTSP.Encoder;

public struct NalUnit
{
    public int Offset;
    public int Length;
    public byte NalType;
    public bool IsKeyFrame => NalType == 5;
    public bool IsSps => NalType == 7;
    public bool IsPps => NalType == 8;
}

public static class H264Utils
{
    public static List<NalUnit> SplitNalUnits(byte[] data, int length)
    {
        var nals = new List<NalUnit>();
        int i = 0;

        while (i < length - 3)
        {
            // Find start code
            int startCodeLen = 0;
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    startCodeLen = 3;
                }
                else if (i < length - 3 && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    startCodeLen = 4;
                }
            }

            if (startCodeLen > 0)
            {
                int nalStart = i + startCodeLen;
                if (nalStart >= length) break;

                byte nalType = (byte)(data[nalStart] & 0x1F);

                // Find next start code or end of buffer
                int nextStart = length;
                for (int j = nalStart; j < length - 3; j++)
                {
                    if (data[j] == 0 && data[j + 1] == 0)
                    {
                        if (data[j + 2] == 1 || (j < length - 3 && data[j + 2] == 0 && data[j + 3] == 1))
                        {
                            nextStart = j;
                            break;
                        }
                    }
                }

                nals.Add(new NalUnit
                {
                    Offset = nalStart,
                    Length = nextStart - nalStart,
                    NalType = nalType
                });

                i = nextStart;
            }
            else
            {
                i++;
            }
        }

        return nals;
    }
}
