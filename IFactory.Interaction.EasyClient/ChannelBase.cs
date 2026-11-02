using System.Text;
using Microsoft.Extensions.Logging;

namespace IFactory.Interaction.EasyClient;

public abstract class ChannelBase : IChannel
{
    public string Uri { get; set; } = "";
    public bool IsClosed { get; set; } = true;
    public bool IsConnected { get; set; }
    protected ILogger? logger;
    public ILogger? Logger { get => logger; set => logger = value; }
    private readonly object _receiveGate = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private PendingResponse? _pending;
    public Action<List<byte>>? DataReceiceHandler { get; set; }

    private sealed class PendingResponse(int length)
    {
        public readonly int Length = length;
        public readonly List<byte> Buffer = new();
        public readonly TaskCompletionSource<List<byte>> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Copy before returning pooled transport buffers. All response state belongs to this lock.
    protected void ReceiveData(ReadOnlySpan<byte> data)
    {
        List<byte>? unsolicited = null;
        lock (_receiveGate)
        {
            if (_pending is { } pending)
            {
                int count = pending.Length > 0
                    ? Math.Min(data.Length, pending.Length - pending.Buffer.Count) : data.Length;
                pending.Buffer.AddRange(data[..count].ToArray());
                if (pending.Length == 0 || pending.Buffer.Count >= pending.Length)
                {
                    _pending = null;
                    pending.Completion.TrySetResult(pending.Buffer.ToList());
                }
                if (count < data.Length) unsolicited = data[count..].ToArray().ToList();
            }
            else unsolicited = data.ToArray().ToList();
        }
        if (unsolicited is { Count: > 0 })
        {
            var snapshot = unsolicited;
            _ = Task.Run(() =>
            {
                try { DataReceiceHandler?.Invoke(snapshot); }
                catch (Exception ex) { logger?.LogError(ex, "Receive callback failed: {Uri}", Uri); }
            });
        }
    }

    protected void EndPendingResponse(Exception? error = null)
    {
        lock (_receiveGate)
        {
            if (_pending is { } pending)
            {
                _pending = null;
                if (error is null) pending.Completion.TrySetResult(pending.Buffer.ToList()); else pending.Completion.TrySetException(error);
            }
        }
    }

    public abstract Task CloseAsync();
    public abstract void Send(ReadOnlyMemory<byte> data);
    public abstract ValueTask SendAsync(ReadOnlyMemory<byte> data);
    public void Send(string data) => Send(Encoding.UTF8.GetBytes(data));
    public ValueTask SendAsync(string data) => SendAsync(Encoding.UTF8.GetBytes(data));

    public async Task<List<byte>> SendReceiveAsync(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, -1);
        ArgumentOutOfRangeException.ThrowIfNegative(expectReceiveLength);
        await _requestGate.WaitAsync().ConfigureAwait(false);
        var pending = new PendingResponse(expectReceiveLength);
        try
        {
            lock (_receiveGate) _pending = pending;
            await SendAsync(data).ConfigureAwait(false);
            try
            {
                return await pending.Completion.Task.WaitAsync(
                    millisecondsTimeout == -1 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(millisecondsTimeout)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lock (_receiveGate)
                {
                    if (ReferenceEquals(_pending, pending)) _pending = null;
                    throw new TimeoutException($"Response timed out after {millisecondsTimeout} ms; received {pending.Buffer.Count} bytes.");
                }
            }
        }
        finally
        {
            lock (_receiveGate)
                if (ReferenceEquals(_pending, pending)) _pending = null;
            _requestGate.Release();
        }
    }

    public List<byte> SendReceive(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength)
        => SendReceiveAsync(data, millisecondsTimeout, expectReceiveLength).GetAwaiter().GetResult();
    public async Task<string> SendReceiveAsync(string data, int millisecondsTimeout, int expectReceiveLength)
        => Encoding.UTF8.GetString((await SendReceiveAsync(Encoding.UTF8.GetBytes(data), millisecondsTimeout, expectReceiveLength).ConfigureAwait(false)).ToArray());
    public string SendReceive(string data, int millisecondsTimeout, int expectReceiveLength)
        => Encoding.UTF8.GetString(SendReceive(Encoding.UTF8.GetBytes(data), millisecondsTimeout, expectReceiveLength).ToArray());
}
