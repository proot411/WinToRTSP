using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinToRTSP.Capture;

namespace WinToRTSP.Encoder;

public class MediaFoundationH264Encoder : IVideoEncoder
{
    public event Action<EncodedPacket>? PacketEncoded;

    private int _width;
    private int _height;
    private int _fps;
    private int _bitrateKbps;

    private byte[]? _sps;
    private byte[]? _pps;

    private bool _initialized;
    private bool _mfStarted;
    private long _frameIndex;
    private IMFTransform? _mft;

    /// <summary>Human readable reason for the last failed Initialize() call.</summary>
    public string? LastError { get; private set; }

    // Media Foundation COM & GUID definitions.
    // NOTE: every GUID below was verified against the Windows SDK headers (mfapi.h /
    // mfobjects.h / mftransform.h / codecapi.h). The originals were wrong (they kept the
    // first DWORD but fabricated the rest), which made every media type lookup and every
    // COM QueryInterface fail with E_INVALIDARG / E_NOINTERFACE.

    // Windows registers the H.264 encoder MFT under different CLSIDs depending on the
    // OS build. Hardcoding a single one breaks on systems where that CLSID is missing
    // (0x80040154 REGDB_E_CLASSNOTREG), so every known candidate is probed instead.
    private static readonly Guid CLSID_H264EncoderMft = new("6ca50344-051a-4ded-9779-a43305165e35"); // mfh264enc.dll
    private static readonly Guid CLSID_CMSH264EncoderMFT = new("6ca510ac-b229-4768-bd61-1750157e3c09");
    private static readonly Guid[] CandidateClsIds = { CLSID_H264EncoderMft, CLSID_CMSH264EncoderMFT };

    // MFT_CATEGORY_VIDEO_ENCODER (mfapi.h)
    private static readonly Guid MftCategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    // MFTEnumEx flags (mfapi.h)
    private const uint MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
    private const uint MFT_ENUM_FLAG_LOCALMFT = 0x00000010;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;
    private const uint MFT_ENUM_FLAG_ALL = 0x0000003F;
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

    private static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    private const uint MF_VERSION = 0x00020070;
    private const uint MFSTARTUP_NOSOCKET = 0x1;

    public string EncoderName => "MediaFoundation Hardware/Software H.264";

    public byte[]? GetSps() => _sps;
    public byte[]? GetPps() => _pps;

    public bool Initialize(int width, int height, int fps, int bitrateKbps)
    {
        _width = width & ~1;
        _height = height & ~1;
        _fps = Math.Clamp(fps, 5, 60);
        _bitrateKbps = Math.Clamp(bitrateKbps, 500, 25000);
        LastError = null;

        try
        {
            ReleaseMft();

            if (!_mfStarted)
            {
                int startup = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
                if (startup != 0)
                {
                    LastError = $"Media Foundation startup failed (0x{startup:X8}).";
                    Debug.WriteLine($"[MF] {LastError}");
                    return false;
                }
                _mfStarted = true;
            }

            var failures = new List<string>();

            // 1. Synchronous (V1) encoder MFTs reported by Windows — these work with the
            //    ProcessInput/ProcessOutput model used below.
            var syncMfts = EnumEncoderMfts(MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER);
            failures.Add($"[sync MFTs found: {syncMfts.Count}]");
            bool ok = TryCandidates(syncMfts, failures);

            // 2. Any other registered video encoder MFT (hardware / async).
            if (!ok)
            {
                var allMfts = EnumEncoderMfts(MFT_ENUM_FLAG_ALL | MFT_ENUM_FLAG_SORTANDFILTER);
                failures.Add($"[all MFTs found: {allMfts.Count}]");
                ok = TryCandidates(allMfts, failures);
            }

            // 3. Known CLSIDs — covers systems where MFTEnumEx returns nothing usable.
            if (!ok)
            {
                var byClsid = CreateFromClsIds(failures);
                failures.Add($"[CLSIDs created: {byClsid.Count}]");
                ok = TryCandidates(byClsid, failures);
            }

            if (!ok || _mft == null)
            {
                LastError = "No usable H.264 encoder MFT found. " +
                            (failures.Count > 0 ? string.Join(" | ", failures.Take(6)) : "No encoder MFT registered on this system.");
                Debug.WriteLine($"[MF] {LastError}");
                return false;
            }

            _initialized = true;
            _frameIndex = 0;
            Debug.WriteLine($"[MF] Encoder initialized: {_width}x{_height} @ {_fps}fps, {_bitrateKbps} kbps");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Debug.WriteLine($"[MF] Encoder initialization exception: {ex.Message}");
            ReleaseMft();
            return false;
        }
    }

    /// <summary>
    /// Tries each candidate MFT until one accepts our media types. The first success is
    /// kept in <see cref="_mft"/>; everything else is released.
    /// </summary>
    private bool TryCandidates(List<(IMFTransform Mft, string Label)> candidates, List<string> failures)
    {
        foreach (var (mft, label) in candidates)
        {
            try
            {
                if (TryConfigure(mft, out string error))
                {
                    _mft = mft;
                    Debug.WriteLine($"[MF] Using encoder MFT: {label}");
                    return true;
                }

                failures.Add($"{label}: {error}");
                Marshal.ReleaseComObject(mft);
            }
            catch (Exception ex)
            {
                failures.Add($"{label}: {ex.Message}");
                try { Marshal.ReleaseComObject(mft); } catch { }
            }
        }
        return false;
    }

    /// <summary>Configures CodecAPI options and negotiates input/output media types.</summary>
    private bool TryConfigure(IMFTransform mft, out string error)
    {
        // Configure low latency mode and GOP size (optional — not every MFT exposes CodecAPI)
        if (mft is ICodecAPI codecApi)
        {
            try
            {
                object lowLatencyVal = true;
                codecApi.SetValue(ref UnsafeAsRef(in CODECAPI_AVLowLatencyMode), ref lowLatencyVal);

                // Rate control mode: CBR (0) or VBR (2)
                object rateControlVal = (uint)0; // CBR
                codecApi.SetValue(ref UnsafeAsRef(in CODECAPI_AVEncCommonRateControlMode), ref rateControlVal);

                // GOP size (keyframe interval = 1 second)
                object gopSizeVal = (uint)_fps;
                codecApi.SetValue(ref UnsafeAsRef(in CODECAPI_AVEncMPVGOPSize), ref gopSizeVal);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MF] CodecAPI configuration warning: {ex.Message}");
            }
        }

        // 1. Set Output Media Type (H.264). Some MFTs reject the optional profile /
        //    interlace keys, so retry with a minimal type before giving up.
        int hr = SetOutputMediaType(mft, relaxed: false);
        if (hr != 0) hr = SetOutputMediaType(mft, relaxed: true);
        if (hr != 0)
        {
            error = $"output media type rejected (0x{hr:X8})";
            return false;
        }

        // 2. Set Input Media Type (NV12)
        hr = SetInputMediaType(mft, relaxed: false);
        if (hr != 0) hr = SetInputMediaType(mft, relaxed: true);
        if (hr != 0)
        {
            error = $"input media type rejected (0x{hr:X8})";
            return false;
        }

        mft.ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, IntPtr.Zero);
        mft.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        mft.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);

        error = string.Empty;
        return true;
    }

    private int SetOutputMediaType(IMFTransform mft, bool relaxed)
    {
        MFCreateMediaType(out var outputType);
        try
        {
            outputType.SetGUID(ref UnsafeAsRef(in MF_MT_MAJOR_TYPE), ref UnsafeAsRef(in MFMediaType_Video));
            outputType.SetGUID(ref UnsafeAsRef(in MF_MT_SUBTYPE), ref UnsafeAsRef(in MFVideoFormat_H264));
            outputType.SetUINT32(ref UnsafeAsRef(in MF_MT_AVG_BITRATE), (uint)(_bitrateKbps * 1000));
            outputType.SetUINT64(ref UnsafeAsRef(in MF_MT_FRAME_SIZE), PackSize((uint)_width, (uint)_height));
            outputType.SetUINT64(ref UnsafeAsRef(in MF_MT_FRAME_RATE), PackRatio((uint)_fps, 1));
            outputType.SetUINT64(ref UnsafeAsRef(in MF_MT_PIXEL_ASPECT_RATIO), PackRatio(1, 1));

            if (!relaxed)
            {
                outputType.SetUINT32(ref UnsafeAsRef(in MF_MT_INTERLACE_MODE), 2); // Progressive
                outputType.SetUINT32(ref UnsafeAsRef(in MF_MT_MPEG2_PROFILE), 66); // Baseline Profile
            }

            return mft.SetOutputType(0, outputType, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(outputType);
        }
    }

    private int SetInputMediaType(IMFTransform mft, bool relaxed)
    {
        MFCreateMediaType(out var inputType);
        try
        {
            inputType.SetGUID(ref UnsafeAsRef(in MF_MT_MAJOR_TYPE), ref UnsafeAsRef(in MFMediaType_Video));
            inputType.SetGUID(ref UnsafeAsRef(in MF_MT_SUBTYPE), ref UnsafeAsRef(in MFVideoFormat_NV12));
            inputType.SetUINT64(ref UnsafeAsRef(in MF_MT_FRAME_SIZE), PackSize((uint)_width, (uint)_height));
            inputType.SetUINT64(ref UnsafeAsRef(in MF_MT_FRAME_RATE), PackRatio((uint)_fps, 1));

            if (!relaxed)
            {
                inputType.SetUINT64(ref UnsafeAsRef(in MF_MT_PIXEL_ASPECT_RATIO), PackRatio(1, 1));
                inputType.SetUINT32(ref UnsafeAsRef(in MF_MT_INTERLACE_MODE), 2);
            }

            return mft.SetInputType(0, inputType, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(inputType);
        }
    }

    /// <summary>Enumerates video encoder MFTs registered with Media Foundation.</summary>
    private static List<(IMFTransform Mft, string Label)> EnumEncoderMfts(uint flags)
    {
        var result = new List<(IMFTransform, string)>();
        IntPtr arrayPtr = IntPtr.Zero;

        int hr = MFTEnumEx(MftCategoryVideoEncoder, flags, IntPtr.Zero, IntPtr.Zero, out arrayPtr, out uint count);
        if (hr < 0 || arrayPtr == IntPtr.Zero) return result;

        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr activatePtr = Marshal.ReadIntPtr(arrayPtr, i * IntPtr.Size);
                if (activatePtr == IntPtr.Zero) continue;

                // IMFActivate does not answer QueryInterface(IID_IMFTransform) — it returns
                // E_NOINTERFACE (0x80004002). The activation object has to be asked for the
                // transform explicitly through IMFActivate::ActivateObject.
                IntPtr mftPtr = ActivateTransform(activatePtr);
                if (mftPtr != IntPtr.Zero)
                {
                    try
                    {
                        if (Marshal.GetObjectForIUnknown(mftPtr) is IMFTransform mft)
                            result.Add((mft, $"video encoder MFT #{i}"));
                    }
                    finally
                    {
                        Marshal.Release(mftPtr);
                    }
                }

                Marshal.Release(activatePtr);
            }
        }
        finally
        {
            CoTaskMemFree(arrayPtr);
        }

        return result;
    }

    /// <summary>Calls IMFActivate::ActivateObject(IID_IMFTransform) on an activation object.</summary>
    private static IntPtr ActivateTransform(IntPtr activatePtr)
    {
        try
        {
            // Vtable: [0..2] IUnknown, [3..32] IMFAttributes, [33] ActivateObject.
            IntPtr vtable = Marshal.ReadIntPtr(activatePtr);
            IntPtr fn = Marshal.ReadIntPtr(vtable, 33 * IntPtr.Size);
            var activateObject = Marshal.GetDelegateForFunctionPointer<ActivateObjectFn>(fn);

            Guid iid = typeof(IMFTransform).GUID;
            int hr = activateObject(activatePtr, ref iid, out IntPtr mftPtr);
            if (hr >= 0 && mftPtr != IntPtr.Zero) return mftPtr;
            if (mftPtr != IntPtr.Zero) Marshal.Release(mftPtr);

            Debug.WriteLine($"[MF] IMFActivate::ActivateObject failed: 0x{hr:X8}");
            return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MF] IMFActivate::ActivateObject exception: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateObjectFn(IntPtr self, ref Guid riid, out IntPtr ppv);

    /// <summary>Instantiates the well known H.264 encoder MFT CLSIDs registered on this system.</summary>
    private List<(IMFTransform Mft, string Label)> CreateFromClsIds(List<string> failures)
    {
        var result = new List<(IMFTransform, string)>();

        foreach (var clsid in CandidateClsIds)
        {
            try
            {
                Type? mftType = Type.GetTypeFromCLSID(clsid);
                if (mftType == null)
                {
                    failures.Add($"CLSID {clsid:B}: type unavailable");
                    continue;
                }

                if (Activator.CreateInstance(mftType) is IMFTransform mft)
                    result.Add((mft, $"CLSID {clsid:B}"));
                else
                    failures.Add($"CLSID {clsid:B}: not an IMFTransform");
            }
            catch (Exception ex)
            {
                failures.Add($"CLSID {clsid:B}: {ex.Message}");
                Debug.WriteLine($"[MF] CLSID {clsid:B} not usable: {ex.Message}");
            }
        }

        return result;
    }

    public unsafe void EncodeFrame(CapturedFrame frame)
    {
        if (!_initialized || _mft == null) return;

        int nv12Size = _width * _height * 3 / 2;
        byte[] nv12Buffer = ArrayPool<byte>.Shared.Rent(nv12Size);

        try
        {
            // Convert BGRA to NV12
            fixed (byte* pBgra = frame.Data)
            fixed (byte* pNv12 = nv12Buffer)
            {
                BgraToNv12(pBgra, frame.Width, frame.Height, frame.Stride, pNv12, _width, _height);
            }

            // Create Media Foundation Sample & Media Buffer
            MFCreateMemoryBuffer((uint)nv12Size, out var mediaBuffer);
            mediaBuffer.Lock(out var pBuffer, out _, out _);
            Marshal.Copy(nv12Buffer, 0, pBuffer, nv12Size);
            mediaBuffer.Unlock();
            mediaBuffer.SetCurrentLength((uint)nv12Size);

            MFCreateSample(out var sample);
            sample.AddBuffer(mediaBuffer);

            long frameDurationHns = 10_000_000L / _fps; // 100-nanosecond units
            long frameTimeHns = _frameIndex * frameDurationHns;
            sample.SetSampleTime(frameTimeHns);
            sample.SetSampleDuration(frameDurationHns);

            int hr = _mft.ProcessInput(0, sample, 0);
            Marshal.ReleaseComObject(mediaBuffer);
            Marshal.ReleaseComObject(sample);

            if (hr == 0)
            {
                _frameIndex++;
                DrainOutput();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MF] Error encoding frame: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(nv12Buffer);
        }
    }

    private void DrainOutput()
    {
        if (_mft == null) return;

        MFT_OUTPUT_STREAM_INFO streamInfo;
        _mft.GetOutputStreamInfo(0, out streamInfo);
        uint bufferSize = streamInfo.cbSize > 0 ? streamInfo.cbSize : 1024u * 1024u;

        // MFT_OUTPUT_DATA_BUFFER layout (both x86 and x64):
        //   DWORD dwStreamID; IMFSample *pSample; DWORD dwStatus; IMFCollection *pEvents;
        int offPSample = IntPtr.Size;
        int offStatus = IntPtr.Size * 2;
        int structSize = IntPtr.Size * 4;

        while (true)
        {
            MFCreateSample(out var outSample);
            MFCreateMemoryBuffer(bufferSize, out var outBuffer);
            outSample.AddBuffer(outBuffer);

            // Reference we hand to the MFT (and get back in the buffer struct).
            IntPtr providedPtr = Marshal.GetIUnknownForObject(outSample);
            IntPtr outputBuffer = Marshal.AllocHGlobal(structSize);
            bool gotSample = false;

            try
            {
                Marshal.WriteInt32(outputBuffer, 0, 0);                    // dwStreamID
                Marshal.WriteIntPtr(outputBuffer, offPSample, providedPtr); // pSample (we provide it)
                Marshal.WriteInt32(outputBuffer, offStatus, 0);            // dwStatus
                Marshal.WriteIntPtr(outputBuffer, offStatus + IntPtr.Size, IntPtr.Zero); // pEvents

                int hr = _mft.ProcessOutput(0, 1, outputBuffer, out _);
                IntPtr resultPtr = Marshal.ReadIntPtr(outputBuffer, offPSample);

                if (hr == 0 && resultPtr != IntPtr.Zero)
                {
                    IMFSample sample = resultPtr == providedPtr
                        ? outSample
                        : (IMFSample)Marshal.GetObjectForIUnknown(resultPtr);

                    gotSample = true;
                    ProcessEncodedSample(sample);

                    // If the MFT handed back its own sample we own that reference.
                    if (resultPtr != providedPtr)
                        Marshal.Release(resultPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(outputBuffer);
                Marshal.Release(providedPtr);
                try { Marshal.ReleaseComObject(outBuffer); } catch { }
                try { Marshal.ReleaseComObject(outSample); } catch { }
            }

            if (!gotSample) break;
        }
    }

    private void ProcessEncodedSample(IMFSample sample)
    {
        sample.ConvertToContiguousBuffer(out var buffer);
        buffer.Lock(out var pData, out _, out var currentLength);

        byte[] encodedData = new byte[currentLength];
        Marshal.Copy(pData, encodedData, 0, (int)currentLength);
        buffer.Unlock();
        Marshal.ReleaseComObject(buffer);

        long sampleTimeHns = 0;
        sample.GetSampleTime(out sampleTimeHns);
        long timestampUs = sampleTimeHns / 10; // HNS to microseconds

        // Parse NALUs to discover SPS / PPS and Keyframes
        var nals = H264Utils.SplitNalUnits(encodedData, encodedData.Length);
        bool isKeyFrame = false;

        foreach (var nal in nals)
        {
            if (nal.IsSps && _sps == null)
            {
                _sps = new byte[nal.Length];
                Buffer.BlockCopy(encodedData, nal.Offset, _sps, 0, nal.Length);
                Debug.WriteLine($"[MF] Extracted SPS ({_sps.Length} bytes)");
            }
            else if (nal.IsPps && _pps == null)
            {
                _pps = new byte[nal.Length];
                Buffer.BlockCopy(encodedData, nal.Offset, _pps, 0, nal.Length);
                Debug.WriteLine($"[MF] Extracted PPS ({_pps.Length} bytes)");
            }

            if (nal.IsKeyFrame)
            {
                isKeyFrame = true;
            }
        }

        PacketEncoded?.Invoke(new EncodedPacket(encodedData, encodedData.Length, isKeyFrame, timestampUs));
    }

    private static unsafe void BgraToNv12(byte* bgra, int width, int height, int stride, byte* nv12, int dstW, int dstH)
    {
        int yPlaneSize = dstW * dstH;
        byte* yPlane = nv12;
        byte* uvPlane = nv12 + yPlaneSize;

        for (int y = 0; y < dstH; y++)
        {
            byte* srcRow = bgra + (y * stride);
            byte* dstYRow = yPlane + (y * dstW);
            byte* dstUvRow = uvPlane + ((y >> 1) * dstW);
            bool isEvenRow = (y & 1) == 0;

            for (int x = 0; x < dstW; x++)
            {
                int srcIdx = x * 4;
                int b = srcRow[srcIdx + 0];
                int g = srcRow[srcIdx + 1];
                int r = srcRow[srcIdx + 2];

                // Fast integer RGB to YCbCr conversion
                int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                dstYRow[x] = (byte)Math.Clamp(yVal, 16, 235);

                if (isEvenRow && (x & 1) == 0)
                {
                    int uVal = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    int vVal = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;

                    dstUvRow[x] = (byte)Math.Clamp(uVal, 16, 240);
                    dstUvRow[x + 1] = (byte)Math.Clamp(vVal, 16, 240);
                }
            }
        }
    }

    private static ulong PackSize(uint width, uint height) => ((ulong)width << 32) | height;
    private static ulong PackRatio(uint num, uint den) => ((ulong)num << 32) | den;
    private static ref T UnsafeAsRef<T>(in T val) => ref Unsafe.AsRef(in val);

    private void ReleaseMft()
    {
        if (_mft != null)
        {
            try
            {
                _mft.ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
                Marshal.ReleaseComObject(_mft);
            }
            catch { }
            _mft = null;
        }

        _initialized = false;
    }

    public void Dispose()
    {
        ReleaseMft();

        if (_mfStarted)
        {
            MFShutdown();
            _mfStarted = false;
        }
    }

    // Media Foundation P/Invoke and COM interfaces
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint Version, uint dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(out IMFSample ppIMFSample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(
        Guid guidCategory,
        uint flags,
        IntPtr pInputType,
        IntPtr pOutputType,
        out IntPtr pppMFTActivate,
        out uint pcMFTActivate);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr pv);

    private const uint MFT_MESSAGE_COMMAND_FLUSH = 0x00000000;
    private const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    private const uint MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    private const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
    private const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        void GetItem(ref Guid guidKey, [In, Out] IntPtr pValue);
        void GetItemType(ref Guid guidKey, out int pType);
        void CompareItem(ref Guid guidKey, IntPtr Value, out bool pbResult);
        void Compare(IntPtr pTheirs, int MatchType, out bool pbResult);
        void GetUINT32(ref Guid guidKey, out uint punValue);
        void GetUINT64(ref Guid guidKey, out ulong punValue);
        void GetDouble(ref Guid guidKey, out double pfValue);
        void GetGUID(ref Guid guidKey, out Guid pguidValue);
        void GetStringLength(ref Guid guidKey, out uint pcchLength);
        void GetString(ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        void GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
        void GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        void GetBlob(ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        void GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        void GetUnknown(ref Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        void SetItem(ref Guid guidKey, IntPtr Value);
        void DeleteItem(ref Guid guidKey);
        void DeleteAllItems();
        void SetUINT32(ref Guid guidKey, uint unValue);
        void SetUINT64(ref Guid guidKey, ulong unValue);
        void SetDouble(ref Guid guidKey, double fValue);
        void SetGUID(ref Guid guidKey, ref Guid guidValue);
        void SetString(ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        void SetBlob(ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
        void SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        void LockStore();
        void UnlockStore();
        void GetCount(out uint pcItems);
        void GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] IntPtr pValue);
        void CopyAllItems(IntPtr pDest);
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        // NOTE: the CLR prepends the IUnknown slots itself, and IMFSample derives from
        // IMFAttributes, so the 30 attribute methods come first. Do not add placeholders.
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        void GetUINT32(); void GetUINT64(); void GetDouble(); void GetGUID();
        void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
        void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID();
        void SetString(); void SetBlob(); void SetUnknown();
        void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();

        void GetSampleFlags(out uint pdwSampleFlags);
        void SetSampleFlags(uint dwSampleFlags);
        void GetSampleTime(out long phnsSampleTime);
        void SetSampleTime(long hnsSampleTime);
        void GetSampleDuration(out long phnsSampleDuration);
        void SetSampleDuration(long hnsSampleDuration);
        void GetBufferCount(out uint pdwBufferCount);
        void GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
        void ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        void AddBuffer(IMFMediaBuffer pBuffer);
        void RemoveBufferByIndex(uint dwIndex);
        void RemoveAllBuffers();
        void GetTotalLength(out uint pcbTotalLength);
        void CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        void Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        void Unlock();
        void GetCurrentLength(out uint pcbCurrentLength);
        void SetCurrentLength(uint cbCurrentLength);
        void GetMaxLength(out uint pcbMaxLength);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_STREAM_INFO
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    [ComImport, Guid("BF94C121-5B05-4E6F-8000-BA598961414D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFTransform
    {
        void GetStreamLimits(out uint pdwInputMinimum, out uint pdwInputMaximum, out uint pdwOutputMinimum, out uint pdwOutputMaximum);
        void GetStreamCount(out uint pcInputStreams, out uint pcOutputStreams);
        void GetStreamIDs(uint dwInputIDArraySize, [Out] uint[] pdwInputIDs, uint dwOutputIDArraySize, [Out] uint[] pdwOutputIDs);
        void GetInputStreamInfo(uint dwInputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo);
        void GetOutputStreamInfo(uint dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo);
        void GetAttributes(out IntPtr pAttributes);
        void GetInputStreamAttributes(uint dwInputStreamID, out IntPtr pAttributes);
        void GetOutputStreamAttributes(uint dwOutputStreamID, out IntPtr pAttributes);
        void DeleteInputStream(uint dwStreamID);
        void AddInputStreams(uint cStreams, [In] uint[] adwStreamIDs);
        void GetInputAvailableType(uint dwInputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
        void GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int SetInputType(uint dwInputStreamID, IMFMediaType pType, uint dwFlags);
        [PreserveSig] int SetOutputType(uint dwOutputStreamID, IMFMediaType pType, uint dwFlags);
        void GetInputCurrentType(uint dwInputStreamID, out IMFMediaType ppType);
        void GetOutputCurrentType(uint dwOutputStreamID, out IMFMediaType ppType);
        void GetInputStatus(uint dwInputStreamID, out uint pdwFlags);
        void GetOutputStatus(out uint pdwFlags);
        void SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
        [PreserveSig] int ProcessEvent(uint dwInputStreamID, IntPtr pEvent);
        [PreserveSig] int ProcessMessage(uint eMessage, IntPtr ulParam);
        [PreserveSig] int ProcessInput(uint dwInputStreamID, IMFSample pSample, uint dwFlags);

        // MFT_OUTPUT_DATA_BUFFER is passed as a raw pointer: marshaling an array of
        // structs through the COM interop layer tries to use a type library (which is
        // not registered) and throws 0x80131165. The struct is written by hand instead.
        [PreserveSig] int ProcessOutput(uint dwFlags, uint cOutputBufferCount, IntPtr pOutputSamples, out uint pdwStatus);
    }

    [ComImport, Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        void IsSupported(ref Guid Api);
        void IsModifiable(ref Guid Api);
        void GetParameterRange(ref Guid Api, out object ValueMin, out object ValueMax, out object SteppingDelta);
        void GetParameterValues(ref Guid Api, out IntPtr Values, out uint ValuesCount);
        void GetDefaultValue(ref Guid Api, out object Value);
        void GetValue(ref Guid Api, out object Value);
        void SetValue(ref Guid Api, ref object Value);
        void RegisterForEvent(ref Guid Api, IntPtr userData);
        void UnregisterForEvent(ref Guid Api);
        void SetAllDefaults();
        void SetValueWithLargeData(ref Guid Api, [In, MarshalAs(UnmanagedType.LPArray)] byte[] hiData, uint hiDataSize);
        void GetValueWithLargeData(ref Guid Api, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] hiData, uint hiDataSize);
    }
}
