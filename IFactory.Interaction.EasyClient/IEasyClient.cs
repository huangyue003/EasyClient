using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers;

namespace IFactory.Interaction.EasyClient
{
    public interface IEasyClient
    {
        IChannel Channel { get; }
        Task ResetChannelAsync(string uri);

        string Uri { get; }
        Action<List<byte>>? DataReceiceHandler { get; set; }

        ValueTask SendAsync(List<byte> data);
        ValueTask SendAsync(string data);
        Task<List<byte>> SendReceiveAsync(List<byte> data, int millisecondsTimeout, int expectReceiveLength);
        Task<string> SendReceiveAsync(string data, int millisecondsTimeout, int expectReceiveLength);
        Task CloseAsync();
        void Send(List<byte> data);
        void Send(string data);
        List<byte> SendReceive(List<byte> data, int millisecondsTimeout, int expectReceiveLength);
        string SendReceive(string data, int millisecondsTimeout, int expectReceiveLength);
    }
}
