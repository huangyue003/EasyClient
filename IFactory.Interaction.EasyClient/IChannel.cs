using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers;

namespace IFactory.Interaction.EasyClient
{
    public interface IChannel
    {
        string Uri { get; }
        Action<List<byte>>? DataReceiceHandler { get; set; }
        bool IsClosed { get; set; }
        bool IsConnected { get; set; }

        ValueTask SendAsync(ReadOnlyMemory<byte> data);
        ValueTask SendAsync(string data);
        Task<List<byte>> SendReceiveAsync(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength);
        Task<string> SendReceiveAsync(string data, int millisecondsTimeout, int expectReceiveLength);
        Task CloseAsync();
        void Send(ReadOnlyMemory<byte> data);
        void Send(string data);
        List<byte> SendReceive(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength);
        string SendReceive(string data, int millisecondsTimeout, int expectReceiveLength);
    }
}
