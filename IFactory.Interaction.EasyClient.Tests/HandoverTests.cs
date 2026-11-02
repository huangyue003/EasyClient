using System.Net;
using System.Net.Sockets;
namespace IFactory.Interaction.EasyClient.Tests;
public class HandoverTests
{
    private sealed class RetryingCreator : IChannelCreator
    {
        public int Attempts;
        public IChannel CreateChannel(string uri)
        {
            Attempts++;
            if (Attempts is 2 or 3) throw new UnauthorizedAccessException("Transient port release");
            return new Probe();
        }
    }
    [Fact]
    public async Task SerialResetRetriesOnlyTransientOpenFailures()
    {
        var creator = new RetryingCreator();
        await using var client = new EasyClient("sio://COM1/?BaudRate=9600", creator);
        await client.ResetChannelAsync();
        Assert.Equal(4, creator.Attempts);
        Assert.Null(client.LastResetError);
        await client.CloseAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync(new byte[1]).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendReceiveAsync(new byte[1], 100));
    }
    [Fact]
    public async Task SerialWriteAlreadyCancelledDoesNotStartOrAbort()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        bool start = false, abort = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SerialWriteOperation.RunAsync(
            () => { start = true; return Task.CompletedTask; }, () => abort = true, cts.Token));
        Assert.False(start); Assert.False(abort);
    }
    [Fact]
    public async Task SerialWriteCancellationAbortsOnlyOutstandingWrite()
    {
        using var cts = new CancellationTokenSource();
        int aborted = 0;
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = SerialWriteOperation.RunAsync(() => write.Task, () => { aborted++; write.TrySetCanceled(cts.Token); }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, aborted);
    }
    [Fact]
    public async Task CompletedWriteDoesNotAbortWhenCancellationPrecedesContinuation()
    {
        using var cts = new CancellationTokenSource();
        int aborted = 0;
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = SerialWriteOperation.RunAsync(() => write.Task, () => aborted++, cts.Token);
        write.SetResult();
        cts.Cancel();
        await task;
        Assert.Equal(0, aborted);
    }
    [Theory]
    [InlineData("tcp://plc.local:5000")]
    [InlineData("tcp://127.0.0.1:1/?ReceiceBuffer=1048577")]
    [InlineData("tcp://127.0.0.1:1/?ReconnectInitialMs=500&ReconnectMaxMs=100")]
    public void InvalidConfigurationFailsBeforeConnecting(string uri)
        => Assert.Throws<ArgumentException>(() => new TcpClientChannel(uri));

    private sealed class Probe : ChannelBase
    {
        public void Deliver(byte[] data) => ReceiveData(data);
        protected override ValueTask SendCoreAsync(ReadOnlyMemory<byte> data, CancellationToken token) => ValueTask.CompletedTask;
        public override Task CloseAsync() { StopCallbacks(); FailPendingResponse(new ObjectDisposedException("Probe")); return Task.CompletedTask; }
    }
    [Fact]
    public async Task DiagnosticsAndResponseBoundAreExposedThroughInterface()
    {
        var probe = new Probe(); IChannel channel = probe;
        probe.Deliver(new byte[3]);
        Assert.Equal(1, channel.DroppedChunks); Assert.Equal(3, channel.DroppedBytes);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => channel.SendReceiveAsync(new byte[1], 100, ChannelBase.MaxResponseLength + 1));
        await probe.CloseAsync(); probe.Deliver(new byte[4]);
        Assert.Equal(3, channel.DroppedBytes);
    }
    [Fact]
    public async Task ByteLimitTriggersBeforeChunkLimit()
    {
        var probe = new Probe();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.DataReceiceHandler = _ => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); };
        try
        {
            probe.Deliver(new byte[1]); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var chunk = new byte[1024 * 1024];
            for (int i = 0; i < 4; i++) probe.Deliver(chunk);
            Assert.Throws<IOException>(() => probe.Deliver(new byte[1]));
            Assert.NotNull(((IChannel)probe).LastError);
            Assert.NotNull(((IChannel)probe).ErrorTimestamp);
        }
        finally { release.Set(); await probe.CloseAsync(); }
    }
    private sealed class ValidatingCreator : IChannelCreator
    {
        public int Validations; public Probe Created = new();
        public IChannel CreateChannel(string uri) => Created;
        public void Validate(string uri) { Validations++; if (uri == "invalid") throw new ArgumentException("Invalid custom URI."); }
    }
    [Fact]
    public async Task CustomValidationRunsBeforeOldChannelIsClosed()
    {
        var creator = new ValidatingCreator();
        await using var client = new EasyClient("custom", creator);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ResetChannelAsync("invalid"));
        Assert.Equal(2, creator.Validations); Assert.Same(creator.Created, client.Channel);
        Assert.NotNull(((IEasyClient)client).LastResetError);
    }
    [Fact]
    public async Task RecoverableWorkerCreationFailureIsObservedAndRetried()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int calls = 0;
        var channel = new TcpClientChannel($"tcp://{listener.LocalEndpoint}/?ReconnectInitialMs=10&ReconnectMaxMs=20", () =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("Injected creation failure");
            return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        });
        try
        {
            using var peer = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Connected(channel);
            Assert.IsType<InvalidOperationException>(channel.LastError);
            await channel.SendAsync(new byte[] { 1 });
            Assert.Equal(1, await peer.ReceiveAsync(new byte[1].AsMemory(), SocketFlags.None));
        }
        finally { await channel.CloseAsync(); listener.Stop(); }
    }
    [Fact]
    public async Task RealTcpSendCancellationKeepsAbortCauseAndReconnects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var channel = new TcpClientChannel($"tcp://{listener.LocalEndpoint}/?ReconnectInitialMs=10&ReconnectMaxMs=20", () =>
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { SendBufferSize = 1024 });
        try
        {
            using var peer = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(5));
            peer.ReceiveBufferSize = 1024;
            await Connected(channel);
            using var cts = new CancellationTokenSource();
            var send = channel.SendAsync(new byte[16 * 1024 * 1024], cts.Token).AsTask();
            await Task.Delay(50); if (send.IsFaulted) await send; Assert.False(send.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
            var reason = Assert.IsType<IOException>(channel.LastError);
            Assert.IsAssignableFrom<OperationCanceledException>(reason.InnerException);
            using var next = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Connected(channel);
            Assert.Same(reason, channel.LastError);
            await channel.SendAsync(new byte[] { 9 });
            var value = new byte[1];
            Assert.Equal(1, await next.ReceiveAsync(value.AsMemory(), SocketFlags.None)); Assert.Equal(9, value[0]);
        }
        finally { await channel.CloseAsync(); listener.Stop(); }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.SendAsync(new byte[1]).AsTask());
    }
    private static async Task Connected(TcpClientChannel channel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!channel.IsConnected) await Task.Delay(5, timeout.Token);
    }
    [Fact]
    public async Task UdpReportsBoundEndpointAndRejectedSource()
    {
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var stranger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0)); stranger.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await using var channel = new UdpChannel($"udp://127.0.0.1:0/?RemoteEndPoint={peer.LocalEndPoint}");
        Assert.True(channel.LocalEndPoint.Port > 0);
        await stranger.SendToAsync(new byte[] { 1 }.AsMemory(), SocketFlags.None, channel.LocalEndPoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (channel.RejectedSourceDatagrams == 0) await Task.Delay(5, timeout.Token);
        Assert.Equal(1, channel.RejectedSourceDatagrams); Assert.False(channel.IsClosed);
        await channel.CloseAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.SendAsync(new byte[1]).AsTask());
    }
}
