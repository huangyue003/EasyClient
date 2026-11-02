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
    private const int SioUdpConnReset = unchecked((int)0x9800000C); // Windows: ignore ICMP port-unreachable reset.
    public IPEndPoint LocalEndPoint { get; }
    private long _rejectedSourceDatagrams;
    public long RejectedSourceDatagrams => Interlocked.Read(ref _rejectedSourceDatagrams);
    public UdpChannel(string uri)
    {
        Uri = uri;
        var address = ChannelOptions.Address(uri);
        var query = ChannelOptions.Parse(address);
        if (!query.TryGetValue("RemoteEndPoint", out var remote) || !IPEndPoint.TryParse(remote, out var endpoint))
            throw new ArgumentException("A valid RemoteEndPoint is required.", nameof(uri));
        _remote = endpoint;
        var local = ChannelOptions.Endpoint(address);
        if (local.AddressFamily != _remote.AddressFamily) throw new ArgumentException("Endpoint address families must match.");
        _bufferSize = ChannelOptions.ReceiveBuffer(query);
        _socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _socket.Bind(local);
            if (OperatingSystem.IsWindows()) _socket.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
        }
        catch { _socket.Dispose(); throw; }
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        IsClosed = false;
        IsConnected = true;
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }
    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _socket.ReceiveFromAsync(buffer.AsMemory(0, 65536), SocketFlags.None, _remote, _cts.Token).ConfigureAwait(false);
                if (!result.RemoteEndPoint.Equals(_remote)) { Interlocked.Increment(ref _rejectedSourceDatagrams); continue; }
                if (result.ReceivedBytes > _bufferSize) { Logger?.LogWarning("Oversized UDP datagram dropped: {Size}", result.ReceivedBytes); continue; }
                if (result.RemoteEndPoint.Equals(_remote)) ReceiveData(buffer.AsSpan(0, result.ReceivedBytes), datagram: true);
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
            {
                RecordError(ex);
                Logger?.LogError(ex, "UDP receive failed: {Uri}", Uri);
                FailPendingResponse(ex);
            }
        }
        finally
        {
            IsConnected = false;
            StopCallbacks();
            IsClosed = true;
            _socket.Dispose();
            FailPendingResponse(new IOException("UDP channel closed."));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    public override async Task CloseAsync()
    {
        StopCallbacks();
        IsClosed = true;
        IsConnected = false;
        FailPendingResponse(new ObjectDisposedException(nameof(UdpChannel)));
        _cts.Cancel();
        _socket.Dispose();
        await _receiveTask.ConfigureAwait(false);
    }
    protected override async ValueTask SendCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (IsClosed) throw new ObjectDisposedException(nameof(UdpChannel));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await _socket.SendToAsync(data, SocketFlags.None, _remote, linked.Token).ConfigureAwait(false);
    }
}
