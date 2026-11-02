using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers;

namespace IFactory.Interaction.EasyClient
{
    public class EasyClient : IEasyClient
    {
        public EasyClient(string uri) : this(uri, new ChannelCreator()) { }
        public EasyClient(string uri, IChannelCreator channelCreator)
        {
            _channelCreator = channelCreator ?? throw new ArgumentNullException(nameof(channelCreator));
            Uri = uri;
            SetChannel(_channelCreator.CreateChannel(uri));
        }
        private readonly IChannelCreator _channelCreator;
        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private int _closed;
        public IChannel Channel { get; private set; } = null!;
        public Action<List<byte>>? DataReceiceHandler { get; set; }
        public void OnDataReceived(List<byte> data)
        {
            DataReceiceHandler?.Invoke(data);
        }
        public Exception? LastResetError { get; private set; }
        public string Uri { get; private set; }

        private void SetChannel(IChannel channel)
        {
            Channel = channel;
            channel.DataReceiceHandler += OnDataReceived;
        }
        public async Task CloseAsync()
        {
            Interlocked.Exchange(ref _closed, 1);
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                Channel.DataReceiceHandler -= OnDataReceived;
                await Channel.CloseAsync().ConfigureAwait(false);
            }
            finally { _lifecycleGate.Release(); }
        }
        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            => Channel.SendAsync(data, cancellationToken);
        public Task<List<byte>> SendReceiveAsync(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength = 0, CancellationToken cancellationToken = default)
            => Channel.SendReceiveAsync(data, millisecondsTimeout, expectReceiveLength, cancellationToken);
        public void Send(ReadOnlyMemory<byte> data) => Channel.Send(data);
        public ValueTask SendAsync(List<byte> data) => Channel.SendAsync(new ReadOnlyMemory<byte>(data.ToArray()));
        public ValueTask SendAsync(string data) => Channel.SendAsync(data);
        public void Send(List<byte> data) => Channel.Send(new ReadOnlyMemory<byte>(data.ToArray()));
        public void Send(string data) => Channel.Send(data);

        public Task<List<byte>> SendReceiveAsync(List<byte> data, int millisecondsTimeout, int expectReceiveLength = 0)
        => Channel.SendReceiveAsync(new ReadOnlyMemory<byte>(data.ToArray()), millisecondsTimeout, expectReceiveLength);
        public Task<string> SendReceiveAsync(string data, int millisecondsTimeout, int expectReceiveLength = 0)
        => Channel.SendReceiveAsync(data, millisecondsTimeout, expectReceiveLength);

        public List<byte> SendReceive(List<byte> data, int millisecondsTimeout, int expectReceiveLength = 0)
       => Channel.SendReceive(new ReadOnlyMemory<byte>(data.ToArray()), millisecondsTimeout, expectReceiveLength);

        public string SendReceive(string data, int millisecondsTimeout, int expectReceiveLength = 0)
        => Channel.SendReceive(data, millisecondsTimeout, expectReceiveLength);

        public async Task ResetChannelAsync(string uri = "")
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
                string nextUri = string.IsNullOrEmpty(uri) ? Uri : uri;
                if (_channelCreator is ChannelCreator) ChannelOptions.Validate(nextUri);
                Channel.DataReceiceHandler -= OnDataReceived;
                await Channel.CloseAsync().ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
                IChannel next;
                int attempt = 0;
                while (true)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
                    try { next = _channelCreator.CreateChannel(nextUri); break; }
                    catch (Exception ex) when (new Uri(nextUri).Scheme == "sio" && attempt++ < 5 && ex is UnauthorizedAccessException or IOException)
                    { await Task.Delay(100).ConfigureAwait(false); }
                }
                if (Volatile.Read(ref _closed) != 0)
                {
                    await next.CloseAsync().ConfigureAwait(false);
                    throw new ObjectDisposedException(nameof(EasyClient));
                }
                Uri = nextUri;
                SetChannel(next);
                LastResetError = null;
            }
            catch (Exception ex) { LastResetError = ex; throw; }
            finally { _lifecycleGate.Release(); }
        }
    }
}
