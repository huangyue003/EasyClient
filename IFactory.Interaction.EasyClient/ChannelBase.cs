using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers;

namespace IFactory.Interaction.EasyClient
{
    public abstract class ChannelBase : IChannel
    {
        public string Uri { get; set; }
        public bool IsClosed { get; set; } = true;
        protected ILogger? logger;
        protected AutoResetEvent AutoResetEvent { get; set; } = new AutoResetEvent(false);
        protected bool IsWaitingReceice = false;
        protected List<byte> receiveBuffer = new List<byte>();
        protected int _expectReceiveLength = 0;
        protected int _millisecondsTimeout = 100;
        private SemaphoreSlim _sendReceiveLock = new SemaphoreSlim(1);
        public bool IsConnected { get; set; } = false;
        public Action<List<byte>>? DataReceiceHandler { get; set; }

        private List<byte> Receive(int millisecondsTimeout, int expectReceiveLength)
        {
            IsWaitingReceice = true;
            _expectReceiveLength = expectReceiveLength;
            _millisecondsTimeout = millisecondsTimeout;
            AutoResetEvent.WaitOne(millisecondsTimeout);
            IsWaitingReceice = false;
            return receiveBuffer.ToList();
        }
        public abstract Task CloseAsync();
        public abstract void Send(ReadOnlyMemory<byte> data);
        public void Send(string data)
        {
            var binaryData = Encoding.UTF8.GetBytes(data, 0, data.Length);
            Send(binaryData);
        }
        public abstract ValueTask SendAsync(ReadOnlyMemory<byte> data);
        public ValueTask SendAsync(string data)
        {
            var binaryData = Encoding.UTF8.GetBytes(data, 0, data.Length);
            return SendAsync(binaryData);
        }

        public async Task<List<byte>> SendReceiveAsync(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength = 0)
        {
            try
            {
                _sendReceiveLock.Wait();
                receiveBuffer.Clear();
                await SendAsync(data);
                return Receive(millisecondsTimeout, expectReceiveLength);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Exception happened in SendReceiveAsync.");
            }
            finally
            {
                _sendReceiveLock.Release();
            }
            return new List<byte>();
        }
        public async Task<string> SendReceiveAsync(string data, int millisecondsTimeout, int expectReceiveLength)
        {
            var binaryData = Encoding.UTF8.GetBytes(data, 0, data.Length);
            var receive = await SendReceiveAsync(binaryData, millisecondsTimeout, expectReceiveLength);
            return Encoding.UTF8.GetString(receive.ToArray());
        }

        public List<byte> SendReceive(ReadOnlyMemory<byte> data, int millisecondsTimeout, int expectReceiveLength)
        {
            try
            {
                _sendReceiveLock.Wait();
                receiveBuffer.Clear();
                Send(data);
                return Receive(millisecondsTimeout, expectReceiveLength);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Exception happened in SendReceiveAsync.");
            }
            finally
            {
                _sendReceiveLock.Release();
            }
            return new List<byte>();
        }

        public string SendReceive(string data, int millisecondsTimeout, int expectReceiveLength)
        {
            var binaryData = Encoding.UTF8.GetBytes(data, 0, data.Length);
            var receive = SendReceive(binaryData, millisecondsTimeout, expectReceiveLength);
            return Encoding.UTF8.GetString(receive.ToArray());
        }
    }
}
