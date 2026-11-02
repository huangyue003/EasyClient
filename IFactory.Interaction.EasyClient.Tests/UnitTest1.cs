using System.Net;
using System.Net.Sockets;

namespace IFactory.Interaction.EasyClient.Tests;

public class ChannelTests
{
    private sealed class FakeChannel : ChannelBase
    {
        public Action<byte[]>? OnSend;
        public void Deliver(params byte[] data) => ReceiveData(data);

        protected override ValueTask SendCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); OnSend?.Invoke(data.ToArray()); return ValueTask.CompletedTask; }
        public override Task CloseAsync() { EndPendingResponse(); return Task.CompletedTask; }
    }

    [Fact]
    public async Task ImmediateResponseIsRegisteredBeforeSending()
    {
        var channel = new FakeChannel();
        channel.OnSend = _ => channel.Deliver(1, 2);
        Assert.Equal(new byte[] { 1, 2 }, await channel.SendReceiveAsync(new byte[] { 0 }, 1000, 2));
    }

    [Fact]
    public async Task FragmentsAccumulateAndExcessIsDeliveredSeparately()
    {
        var channel = new FakeChannel();
        var extra = new TaskCompletionSource<List<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DataReceiceHandler = data => extra.TrySetResult(data);
        var response = channel.SendReceiveAsync(new byte[] { 0 }, 1000, 3);
        channel.Deliver(1);
        Assert.False(response.IsCompleted);
        channel.Deliver(2, 3, 4);
        Assert.Equal(new byte[] { 1, 2, 3 }, await response);
        Assert.Equal(new byte[] { 4 }, await extra.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CallbackOwnsItsData()
    {
        var channel = new FakeChannel();
        var result = new TaskCompletionSource<List<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DataReceiceHandler = data => result.TrySetResult(data);
        byte[] source = [1, 2, 3];
        channel.Deliver(source);
        Array.Fill(source, (byte)9);
        Assert.Equal(new byte[] { 1, 2, 3 }, await result.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ConcurrentRequestsAreSerialized()
    {
        var channel = new FakeChannel();
        channel.OnSend = data => channel.Deliver(data);
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(i =>
            channel.SendReceiveAsync(new byte[] { (byte)i }, 1000, 1)));
        for (int i = 0; i < results.Length; i++) Assert.Equal((byte)i, Assert.Single(results[i]));
    }

    [Fact]
    public async Task TimeoutDoesNotCompleteTheNextRequest()
    {
        var channel = new FakeChannel();
        await Assert.ThrowsAsync<TimeoutException>(() => channel.SendReceiveAsync(new byte[] { 0 }, 10, 2));
        channel.OnSend = _ => channel.Deliver(7, 8);
        Assert.Equal(new byte[] { 7, 8 }, await channel.SendReceiveAsync(new byte[] { 0 }, 1000, 2));
    }

    [Fact]
    public async Task TcpReceivesFragmentsAndReconnectsAfterPeerClose()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var channel = new TcpClientChannel($"tcp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        try
        {
            using (var peer = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(5)))
            {
                await WaitConnected(channel);
                var response = channel.SendReceiveAsync(new byte[] { 9 }, 3000, 4);
                var request = new byte[1];
                await peer.ReceiveAsync(request.AsMemory(), SocketFlags.None);
                await peer.SendAsync(new byte[] { 1, 2 }.AsMemory(), SocketFlags.None);
                await Task.Delay(50);
                Assert.False(response.IsCompleted);
                await peer.SendAsync(new byte[] { 3, 4 }.AsMemory(), SocketFlags.None);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response);
            }
            using var next = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await WaitConnected(channel);
            await channel.SendAsync(new byte[] { 5 });
            var received = new byte[1];
            Assert.Equal(1, await next.ReceiveAsync(received.AsMemory(), SocketFlags.None));
            Assert.Equal(5, received[0]);
        }
        finally { await channel.CloseAsync(); listener.Stop(); }
        Assert.False(channel.IsConnected);
    }

    [Fact]
    public async Task SendFailurePropagatesAndReleasesRequestGate()
    {
        var channel = new FakeChannel();
        channel.OnSend = _ => throw new IOException("send failed");
        await Assert.ThrowsAsync<IOException>(() => channel.SendReceiveAsync(new byte[] { 1 }, 1000, 1));
        channel.OnSend = data => channel.Deliver(data);
        Assert.Equal(new byte[] { 2 }, await channel.SendReceiveAsync(new byte[] { 2 }, 1000, 1));
    }
    private static async Task WaitConnected(TcpClientChannel channel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!channel.IsConnected) await Task.Delay(10, timeout.Token);
    }
}
