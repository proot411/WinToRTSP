using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinToRTSP.Audio;
using WinToRTSP.Config;
using WinToRTSP.Encoder;
using WinToRTSP.Security;

namespace WinToRTSP.Rtsp;

public class RtspServer : IDisposable
{
    private TcpListener? _listener;
    private readonly ConcurrentDictionary<string, RtspClientSession> _sessions = new();
    private readonly RtpPacketizer _packetizer = new();

    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private volatile bool _isRunning;

    private byte[]? _currentSps;
    private byte[]? _currentPps;
    private readonly object _spsLock = new();

    // Metrics tracking
    private long _bytesSentInWindow;
    private int _framesSentInWindow;
    private DateTime _lastMetricReset = DateTime.UtcNow;
    public double CurrentBitrateKbps { get; private set; }
    public double CurrentFps { get; private set; }
    public int ActiveViewerCount => _sessions.Values.Count(s => s.State == RtspSessionState.Playing);

    public IEnumerable<RtspClientSession> ActiveSessions => _sessions.Values;

    public bool IsRunning => _isRunning;

    public bool Start(int port)
    {
        if (_isRunning) return true;

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _isRunning = true;

            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
            Debug.WriteLine($"[RTSP] Server listening on port {port}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RTSP] Failed to start server on port {port}: {ex.Message}");
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        try
        {
            _listener?.Stop();
        }
        catch { }

        foreach (var session in _sessions.Values)
        {
            session.Close();
        }
        _sessions.Clear();

        _listener = null;
        Debug.WriteLine("[RTSP] Server stopped.");
    }

    public void SetParameters(byte[]? sps, byte[]? pps)
    {
        lock (_spsLock)
        {
            if (sps != null) _currentSps = sps;
            if (pps != null) _currentPps = pps;
        }
    }

    public void BroadcastVideoPacket(EncodedPacket packet)
    {
        if (!_isRunning) return;

        var nals = H264Utils.SplitNalUnits(packet.Data, packet.Length);

        // Always remember the latest SPS/PPS — even before any viewer connects —
        // so every DESCRIBE can carry sprop-parameter-sets.
        foreach (var nal in nals)
        {
            if (nal.IsSps || nal.IsPps)
            {
                byte[] copy = new byte[nal.Length];
                Buffer.BlockCopy(packet.Data, nal.Offset, copy, 0, nal.Length);
                SetParameters(nal.IsSps ? copy : null, nal.IsSps ? null : copy);
            }
        }

        if (_sessions.IsEmpty)
        {
            // Nobody is receiving: decay delivered-frame metrics instead of
            // freezing at the last value after the final viewer disconnects.
            if (CurrentFps != 0 || CurrentBitrateKbps != 0)
            {
                Interlocked.Exchange(ref _bytesSentInWindow, 0);
                Interlocked.Exchange(ref _framesSentInWindow, 0);
                CurrentFps = 0;
                CurrentBitrateKbps = 0;
            }
            return;
        }

        uint rtpTimestamp = (uint)((packet.TimestampUs * 90) / 1000);

        foreach (var nal in nals)
        {
            var rtpPackets = _packetizer.PacketizeH264(packet.Data, nal.Offset, nal.Length, rtpTimestamp, 0);
            foreach (var rtp in rtpPackets)
            {
                foreach (var session in _sessions.Values)
                {
                    if (session.State == RtspSessionState.Playing)
                    {
                        session.EnqueuePacket(rtp);
                        Interlocked.Add(ref _bytesSentInWindow, rtp.Length);
                    }
                }
            }
        }

        Interlocked.Increment(ref _framesSentInWindow);
        UpdateMetrics();
    }

    public void BroadcastAudioBuffer(byte[] pcmData, int length, int sampleRate)
    {
        if (!_isRunning || _sessions.IsEmpty) return;

        uint rtpTimestamp = (uint)((Stopwatch.GetTimestamp() * 48000) / Stopwatch.Frequency);
        // Interleaved channel 1: SETUP advertises "interleaved=0-1" (0 = video, 1 = audio).
        var rtpPackets = _packetizer.PacketizeAudioL16(pcmData, 0, length, rtpTimestamp, 1);

        foreach (var rtp in rtpPackets)
        {
            foreach (var session in _sessions.Values)
            {
                if (session.State == RtspSessionState.Playing)
                {
                    session.EnqueuePacket(rtp);
                    Interlocked.Add(ref _bytesSentInWindow, rtp.Length);
                }
            }
        }
    }

    private void UpdateMetrics()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastMetricReset).TotalSeconds;
        if (elapsed >= 1.0)
        {
            long bytes = Interlocked.Exchange(ref _bytesSentInWindow, 0);
            int frames = Interlocked.Exchange(ref _framesSentInWindow, 0);

            CurrentBitrateKbps = Math.Round((bytes * 8.0) / (elapsed * 1000.0), 1);
            CurrentFps = Math.Round(frames / elapsed, 1);
            _lastMetricReset = now;
        }
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                var clientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;

                // 1. Brute-force & IP ban check
                if (SecurityManager.IsIpBlocked(clientIp, out var remainingTime))
                {
                    Debug.WriteLine($"[RTSP] Rejected connection from banned IP {clientIp}. Remaining ban: {remainingTime.TotalMinutes:F1} mins");
                    client.Close();
                    continue;
                }

                var session = new RtspClientSession(client);
                session.Closed += s => _sessions.TryRemove(s.SessionId, out _);
                _sessions[session.SessionId] = session;

                _ = Task.Run(() => HandleClientAsync(session, token), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RTSP] Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(RtspClientSession session, CancellationToken token)
    {
        var stream = session.Client.GetStream();
        var buffer = new byte[8192];
        var memoryStream = new MemoryStream();

        try
        {
            while (!token.IsCancellationRequested && session.State != RtspSessionState.Closed)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                if (read <= 0) break;

                memoryStream.Write(buffer, 0, read);
                var buf = memoryStream.GetBuffer();
                int len = (int)memoryStream.Length;

                // The stream mixes RTSP text messages with '$'-prefixed interleaved (RTCP)
                // packets. Parse only complete items; partial packet/text bytes must stay
                // buffered instead of leaking into the RTSP text parser (the old pre-skip
                // corrupted requests when a packet straddled two TCP reads).
                while (true)
                {
                    if (len == 0) break;

                    if (buf[0] == 0x24) // '$' interleaved packet
                    {
                        if (len < 4) break;                  // header incomplete, wait for more
                        int packetLen = (buf[2] << 8) | buf[3];
                        if (len < 4 + packetLen) break;      // payload continues in a later read
                        ConsumePrefix(memoryStream, 4 + packetLen);
                        len = (int)memoryStream.Length;
                        continue;
                    }

                    int endOfHeaders = IndexOfHeaderEnd(buf, len);
                    if (endOfHeaders < 0)
                    {
                        // Not a complete request yet; cap runaway non-RTSP garbage.
                        if (len > 64 * 1024)
                        {
                            memoryStream.SetLength(0);
                            len = 0;
                        }
                        break;
                    }

                    string requestText = Encoding.UTF8.GetString(buf, 0, endOfHeaders);
                    ConsumePrefix(memoryStream, endOfHeaders + 4);
                    len = (int)memoryStream.Length;

                    await ProcessRtspRequestAsync(session, requestText);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RTSP] Client handler exception: {ex.Message}");
        }
        finally
        {
            session.Close();
        }
    }

    /// <summary>Removes <paramref name="count"/> leading bytes from the accumulation buffer.</summary>
    private static void ConsumePrefix(MemoryStream ms, int count)
    {
        var buf = ms.GetBuffer();
        int len = (int)ms.Length;
        int remain = len - count;
        if (remain > 0)
        {
            // Destination starts before source, so a forward copy keeps overlapping bytes intact.
            Buffer.BlockCopy(buf, count, buf, 0, remain);
        }
        ms.SetLength(remain);
        ms.Position = remain; // next append must go after the unconsumed bytes
    }

    /// <summary>Returns the offset of the first "\r\n\r\n" in <paramref name="buf"/>, or -1.</summary>
    private static int IndexOfHeaderEnd(byte[] buf, int len)
    {
        for (int i = 0; i + 3 < len; i++)
        {
            if (buf[i] == 0x0D && buf[i + 1] == 0x0A && buf[i + 2] == 0x0D && buf[i + 3] == 0x0A)
                return i;
        }
        return -1;
    }

    private async Task ProcessRtspRequestAsync(RtspClientSession session, string requestText)
    {
        var lines = requestText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3) return;

        string method = requestLine[0].Trim().ToUpperInvariant();
        string uri = requestLine[1].Trim();
        string cseq = "1";
        string authHeader = string.Empty;
        string transportHeader = string.Empty;

        for (int i = 1; i < lines.Length; i++)
        {
            int colonIdx = lines[i].IndexOf(':');
            if (colonIdx > 0)
            {
                string headerName = lines[i].Substring(0, colonIdx).Trim();
                string headerValue = lines[i].Substring(colonIdx + 1).Trim();

                if (headerName.Equals("CSeq", StringComparison.OrdinalIgnoreCase))
                    cseq = headerValue;
                else if (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    authHeader = headerValue;
                else if (headerName.Equals("Transport", StringComparison.OrdinalIgnoreCase))
                    transportHeader = headerValue;
            }
        }

        var config = ConfigManager.Current;

        // Security check for authentication on DESCRIBE and SETUP
        if (config.RequireAuthentication && (method == "DESCRIBE" || method == "SETUP") && !session.IsAuthenticated)
        {
            if (string.IsNullOrEmpty(authHeader) || string.IsNullOrEmpty(session.CurrentNonce))
            {
                // Challenge client with Digest Auth
                session.CurrentNonce = RtspDigestAuth.GenerateNonce();
                string challengeResponse =
                    $"RTSP/1.0 401 Unauthorized\r\n" +
                    $"CSeq: {cseq}\r\n" +
                    $"WWW-Authenticate: Digest realm=\"{SecurityManager.RtspRealm}\", nonce=\"{session.CurrentNonce}\"\r\n\r\n";

                await session.SendRawAsync(Encoding.UTF8.GetBytes(challengeResponse));
                return;
            }

            bool authSuccess = RtspDigestAuth.ValidateDigest(
                method, uri, authHeader, session.CurrentNonce, config.Username);

            if (!authSuccess)
            {
                SecurityManager.RecordFailedAttempt(session.ClientIp);
                Debug.WriteLine($"[RTSP] Auth failed for IP {session.ClientIp}");

                session.CurrentNonce = RtspDigestAuth.GenerateNonce();
                string challengeResponse =
                    $"RTSP/1.0 401 Unauthorized\r\n" +
                    $"CSeq: {cseq}\r\n" +
                    $"WWW-Authenticate: Digest realm=\"{SecurityManager.RtspRealm}\", nonce=\"{session.CurrentNonce}\"\r\n\r\n";

                await session.SendRawAsync(Encoding.UTF8.GetBytes(challengeResponse));
                return;
            }

            // Authentication succeeded
            SecurityManager.RecordSuccessfulAttempt(session.ClientIp);
            session.IsAuthenticated = true;
        }

        // Handle RTSP Methods
        switch (method)
        {
            case "OPTIONS":
                string optionsResp =
                    $"RTSP/1.0 200 OK\r\n" +
                    $"CSeq: {cseq}\r\n" +
                    $"Public: OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN, GET_PARAMETER\r\n\r\n";
                await session.SendRawAsync(Encoding.UTF8.GetBytes(optionsResp));
                break;

            case "DESCRIBE":
                string sdp = BuildSdp(config, uri);
                string describeResp =
                    $"RTSP/1.0 200 OK\r\n" +
                    $"CSeq: {cseq}\r\n" +
                    $"Content-Type: application/sdp\r\n" +
                    $"Content-Base: {uri}/\r\n" +
                    $"Content-Length: {Encoding.UTF8.GetByteCount(sdp)}\r\n\r\n" +
                    sdp;
                await session.SendRawAsync(Encoding.UTF8.GetBytes(describeResp));
                break;

            case "SETUP":
                // Parse interleaved channels
                if (transportHeader.Contains("interleaved=", StringComparison.OrdinalIgnoreCase))
                {
                    session.State = RtspSessionState.Ready;
                    string setupResp =
                        $"RTSP/1.0 200 OK\r\n" +
                        $"CSeq: {cseq}\r\n" +
                        $"Session: {session.SessionId};timeout=60\r\n" +
                        $"Transport: {transportHeader}\r\n\r\n";
                    await session.SendRawAsync(Encoding.UTF8.GetBytes(setupResp));
                }
                else
                {
                    // Interleaved TCP requested or supported
                    session.State = RtspSessionState.Ready;
                    string setupResp =
                        $"RTSP/1.0 200 OK\r\n" +
                        $"CSeq: {cseq}\r\n" +
                        $"Session: {session.SessionId};timeout=60\r\n" +
                        $"Transport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n\r\n";
                    await session.SendRawAsync(Encoding.UTF8.GetBytes(setupResp));
                }
                break;

            case "PLAY":
                session.State = RtspSessionState.Playing;
                string playResp =
                    $"RTSP/1.0 200 OK\r\n" +
                    $"CSeq: {cseq}\r\n" +
                    $"Session: {session.SessionId}\r\n" +
                    $"Range: npt=0.000-\r\n\r\n";
                await session.SendRawAsync(Encoding.UTF8.GetBytes(playResp));
                break;

            case "TEARDOWN":
                string teardownResp =
                    $"RTSP/1.0 200 OK\r\n" +
                    $"CSeq: {cseq}\r\n" +
                    $"Session: {session.SessionId}\r\n\r\n";
                await session.SendRawAsync(Encoding.UTF8.GetBytes(teardownResp));
                session.Close();
                break;

            case "GET_PARAMETER":
                string getParamResp =
                    $"RTSP/1.0 200 OK\r\n" +
                    $"CSeq: {cseq}\r\n" +
                    $"Session: {session.SessionId}\r\n\r\n";
                await session.SendRawAsync(Encoding.UTF8.GetBytes(getParamResp));
                break;

            default:
                string notImplementedResp =
                    $"RTSP/1.0 501 Not Implemented\r\n" +
                    $"CSeq: {cseq}\r\n\r\n";
                await session.SendRawAsync(Encoding.UTF8.GetBytes(notImplementedResp));
                break;
        }
    }

    private string BuildSdp(AppConfig config, string streamUri)
    {
        string spsB64 = string.Empty;
        string ppsB64 = string.Empty;

        lock (_spsLock)
        {
            if (_currentSps != null) spsB64 = Convert.ToBase64String(_currentSps);
            if (_currentPps != null) ppsB64 = Convert.ToBase64String(_currentPps);
        }

        string sprop = (!string.IsNullOrEmpty(spsB64) && !string.IsNullOrEmpty(ppsB64))
            ? $";sprop-parameter-sets={spsB64},{ppsB64}"
            : "";

        var sb = new StringBuilder();
        sb.Append("v=0\r\n");
        sb.Append($"o=- {Stopwatch.GetTimestamp()} 1 IN IP4 0.0.0.0\r\n");
        sb.Append("s=WinToRTSP Screen Stream\r\n");
        sb.Append("t=0 0\r\n");
        sb.Append("m=video 0 RTP/AVP 96\r\n");
        sb.Append("c=IN IP4 0.0.0.0\r\n");
        sb.Append($"b=AS:{config.BitrateKbps}\r\n");
        sb.Append("a=rtpmap:96 H264/90000\r\n");
        sb.Append($"a=fmtp:96 packetization-mode=1{sprop}\r\n");
        sb.Append("a=control:trackID=0\r\n");

        if (config.EnableAudio)
        {
            sb.Append("m=audio 0 RTP/AVP 97\r\n");
            sb.Append("c=IN IP4 0.0.0.0\r\n");
            sb.Append("a=rtpmap:97 L16/48000/2\r\n");
            sb.Append("a=control:trackID=1\r\n");
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
