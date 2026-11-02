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
        public EasyClient(string uri)
        {
            Uri = uri;
            SetChannel(_channelCreator.CreateChannel(uri));
        }
        IChannelCreator _channelCreator = new ChannelCreator();
        public IChannel Channel { get; private set; } = null!;
        public Action<List<byte>>? DataReceiceHandler { get; set; }
        public void OnDataReceived(List<byte> data)
        {
            DataReceiceHandler?.Invoke(data);
        }
        public string Uri { get; private set; }

        private void SetChannel(IChannel channel)
        {
            Channel = channel;
            channel.DataReceiceHandler += OnDataReceived;
        }
        public Task CloseAsync()
        {
            return Channel.CloseAsync();
        }

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
            //if (Uri.Equals(uri))
            //    return;
            Channel.DataReceiceHandler -= OnDataReceived;
            await Channel.CloseAsync().ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(uri))
                Uri = uri;
            SetChannel(_channelCreator.CreateChannel(Uri));
        }
    }
}
