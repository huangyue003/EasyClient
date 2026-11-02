using System.Net;
using System.Net.Sockets;
namespace IFactory.Interaction.EasyClient.Tests;
public class SchedulingTests
{
    private sealed class Probe : ChannelBase
    {
        public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? Sender;
        public void Deliver(params byte[] bytes) => ReceiveData(bytes);
        protected override ValueTask SendCoreAsync(ReadOnlyMemory<byte> data, CancellationToken token)
            => Sender?.Invoke(data, token) ?? ValueTask.CompletedTask;
        public override Task CloseAsync() { StopCallbacks(); FailPendingResponse(new ObjectDisposedException("Probe")); return Task.CompletedTask; }
    }
    [Fact]
    public async Task OrdinarySendWaitsForRequestAndQueuedCancellationWorks()
    {
        var channel = new Probe();
        var sent = new List<byte>();
        channel.Sender = (data, _) => { sent.Add(data.Span[0]); return ValueTask.CompletedTask; };
        var request = channel.SendReceiveAsync(new byte[] { 1 }, 3000, 1);
        using var cancellation = new CancellationTokenSource();
        var cancelled = channel.SendAsync(new byte[] { 2 }, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var send = channel.SendAsync(new byte[] { 3 }).AsTask();
        Assert.False(send.IsCompleted);
        Assert.Equal(new byte[] { 1 }, sent);
        channel.Deliver(9);
        await request;
        await send;
        Assert.Equal(new byte[] { 1, 3 }, sent);
    }
    [Fact]
    public async Task TotalTimeoutIncludesQueueAndSend()
    {
        var channel = new Probe();
        channel.Sender = async (_, token) => await Task.Delay(Timeout.Infinite, token);
        var first = channel.SendReceiveAsync(new byte[] { 1 }, 100, 1);
        await Assert.ThrowsAsync<TimeoutException>(() => channel.SendReceiveAsync(new byte[] { 2 }, 10, 1));
        await Assert.ThrowsAsync<TimeoutException>(() => first);
        channel.Sender = (_, _) => { channel.Deliver(7); return ValueTask.CompletedTask; };
        Assert.Equal(new byte[] { 7 }, await channel.SendReceiveAsync(new byte[] { 3 }, 1000, 1));
    }
    [Fact]
    public async Task CallbacksAreOrderedAndSerial()
    {
        var channel = new Probe();
        var values = new List<byte>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DataReceiceHandler = data =>
        {
            values.Add(data[0]);
            if (values.Count == 100) completed.SetResult();
        };
        for (int i = 0; i < 100; i++) channel.Deliver((byte)i);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (byte)i), values);
        await channel.CloseAsync();
    }
    [Fact]
    public async Task OversizedUdpDatagramDoesNotCloseChannel()
    {
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var channel = new UdpChannel($"udp://127.0.0.1:0/?RemoteEndPoint={peer.LocalEndPoint}&ReceiceBuffer=4");
        try
        {
            var response = channel.SendReceiveAsync(new byte[] { 1 }, 3000, 2);
            var received = await peer.ReceiveFromAsync(new byte[10].AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0));
            await peer.SendToAsync(new byte[100].AsMemory(), SocketFlags.None, received.RemoteEndPoint);
            await peer.SendToAsync(new byte[] { 2, 3 }.AsMemory(), SocketFlags.None, received.RemoteEndPoint);
            Assert.Equal(new byte[] { 2, 3 }, await response);
            Assert.False(channel.IsClosed);
        }
        finally { await channel.CloseAsync(); }
    }
    private sealed class Factory : IChannelCreator
    {
        public readonly List<DelayedClose> Created = new();
        public IChannel CreateChannel(string uri) { var c = new DelayedClose(); Created.Add(c); return c; }
    }
    private sealed class DelayedClose : ChannelBase
    {
        public TaskCompletionSource? Block;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed;
        protected override ValueTask SendCoreAsync(ReadOnlyMemory<byte> data, CancellationToken token) => ValueTask.CompletedTask;
        public override async Task CloseAsync() { Entered.TrySetResult(); if (Block != null) await Block.Task; Closed = true; }
    }
    [Fact]
    public async Task CloseDuringResetPreventsReplacement()
    {
        var factory = new Factory();
        var client = new EasyClient("tcp://127.0.0.1:1", factory);
        var old = factory.Created[0];
        old.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reset = client.ResetChannelAsync();
        await old.Entered.Task;
        var close = client.CloseAsync();
        old.Block.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reset);
        await close;
        Assert.Single(factory.Created);
        Assert.True(old.Closed);
    }
    [Fact]
    public async Task ConcurrentResetsCloseEveryReplacedChannel()
    {
        var factory = new Factory();
        var client = new EasyClient("tcp://127.0.0.1:1", factory);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.ResetChannelAsync()));
        Assert.Equal(21, factory.Created.Count);
        Assert.All(factory.Created.Take(20), channel => Assert.True(channel.Closed));
        await client.CloseAsync();
        Assert.All(factory.Created, channel => Assert.True(channel.Closed));
    }
}
