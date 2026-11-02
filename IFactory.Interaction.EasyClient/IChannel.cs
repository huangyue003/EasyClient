using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers;

namespace IFactory.Interaction.EasyClient
{
    public interface IChannel : IAsyncDisposable
    {
        string Uri { get; }
        Exception? LastError => null;
        DateTimeOffset? ErrorTimestamp => null;
        Microsoft.Extensions.Logging.ILogger? Logger { get => null; set { } }
        long DroppedChunks => 0;
        long DroppedBytes => 0;
        Action<List<byte>>? DataReceiceHandler { get; set; }
        bool IsClosed { get; }
        bool IsConnected { get; }

        ValueTask SendAsync(ReadOnlyMemory<byte> data);
        ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
        Task<List<byte>> SendReceiveAsync(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength, CancellationToken cancellationToken);
        ValueTask SendAsync(string data);
        Task<List<byte>> SendReceiveAsync(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength = 0);
        Task<string> SendReceiveAsync(string data, int millisecondsTimeout, int expectReceiveLength = 0);
        /// <summary>Closes transport; subsequent sends throw ObjectDisposedException.</summary>
        Task CloseAsync();
        ValueTask IAsyncDisposable.DisposeAsync() => new(CloseAsync());
        void Send(ReadOnlyMemory<byte> data);
        void Send(string data);
        List<byte> SendReceive(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength = 0);
        string SendReceive(string data, int millisecondsTimeout, int expectReceiveLength = 0);
    }
}
