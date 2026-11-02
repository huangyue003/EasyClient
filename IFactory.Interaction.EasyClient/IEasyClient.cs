using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers;

namespace IFactory.Interaction.EasyClient
{
    public interface IEasyClient : IAsyncDisposable
    {
        IChannel Channel { get; }
        Exception? LastResetError => null;
        Task ResetChannelAsync(string uri);

        string Uri { get; }
        Action<List<byte>>? DataReceiceHandler { get; set; }

        ValueTask SendAsync(List<byte> data);
        ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
        Task<List<byte>> SendReceiveAsync(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength = 0, CancellationToken cancellationToken = default);
        void Send(ReadOnlyMemory<byte> data);
        ValueTask SendAsync(string data);
        Task<List<byte>> SendReceiveAsync(List<byte> data, int millisecondsTimeout, int expectReceiveLength = 0);
        Task<string> SendReceiveAsync(string data, int millisecondsTimeout, int expectReceiveLength = 0);
        /// <summary>Permanently closes this client; subsequent sends and resets throw ObjectDisposedException.</summary>
        Task CloseAsync();
        ValueTask IAsyncDisposable.DisposeAsync() => new(CloseAsync());
        void Send(List<byte> data);
        void Send(string data);
        List<byte> SendReceive(List<byte> data, int millisecondsTimeout, int expectReceiveLength = 0);
        string SendReceive(string data, int millisecondsTimeout, int expectReceiveLength = 0);
    }
}
