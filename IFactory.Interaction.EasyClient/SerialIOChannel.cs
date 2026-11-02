using System.IO.Ports;
using System.Buffers;
using Microsoft.Extensions.Logging;
namespace IFactory.Interaction.EasyClient;
public class SerialIOChannel : ChannelBase
{
    private readonly SerialPort serialIO;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveTask;
    private readonly int _writeTimeout;
    public SerialIOEndPoint SioEndPoint { get; set; }
    public SerialIOChannel(string uri)
    {
        Uri = uri;
        SioEndPoint = new SerialIOEndPoint(uri);
        _writeTimeout = SioEndPoint.WirteTimeout;
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
                Logger?.LogError(ex, "Serial receive failed: {Uri}", Uri);
                FailPendingResponse(ex);
            }
        }
        finally
        {
            IsConnected = false;
            StopCallbacks();
            IsClosed = true;
            serialIO.Dispose();
            FailPendingResponse(new IOException("Serial connection closed."));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    protected override async ValueTask SendCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        if (!IsConnected) throw new InvalidOperationException("Serial port is not connected.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        if (_writeTimeout > 0) linked.CancelAfter(_writeTimeout);
        try
        {
            await SerialWriteOperation.RunAsync(
                () => serialIO.BaseStream.WriteAsync(data, linked.Token).AsTask(),
                () => { IsClosed = true; IsConnected = false; serialIO.Dispose(); },
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_cts.IsCancellationRequested)
        { throw new TimeoutException("Serial write timed out; reset the port if it was aborted."); }
    }
    public override async Task CloseAsync()
    {
        StopCallbacks();
        IsClosed = true;
        IsConnected = false;
        FailPendingResponse(new ObjectDisposedException(nameof(SerialIOChannel)));
        _cts.Cancel();
        serialIO.Dispose();
        await _receiveTask.ConfigureAwait(false);
    }
}
