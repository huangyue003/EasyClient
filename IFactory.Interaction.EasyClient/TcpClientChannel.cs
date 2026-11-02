using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace IFactory.Interaction.EasyClient;

public class TcpClientChannel : ChannelBase
{
    private readonly object _socketGate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly IPEndPoint _endPoint;
    private readonly int _bufferSize = 6144;
    private readonly Task _connectionTask;
    private Socket? _socket;

    public TcpClientChannel(string uri)
    {
        Uri = uri;
        var address = new Uri(uri);
        _endPoint = IPEndPoint.Parse(address.Authority);
        _bufferSize = ChannelOptions.PositiveInt(ChannelOptions.Parse(address), "ReceiceBuffer", 6144);
        IsClosed = false;
        _connectionTask = Task.Run(ConnectionLoopAsync);
    }

    private async Task ConnectionLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            using var socket = new Socket(_endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
            try
            {
                await socket.ConnectAsync(_endPoint, _cts.Token).ConfigureAwait(false);
                lock (_socketGate)
                {
                    if (_cts.IsCancellationRequested) return;
                    _socket = socket;
                    IsConnected = true;
                }
                while (!_cts.IsCancellationRequested)
                {
                    int count = await socket.ReceiveAsync(buffer.AsMemory(0, _bufferSize), SocketFlags.None, _cts.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    ReceiveData(buffer.AsSpan(0, count));
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (!_cts.IsCancellationRequested) logger?.LogError(ex, "TCP connection failed: {Uri}", Uri);
            }
            finally
            {
                lock (_socketGate)
                {
                    if (ReferenceEquals(_socket, socket)) _socket = null;
                    IsConnected = false;
                }
                EndPendingResponse(new IOException("TCP connection closed."));
                ArrayPool<byte>.Shared.Return(buffer);
            }
            socket.Dispose();
            try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task CloseAsync()
    {
        _cts.Cancel();
        lock (_socketGate)
        {
            _socket?.Dispose();
            IsConnected = false;
            IsClosed = true;
        }
        EndPendingResponse(new IOException("TCP connection closed."));
        await _connectionTask.ConfigureAwait(false);
    }

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> data)
    {
        await _sendGate.WaitAsync().ConfigureAwait(false);
        Socket? socket = null;
        try
        {
            lock (_socketGate) socket = _socket;
            if (socket == null) throw new InvalidOperationException("TCP is not connected.");
            while (!data.IsEmpty)
            {
                int sent = await socket.SendAsync(data, SocketFlags.None, _cts.Token).ConfigureAwait(false);
                if (sent == 0) throw new IOException("TCP send returned zero bytes.");
                data = data[sent..];
            }
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
        finally { _sendGate.Release(); }
    }

    public override void Send(ReadOnlyMemory<byte> data) => SendAsync(data).AsTask().GetAwaiter().GetResult();
}
