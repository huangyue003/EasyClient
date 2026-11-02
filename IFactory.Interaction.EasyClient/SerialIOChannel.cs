using System.IO.Ports;
using System.Buffers;
using Microsoft.Extensions.Logging;
namespace IFactory.Interaction.EasyClient;
public class SerialIOChannel : ChannelBase
{
    private readonly SerialPort serialIO;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveTask;
    public SerialIOEndPoint SioEndPoint { get; set; }
    public SerialIOChannel(string uri)
    {
        Uri = uri;
        SioEndPoint = new SerialIOEndPoint(uri);
        serialIO = new SerialPort(SioEndPoint.PortName, SioEndPoint.BaudRate, SioEndPoint.Parity, SioEndPoint.Databits, SioEndPoint.StopBits)
        {
            RtsEnable = SioEndPoint.RtsEnable,
            DtrEnable = SioEndPoint.DtrEnable,
            ReadBufferSize = 64 * 1024,
            WriteTimeout = SioEndPoint.WirteTimeout
        };
        try { serialIO.Open(); }
        catch { serialIO.Dispose(); throw; }
        IsClosed = false;
        IsConnected = true;
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }
    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(6144);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int count = await serialIO.BaseStream.ReadAsync(buffer.AsMemory(0, 6144), _cts.Token).ConfigureAwait(false);
                if (count == 0) break;
                ReceiveData(buffer.AsSpan(0, count));
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
            {
                RecordError(ex);
                logger?.LogError(ex, "Serial receive failed: {Uri}", Uri);
                EndPendingResponse(ex);
            }
        }
        finally
        {
            IsConnected = false;
            StopCallbacks();
            IsClosed = true;
            serialIO.Dispose();
            EndPendingResponse(new IOException("Serial connection closed."));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    protected override async ValueTask SendCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (IsClosed || !IsConnected || !serialIO.IsOpen) throw new InvalidOperationException("Serial port is closed.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        if (SioEndPoint.WirteTimeout > 0) linked.CancelAfter(SioEndPoint.WirteTimeout);
        // Abort the native handle as well: not every serial driver honors async cancellation.
        using var abort = linked.Token.Register(() =>
        {
            IsClosed = true;
            IsConnected = false;
            serialIO.Dispose();
        });
        try { await serialIO.BaseStream.WriteAsync(data, linked.Token).ConfigureAwait(false); }
        catch (Exception) when (linked.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested && !_cts.IsCancellationRequested)
                throw new TimeoutException("Serial write timed out; port closed to abort the write.");
            throw new OperationCanceledException(linked.Token);
        }
    }
    public override async Task CloseAsync()
    {
        StopCallbacks();
        IsClosed = true;
        IsConnected = false;
        EndPendingResponse(new ObjectDisposedException(nameof(SerialIOChannel)));
        _cts.Cancel();
        serialIO.Dispose();
        await _receiveTask.ConfigureAwait(false);
    }
}
