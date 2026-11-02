using System.Text;
using Microsoft.Extensions.Logging;
namespace IFactory.Interaction.EasyClient;
public abstract class ChannelBase : IChannel
{
    public string Uri { get; set; } = "";
    public bool IsClosed { get; set; } = true;
    public bool IsConnected { get; set; }
    protected ILogger? logger;
    public Exception? LastError { get; private set; }
    protected void RecordError(Exception error) => LastError = error;
    public ILogger? Logger { get => logger; set => logger = value; }
    private readonly object _receiveGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private PendingResponse? _pending;
    private readonly Queue<(Action<List<byte>> Handler, List<byte> Data)> _callbacks = new();
    private bool _dispatching;
    private bool _callbacksClosed;
    public Action<List<byte>>? DataReceiceHandler { get; set; }
    private sealed class PendingResponse(int length)
    {
        public readonly int Length = length;
        public readonly List<byte> Buffer = new();
        public readonly TaskCompletionSource<List<byte>> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    protected void ReceiveData(ReadOnlySpan<byte> data, bool datagram = false)
    {
        lock (_receiveGate)
        {
            int count = 0;
            bool hadPending = _pending != null;
            if (_pending is { } pending)
            {
                count = pending.Length > 0 ? Math.Min(data.Length, pending.Length - pending.Buffer.Count) : data.Length;
                pending.Buffer.AddRange(data[..count]);
                if (pending.Length == 0 || pending.Buffer.Count == pending.Length)
                {
                    _pending = null;
                    pending.Completion.TrySetResult(pending.Buffer);
                }
            }
            if ((count < data.Length || (datagram && data.IsEmpty && !hadPending)) && !_callbacksClosed && DataReceiceHandler is { } handler)
            {
                if (_callbacks.Count >= 256)
                {
                    var error = new IOException("Receive callback queue overflow; connection must be reset.");
                    RecordError(error);
                    _callbacks.Clear();
                    throw error;
                }
                var snapshot = new List<byte>(data.Length - count);
                snapshot.AddRange(data[count..]);
                _callbacks.Enqueue((handler, snapshot));
                if (!_dispatching) { _dispatching = true; _ = Task.Run(DispatchCallbacks); }
            }
        }
    }
    private void DispatchCallbacks()
    {
        while (true)
        {
            (Action<List<byte>> Handler, List<byte> Data) item;
            lock (_receiveGate)
            {
                if (_callbacks.Count == 0) { _dispatching = false; return; }
                item = _callbacks.Dequeue();
            }
            try { item.Handler(item.Data); }
            catch (Exception ex) { logger?.LogError(ex, "Receive callback failed: {Uri}", Uri); }
        }
    }
    protected void ClearCallbacks()
    {
        lock (_receiveGate) _callbacks.Clear();
    }
    protected void StopCallbacks()
    {
        lock (_receiveGate) { _callbacksClosed = true; _callbacks.Clear(); }
    }
    protected void EndPendingResponse(Exception? error = null)
    {
        lock (_receiveGate)
        {
            if (_pending is not { } pending) return;
            _pending = null;
            if (error == null) pending.Completion.TrySetResult(pending.Buffer);
            else pending.Completion.TrySetException(error);
        }
    }
    public abstract Task CloseAsync();
    protected abstract ValueTask SendCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    public void Send(ReadOnlyMemory<byte> data) => SendAsync(data).AsTask().GetAwaiter().GetResult();
    public ValueTask SendAsync(ReadOnlyMemory<byte> data) => SendAsync(data, CancellationToken.None);
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SendCoreAsync(data, cancellationToken).ConfigureAwait(false); }
        finally { _operationGate.Release(); }
    }
    public void Send(string data) => Send(Encoding.UTF8.GetBytes(data));
    public ValueTask SendAsync(string data) => SendAsync(Encoding.UTF8.GetBytes(data));
    public Task<List<byte>> SendReceiveAsync(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength = 0)
        => SendReceiveAsync(data, millisecondsTimeout, expectReceiveLength, CancellationToken.None);
    public async Task<List<byte>> SendReceiveAsync(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, -1);
        ArgumentOutOfRangeException.ThrowIfNegative(expectReceiveLength);
        using var deadline = millisecondsTimeout >= 0 ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        deadline?.CancelAfter(millisecondsTimeout);
        var token = deadline?.Token ?? cancellationToken;
        bool acquired = false;
        var pending = new PendingResponse(expectReceiveLength);
        try
        {
            await _operationGate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            lock (_receiveGate) _pending = pending;
            await SendCoreAsync(data, token).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline?.IsCancellationRequested == true)
        { throw new TimeoutException($"Operation timed out after {millisecondsTimeout} ms."); }
        finally
        {
            lock (_receiveGate)
                if (ReferenceEquals(_pending, pending)) _pending = null;
            // Observe a disconnect failure if sending failed before the response was awaited.
            if (pending.Completion.Task.IsFaulted) _ = pending.Completion.Task.Exception;
            if (acquired) _operationGate.Release();
        }
    }
    public List<byte> SendReceive(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength)
        => SendReceiveAsync(data, millisecondsTimeout, expectReceiveLength).GetAwaiter().GetResult();
    public async Task<string> SendReceiveAsync(string data, int millisecondsTimeout, int expectReceiveLength)
        => Encoding.UTF8.GetString((await SendReceiveAsync(Encoding.UTF8.GetBytes(data), millisecondsTimeout, expectReceiveLength).ConfigureAwait(false)).ToArray());
    public string SendReceive(string data, int millisecondsTimeout, int expectReceiveLength)
        => Encoding.UTF8.GetString(SendReceive(Encoding.UTF8.GetBytes(data), millisecondsTimeout, expectReceiveLength).ToArray());
}
