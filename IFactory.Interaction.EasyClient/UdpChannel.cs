using System.Net;
using System.Net.Sockets;
using System.Buffers;
using Microsoft.Extensions.Logging;
namespace IFactory.Interaction.EasyClient;
public class UdpChannel : ChannelBase
{
    private readonly Socket _socket;
    private readonly IPEndPoint _remote;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveTask;
    private readonly int _bufferSize;
    public UdpChannel(string uri)
    {
        Uri = uri;
        var address = new Uri(uri);
        var query = ChannelOptions.Parse(address);
        if (!query.TryGetValue("RemoteEndPoint", out var remote) || !IPEndPoint.TryParse(remote, out var endpoint))
            throw new ArgumentException("A valid RemoteEndPoint is required.", nameof(uri));
        _remote = endpoint;
        var local = IPEndPoint.Parse(address.Authority);
        if (local.AddressFamily != _remote.AddressFamily) throw new ArgumentException("Endpoint address families must match.");
        _bufferSize = ChannelOptions.PositiveInt(query, "ReceiceBuffer", 6144);
        _socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _socket.Bind(local);
            if (OperatingSystem.IsWindows()) _socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null);
        }
        catch { _socket.Dispose(); throw; }
        IsClosed = false;
        IsConnected = true;
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }
    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _socket.ReceiveFromAsync(buffer.AsMemory(0, _bufferSize), SocketFlags.None, _remote, _cts.Token).ConfigureAwait(false);
                if (result.RemoteEndPoint.Equals(_remote)) ReceiveData(buffer.AsSpan(0, result.ReceivedBytes));
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
            {
                logger?.LogError(ex, "UDP receive failed: {Uri}", Uri);
                EndPendingResponse(ex);
            }
        }
        finally
        {
            IsConnected = false;
            IsClosed = true;
            _socket.Dispose();
            EndPendingResponse(new IOException("UDP channel closed."));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    public override async Task CloseAsync()
    {
        IsClosed = true;
        IsConnected = false;
        EndPendingResponse(new ObjectDisposedException(nameof(UdpChannel)));
        _cts.Cancel();
        _socket.Dispose();
        await _receiveTask.ConfigureAwait(false);
    }
    public override async ValueTask SendAsync(ReadOnlyMemory<byte> data)
    {
        if (IsClosed) throw new ObjectDisposedException(nameof(UdpChannel));
        await _socket.SendToAsync(data, SocketFlags.None, _remote, _cts.Token).ConfigureAwait(false);
    }
    public override void Send(ReadOnlyMemory<byte> data)
    {
        if (IsClosed) throw new ObjectDisposedException(nameof(UdpChannel));
        _socket.SendTo(data.Span, SocketFlags.None, _remote);
    }
}
