using System.IO.Ports;
using System.Threading.Channels;
namespace IFactory.Interaction.EasyClient.Tests;

public sealed class VirtualSerialFactAttribute : FactAttribute
{
    public VirtualSerialFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EASYCLIENT_SERIAL_TESTS") != "1")
            Skip = "Set EASYCLIENT_SERIAL_TESTS=1 to use the VSPD COM1/COM2 pair.";
    }
}

[CollectionDefinition("VirtualSerial", DisableParallelization = true)]
public sealed class VirtualSerialCollection { }

[Collection("VirtualSerial")]
[Trait("Category", "SerialIntegration")]
public class SerialIntegrationTests
{
    private static string PortA => Environment.GetEnvironmentVariable("EASYCLIENT_SERIAL_A") ?? "COM1";
    private static string PortB => Environment.GetEnvironmentVariable("EASYCLIENT_SERIAL_B") ?? "COM2";
    private static SerialIOChannel Open(string port)
    {
        Assert.Contains(port, SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        return new SerialIOChannel($"sio://{port}/?BaudRate=115200&DataBits=8&Parity=None&StopBits=One");
    }
    private static Channel<byte> Capture(SerialIOChannel channel)
    {
        var bytes = Channel.CreateUnbounded<byte>();
        channel.DataReceiceHandler = data => { foreach (byte value in data) bytes.Writer.TryWrite(value); };
        return bytes;
    }
    private static async Task<byte[]> Read(Channel<byte> channel, int length)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = new byte[length];
        for (int i = 0; i < length; i++) result[i] = await channel.Reader.ReadAsync(timeout.Token);
        return result;
    }
    private static async Task Close(SerialIOChannel channel)
    {
        await channel.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        // VSPD 6.9 may briefly retain the device after CloseHandle completes.
        await Task.Delay(100);
    }

    [VirtualSerialFact]
    public async Task CancellationBeforeAndAfterWriteKeepsRealPortUsable()
    {
        var a = Open(PortA);
        try
        {
            var b = Open(PortB);
            try
            {
                var received = Capture(b);
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.SendAsync(new byte[] { 99 }, cancelled.Token).AsTask());
                    Assert.False(a.IsClosed);
                }
                for (int i = 0; i < 20; i++)
                {
                    using var completed = new CancellationTokenSource();
                    await a.SendAsync(new byte[] { (byte)i }, completed.Token);
                    completed.Cancel();
                    Assert.Equal(new byte[] { (byte)i }, await Read(received, 1));
                    Assert.True(a.IsConnected);
                    Assert.False(a.IsClosed);
                }
                await a.CloseAsync();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => a.SendAsync(new byte[] { 1 }).AsTask());
            }
            finally { await Close(b); }
        }
        finally { await Close(a); }
    }
    [VirtualSerialFact]
    public async Task RealClientResetReopensWithoutTestCleanupDelay()
    {
        var client = new EasyClient($"sio://{PortA}/?BaudRate=115200");
        var peer = Open(PortB);
        try
        {
            var received = Capture(peer);
            for (int i = 0; i < 10; i++)
            {
                await client.ResetChannelAsync();
                await client.SendAsync(new byte[] { (byte)i });
                Assert.Equal(new byte[] { (byte)i }, await Read(received, 1));
                Assert.Null(client.LastResetError);
            }
        }
        finally
        {
            await client.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Close(peer);
        }
    }
    [VirtualSerialFact]
    public async Task BinaryDataTravelsInBothDirections()
    {
        var a = Open(PortA);
        try
        {
            var b = Open(PortB);
            try
            {
                var atA = Capture(a);
                var atB = Capture(b);
                var forward = Enumerable.Range(0, 16384).Select(i => (byte)i).ToArray();
                var reverse = forward.Reverse().ToArray();
                await a.SendAsync(forward);
                Assert.Equal(forward, await Read(atB, forward.Length));
                await b.SendAsync(reverse);
                Assert.Equal(reverse, await Read(atA, reverse.Length));
            }
            finally { await Close(b); }
        }
        finally { await Close(a); }
    }

    [VirtualSerialFact]
    public async Task FragmentedResponsesAndRepeatedRequests()
    {
        var a = Open(PortA);
        try
        {
            var b = Open(PortB);
            try
            {
                var requests = Capture(b);
                for (int i = 0; i < 20; i++)
                {
                    byte id = (byte)i;
                    var response = a.SendReceiveAsync(new byte[] { id }, 5000, 4);
                    Assert.Equal(new byte[] { id }, await Read(requests, 1));
                    await b.SendAsync(new byte[] { id, 1 });
                    await Task.Delay(20);
                    Assert.False(response.IsCompleted);
                    await b.SendAsync(new byte[] { 2, 3 });
                    Assert.Equal(new byte[] { id, 1, 2, 3 }, await response);
                }
            }
            finally { await Close(b); }
        }
        finally { await Close(a); }
    }

    [VirtualSerialFact]
    public async Task TimeoutCancellationAndPendingClose()
    {
        var a = Open(PortA);
        try
        {
            var b = Open(PortB);
            try
            {
                var requests = Capture(b);
                await Assert.ThrowsAsync<TimeoutException>(() => a.SendReceiveAsync(new byte[] { 1 }, 100, 1));
                Assert.Equal(new byte[] { 1 }, await Read(requests, 1));
                using var cancellation = new CancellationTokenSource();
                var cancelled = a.SendReceiveAsync(new byte[] { 2 }, -1, 1, cancellation.Token);
                Assert.Equal(new byte[] { 2 }, await Read(requests, 1));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
                var pending = a.SendReceiveAsync(new byte[] { 3 }, -1, 1);
                Assert.Equal(new byte[] { 3 }, await Read(requests, 1));
                await Close(a);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
                Assert.True(a.IsClosed);
                Assert.False(a.IsConnected);
            }
            finally { await Close(b); }
        }
        finally { await Close(a); }
    }

    [VirtualSerialFact]
    public async Task PortsCanBeReopenedAfterClosingIdleReceivers()
    {
        for (int i = 0; i < 3; i++)
        {
            var a = Open(PortA);
            try
            {
                var b = Open(PortB);
                try
                {
                    var received = Capture(b);
                    await a.SendAsync(new byte[] { (byte)i });
                    Assert.Equal(new byte[] { (byte)i }, await Read(received, 1));
                }
                finally { await Close(b); }
            }
            finally { await Close(a); }
        }
    }
}
