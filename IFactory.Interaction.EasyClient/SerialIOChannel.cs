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
                logger?.LogError(ex, "Serial receive failed: {Uri}", Uri);
                EndPendingResponse(ex);
            }
        }
        finally
        {
            IsConnected = false;
            EndPendingResponse(new IOException("Serial connection closed."));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    public override void Send(ReadOnlyMemory<byte> data)
    {
        if (IsClosed || !serialIO.IsOpen) throw new InvalidOperationException("Serial port is closed.");
        serialIO.BaseStream.Write(data.Span);
    }
    public override async ValueTask SendAsync(ReadOnlyMemory<byte> data)
    {
        if (IsClosed || !serialIO.IsOpen) throw new InvalidOperationException("Serial port is closed.");
        await serialIO.BaseStream.WriteAsync(data, _cts.Token).ConfigureAwait(false);
    }
    public override async Task CloseAsync()
    {
        IsClosed = true;
        IsConnected = false;
        EndPendingResponse(new ObjectDisposedException(nameof(SerialIOChannel)));
        _cts.Cancel();
        serialIO.Dispose();
        await _receiveTask.ConfigureAwait(false);
    }
}
