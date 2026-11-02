using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
namespace IFactory.Interaction.EasyClient;
public class TcpClientChannel : ChannelBase
{
    private readonly object _socketGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly IPEndPoint _endPoint;
    private readonly int _bufferSize;
    private readonly Task _connectionTask;
    private readonly Func<Socket> _createSocket;
    private readonly int _retryInitial;
    private readonly int _retryMax;
    private Socket? _socket;
    private Socket? _abortedSocket;
    public TcpClientChannel(string uri) : this(uri, null) { }
    internal TcpClientChannel(string uri, Func<Socket>? createSocket)
    {
        var address = ChannelOptions.Address(uri);
        var options = ChannelOptions.Parse(address);
        _endPoint = ChannelOptions.Endpoint(address);
        _bufferSize = ChannelOptions.ReceiveBuffer(options);
        _retryInitial = ChannelOptions.BoundedInt(options, "ReconnectInitialMs", 200, 1, 60000);
        _retryMax = ChannelOptions.BoundedInt(options, "ReconnectMaxMs", 10000, _retryInitial, 60000);
        _createSocket = createSocket ?? (() => new Socket(_endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp));
        Uri = uri;
        IsClosed = false;
        _connectionTask = Task.Run(ConnectionLoopAsync);
    }
    private async Task ConnectionLoopAsync()
    {
        int failures = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket? socket = null;
                byte[]? buffer = null;
                try
                {
                    socket = _createSocket();
                    buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
                    await socket.ConnectAsync(_endPoint, _cts.Token).ConfigureAwait(false);
                    lock (_socketGate)
                    {
                        if (_cts.IsCancellationRequested) return;
                        _socket = socket;
                        _abortedSocket = null;
                        IsConnected = true;
                    }
                    if (failures > 0) Logger?.LogInformation("TCP connection recovered: {Uri}", Uri);
                    failures = 0;
                    while (!_cts.IsCancellationRequested)
                    {
                        int count = await socket.ReceiveAsync(buffer.AsMemory(0, _bufferSize), SocketFlags.None, _cts.Token).ConfigureAwait(false);
                        if (count == 0) throw new IOException("TCP peer closed the connection.");
                        ReceiveData(buffer.AsSpan(0, count));
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    bool aborted;
                    lock (_socketGate) aborted = socket != null && ReferenceEquals(_abortedSocket, socket);
                    if (!_cts.IsCancellationRequested && !aborted)
                    {
                        RecordError(ex);
                        if (failures == 0) Logger?.LogWarning(ex, "TCP connection failed: {Uri}", Uri);
                        else Logger?.LogDebug(ex, "TCP retry failed: {Uri}", Uri);
                    }
                    failures = Math.Min(failures + 1, 30);
                }
                finally
                {
                    lock (_socketGate)
                    {
                        if (ReferenceEquals(_socket, socket)) _socket = null;
                        IsConnected = false;
                    }
                    ClearCallbacks();
                    FailPendingResponse(new IOException("TCP connection closed."));
                    socket?.Dispose();
                    if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
                }
                int delay = (int)Math.Min(_retryMax, _retryInitial * Math.Pow(2, Math.Max(0, failures - 1)));
                await Task.Delay(Random.Shared.Next(Math.Max(1, delay * 4 / 5), delay + 1), _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RecordError(ex);
            IsConnected = false;
            IsClosed = true;
            StopCallbacks();
            FailPendingResponse(ex);
            Logger?.LogCritical(ex, "TCP connection worker stopped: {Uri}", Uri);
        }
    }
    private void AbortConnection(Socket socket, Exception cause)
    {
        lock (_socketGate)
        {
            if (!ReferenceEquals(_socket, socket)) return;
            _socket = null;
            _abortedSocket = socket;
            IsConnected = false;
            RecordError(cause);
        }
        socket.Dispose();
        if (cause.InnerException is OperationCanceledException)
            Logger?.LogInformation("TCP send aborted; connection reset: {Uri}", Uri);
        else Logger?.LogWarning(cause, "TCP send failed: {Uri}", Uri);
    }
    public override async Task CloseAsync()
    {
        IsClosed = true;
        StopCallbacks();
        _cts.Cancel();
        lock (_socketGate) { _socket?.Dispose(); _socket = null; IsConnected = false; }
        FailPendingResponse(new ObjectDisposedException(nameof(TcpClientChannel)));
        await _connectionTask.ConfigureAwait(false);
    }
    protected override async ValueTask SendCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        linked.Token.ThrowIfCancellationRequested();
        Socket socket;
        lock (_socketGate) socket = _socket ?? throw new InvalidOperationException("TCP is not connected.");
        try
        {
            while (!data.IsEmpty)
            {
                int sent = await socket.SendAsync(data[..Math.Min(data.Length, 64 * 1024)], SocketFlags.None, linked.Token).ConfigureAwait(false);
                if (sent == 0) throw new IOException("TCP send returned zero bytes.");
                data = data[sent..];
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested) AbortConnection(socket, new IOException("TCP send failed or was cancelled after entering transport.", ex));
            throw;
        }
    }
}
