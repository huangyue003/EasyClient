using System.Text;
using System.Net;
using System.Net.Sockets;
namespace IFactory.Interaction.EasyClient.Tests;
public class PublicApiTests
{
    private sealed class EchoChannel : ChannelBase
    {
        public EchoChannel() { IsClosed = false; IsConnected = true; }
        public byte[]? LastSent;
        public void Deliver(byte[] bytes) => ReceiveData(bytes);
        protected override ValueTask SendCoreAsync(ReadOnlyMemory<byte> data, CancellationToken token)
        { token.ThrowIfCancellationRequested(); LastSent = data.ToArray(); ReceiveData(data.Span); return ValueTask.CompletedTask; }
        public override Task CloseAsync()
        { IsClosed = true; IsConnected = false; StopCallbacks(); FailPendingResponse(new ObjectDisposedException("Echo")); return Task.CompletedTask; }
    }
    private sealed class Factory : IChannelCreator
    {
        public EchoChannel Channel = new();
        public bool FailCreation;
        public IChannel CreateChannel(string uri) => FailCreation ? throw new IOException("Injected open failure") : Channel;
    }
    [Fact]
    public async Task PublicRequestOverloadsPreserveUtf8AndBinaryPayloads()
    {
        var factory = new Factory();
        await using var client = new EasyClient("custom://echo", factory);
        string text = "测试🙂";
        int length = Encoding.UTF8.GetByteCount(text);
        Assert.Equal(text, await client.SendReceiveAsync(text, 1000, length));
        Assert.Equal(text, client.SendReceive(text, 1000, length));
        List<byte> bytes = [0, 128, 255];
        Assert.Equal(bytes, await client.SendReceiveAsync(bytes, 1000, bytes.Count));
        Assert.Equal(bytes, client.SendReceive(bytes, 1000, bytes.Count));
        Assert.Equal(bytes, await client.SendReceiveAsync(bytes.ToArray().AsMemory(), 1000, bytes.Count));
        IEasyClient api = client;
        Assert.Equal(bytes, await api.SendReceiveAsync(bytes, 1000));
    }
    [Fact]
    public async Task PlainSendOverloadsAndCallbackForwardingWork()
    {
        var factory = new Factory();
        await using var client = new EasyClient("custom://echo", factory);
        var messages = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        client.DataReceiceHandler = data => messages.Writer.TryWrite(data.ToArray());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.SendAsync("你好");
        Assert.Equal(Encoding.UTF8.GetBytes("你好"), await messages.Reader.ReadAsync(timeout.Token));
        client.Send("hello");
        Assert.Equal(Encoding.UTF8.GetBytes("hello"), await messages.Reader.ReadAsync(timeout.Token));
        await client.SendAsync(new List<byte> { 1, 2 });
        Assert.Equal(new byte[] { 1, 2 }, await messages.Reader.ReadAsync(timeout.Token));
        client.Send(new List<byte> { 3, 4 });
        Assert.Equal(new byte[] { 3, 4 }, await messages.Reader.ReadAsync(timeout.Token));
        client.Send(new byte[] { 5 }.AsMemory());
        Assert.Equal(new byte[] { 5 }, await messages.Reader.ReadAsync(timeout.Token));
    }
    [Fact]
    public async Task CallbackFailureDoesNotStopFollowingMessages()
    {
        await using var channel = new EchoChannel();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DataReceiceHandler = bytes =>
        { if (bytes[0] == 1) throw new InvalidOperationException("Consumer failure"); delivered.TrySetResult(); };
        channel.Deliver([1]); channel.Deliver([2]);
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    [Fact]
    public async Task OpenFailureIsVisibleAndLaterResetRecovers()
    {
        var factory = new Factory();
        await using var client = new EasyClient("custom://echo", factory);
        var old = client.Channel;
        factory.FailCreation = true;
        await Assert.ThrowsAsync<IOException>(() => client.ResetChannelAsync("custom://new"));
        Assert.True(old.IsClosed);
        Assert.IsType<IOException>(client.LastResetError);
        factory.FailCreation = false; factory.Channel = new EchoChannel();
        await client.ResetChannelAsync("custom://new");
        Assert.Equal("custom://new", client.Uri); Assert.Null(client.LastResetError);
        Assert.Equal(new byte[] { 7 }, await client.SendReceiveAsync(new byte[] { 7 }, 1000, 1));
    }
    [Theory]
    [InlineData("not a uri")]
    [InlineData("http://127.0.0.1:1")]
    [InlineData("tcp://user@127.0.0.1:1")]
    [InlineData("udp://127.0.0.1:0/?RemoteEndPoint=[::1]:20")]
    [InlineData("udp://127.0.0.1:0/?RemoteEndPoint=invalid")]
    [InlineData("tcp://127.0.0.1:1/?ReceiceBuffer=1048577")]
    [InlineData("tcp://127.0.0.1:1/?ReconnectInitialMs=0")]
    public void FactoryRejectsInvalidConfigurationBeforeOpening(string uri)
        => Assert.Throws<ArgumentException>(() => new ChannelCreator().CreateChannel(uri));
    [Theory]
    [InlineData(1)]
    [InlineData(1048576)]
    public void ReceiveBufferBoundaryValuesAreAccepted(int size)
        => ChannelOptions.Validate($"tcp://127.0.0.1:1/?ReceiceBuffer={size}");
    [Fact]
    public async Task TcpResetByPeerRecoversAndRetainsFailureDiagnostic()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        await using var channel = new TcpClientChannel($"tcp://{listener.LocalEndpoint}/?ReconnectInitialMs=10&ReconnectMaxMs=20");
        try
        {
            using (var peer = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(5)))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!channel.IsConnected) await Task.Delay(5, timeout.Token);
                peer.LingerState = new LingerOption(true, 0);
            }
            using var replacement = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var connected = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!channel.IsConnected) await Task.Delay(5, connected.Token);
            Assert.NotNull(channel.LastError); Assert.NotNull(channel.ErrorTimestamp);
            Assert.False(channel.IsClosed);
            await channel.SendAsync(new byte[] { 42 });
            var bytes = new byte[1];
            Assert.Equal(1, await replacement.ReceiveAsync(bytes.AsMemory(), SocketFlags.None));
            Assert.Equal(42, bytes[0]);
        }
        finally { listener.Stop(); }
    }
}
