using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinToRTSP.Rtsp;

public enum RtspSessionState
{
    Init,
    Ready,
    Playing,
    Closed
}

public class RtspClientSession : IDisposable
{
    public string SessionId { get; }
    public TcpClient Client { get; }
    public IPAddress ClientIp { get; }
    public RtspSessionState State { get; set; } = RtspSessionState.Init;
    public string CurrentNonce { get; set; } = string.Empty;
    public bool IsAuthenticated { get; set; }

    public int VideoInterleavedChannel { get; set; } = 0;
    public int AudioInterleavedChannel { get; set; } = 1;

    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public long BytesSent => _bytesSent;

    private readonly NetworkStream _stream;
    private readonly ConcurrentQueue<byte[]> _sendQueue = new();
    private readonly SemaphoreSlim _sendSignal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _sendTask;
    private long _bytesSent;
    private bool _disposed;

    public event Action<RtspClientSession>? Closed;

    public RtspClientSession(TcpClient client)
    {
        Client = client;
        _stream = client.GetStream();
        ClientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
        SessionId = Guid.NewGuid().ToString("N").Substring(0, 16);

        _sendTask = Task.Run(SendLoop);
    }

    public void EnqueuePacket(byte[] packet)
    {
        if (_disposed || State != RtspSessionState.Playing) return;

        // Cap queue to prevent lag buildup on slow networks
        if (_sendQueue.Count > 100)
        {
            _sendQueue.TryDequeue(out _);
        }

        _sendQueue.Enqueue(packet);
        try
        {
            _sendSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Session was disposed between the check above and Release — harmless.
        }
        catch (SemaphoreFullException)
        {
            // Count already at max; SendLoop will pick the packet up anyway.
        }
    }

    public async Task SendRawAsync(byte[] data)
    {
        if (_disposed) return;
        try
        {
            await _stream.WriteAsync(data, 0, data.Length, _cts.Token);
            await _stream.FlushAsync(_cts.Token);
            Interlocked.Add(ref _bytesSent, data.Length);
        }
        catch
        {
            Close();
        }
    }

    private async Task SendLoop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested && !_disposed)
        {
            try
            {
                await _sendSignal.WaitAsync(token);
                if (_sendQueue.TryDequeue(out var packet))
                {
                    await _stream.WriteAsync(packet, 0, packet.Length, token);
                    Interlocked.Add(ref _bytesSent, packet.Length);
                }
            }
            catch
            {
                break;
            }
        }
        Close();
    }

    public void Close()
    {
        if (!_disposed)
        {
            _disposed = true;
            State = RtspSessionState.Closed;
            _cts.Cancel();
            try { _stream.Close(); } catch { }
            try { Client.Close(); } catch { }
            Closed?.Invoke(this);
        }
    }

    public void Dispose()
    {
        Close();
        _cts.Dispose();
        _sendSignal.Dispose();
    }
}
