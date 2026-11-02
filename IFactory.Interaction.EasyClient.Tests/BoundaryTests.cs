using System.Net;
using System.Net.Sockets;
namespace IFactory.Interaction.EasyClient.Tests;
public class BoundaryTests
{
    private sealed class Probe : ChannelBase
    {
        public void Deliver(byte value) => ReceiveData(new byte[] { value });
        public void Clear() => ClearCallbacks();
        protected override ValueTask SendCoreAsync(ReadOnlyMemory<byte> data, CancellationToken token) => ValueTask.CompletedTask;
        public override Task CloseAsync() { StopCallbacks(); return Task.CompletedTask; }
    }
    [Fact]
    public async Task ConnectionChangeDiscardsQueuedOldBytes()
    {
        var channel = new Probe();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = new List<byte>();
        channel.DataReceiceHandler = data =>
        {
            if (data[0] == 1) { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); }
            values.Add(data[0]);
            if (data[0] == 3) completed.TrySetResult();
        };
        try
        {
            channel.Deliver(1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            channel.Deliver(2);
            channel.Clear();
            channel.Deliver(3);
            release.Set();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new byte[] { 1, 3 }, values);
        }
        finally { release.Set(); await channel.CloseAsync(); }
    }
    [Fact]
    public async Task OverflowIsExplicitAndQueuedBytesAreDiscarded()
    {
        var channel = new Probe();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DataReceiceHandler = _ => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); };
        try
        {
            channel.Deliver(1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (int i = 0; i < 256; i++) channel.Deliver(2);
            var error = Assert.Throws<IOException>(() => channel.Deliver(3));
            Assert.Same(error, channel.LastError);
        }
        finally { release.Set(); await channel.CloseAsync(); }
    }
    [Fact]
    public async Task InvalidResetPreservesWorkingChannelAndExposesError()
    {
        var client = new EasyClient("tcp://127.0.0.1:12345");
        var old = client.Channel;
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => client.ResetChannelAsync("sio://COM1/?BaudRate=0"));
            Assert.Same(old, client.Channel);
            Assert.False(old.IsClosed);
            Assert.NotNull(client.LastResetError);
        }
        finally { await client.CloseAsync(); }
    }
    [Fact]
    public async Task EmptyUdpDatagramReachesCallback()
    {
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var channel = new UdpChannel($"udp://127.0.0.1:0/?RemoteEndPoint={peer.LocalEndPoint}");
        var received = new TaskCompletionSource<List<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DataReceiceHandler = data => received.TrySetResult(data);
        try
        {
            await channel.SendAsync(new byte[] { 1 });
            var source = await peer.ReceiveFromAsync(new byte[1].AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0));
            await peer.SendToAsync(ReadOnlyMemory<byte>.Empty, SocketFlags.None, source.RemoteEndPoint);
            Assert.Empty(await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { await channel.CloseAsync(); }
    }
}
