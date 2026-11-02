using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
namespace IFactory.Interaction.EasyClient.Tests;
public class OptionsTests
{
    [Fact]
    public void SerialDefaultsAndTrailingSeparatorAreAccepted()
    {
        var options = new SerialIOEndPoint("sio://COM2/?BaudRate=9600&");
        Assert.Equal(StopBits.One, options.StopBits);
        Assert.Equal(8, options.Databits);
        Assert.Equal(9600, options.BaudRate);
    }
    [Fact]
    public void SerialOptionsAreDecodedAndCaseInsensitive()
    {
        var options = new SerialIOEndPoint("sio://COM2/?baudrate=%39%36%30%30&stopbits=Two&Parity=Even&DtrEnable=true&WriteTimeout=500");
        Assert.Equal(StopBits.Two, options.StopBits);
        Assert.Equal(Parity.Even, options.Parity);
        Assert.True(options.DtrEnable);
        Assert.Equal(500, options.WirteTimeout);
    }
    [Theory]
    [InlineData("")]
    [InlineData("BaudRate=0")]
    [InlineData("BaudRate=9600&DataBits=264")]
    [InlineData("BaudRate=9600&StopBits=None")]
    [InlineData("BaudRate=9600&Parity=999")]
    [InlineData("BaudRate=9600&WriteTimeout=0")]
    [InlineData("BaudRate=9600&RtsEnable=oops")]
    [InlineData("BaudRate=9600&flag")]
    [InlineData("BaudRate=9600&baudrate=4800")]
    public void InvalidSerialOptionsFailClearly(string query)
        => Assert.Throws<ArgumentException>(() => new SerialIOEndPoint("sio://COM2/?" + query));

    [Theory]
    [InlineData("ReceiceBuffer=0")]
    [InlineData("ReceiceBuffer=-1")]
    [InlineData("ReceiceBuffer=oops")]
    [InlineData("flag")]
    [InlineData("ReceiceBuffer=10&receicebuffer=20")]
    public void InvalidTcpOptionsFailBeforeConnecting(string query)
        => Assert.Throws<ArgumentException>(() => new TcpClientChannel("tcp://127.0.0.1:12345/?" + query));

    [Fact]
    public void UdpRequiresRemoteEndpoint()
        => Assert.Throws<ArgumentException>(() => new UdpChannel("udp://127.0.0.1:0"));

    [Fact]
    public async Task UdpRoundTripAndClosedSendErrorsAreVisible()
    {
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var channel = new UdpChannel($"udp://127.0.0.1:0/?RemoteEndPoint={peer.LocalEndPoint}&");
        try
        {
            var response = channel.SendReceiveAsync(new byte[] { 1 }, 3000, 2);
            var buffer = new byte[10];
            var received = await peer.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0)).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await peer.SendToAsync(new byte[] { 2, 3 }.AsMemory(), SocketFlags.None, received.RemoteEndPoint);
            Assert.Equal(new byte[] { 2, 3 }, await response);
            var pending = channel.SendReceiveAsync(new byte[] { 4 }, 3000, 1);
            await channel.CloseAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.SendAsync(new byte[] { 5 }).AsTask());
        }
        finally { await channel.CloseAsync(); }
    }
}
