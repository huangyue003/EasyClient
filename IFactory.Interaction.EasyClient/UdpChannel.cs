using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.NetworkInformation;

namespace IFactory.Interaction.EasyClient
{
    public class UdpChannel : ChannelBase
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="uri">format【udp://127.0.0.1:8888/?RemoteEndPoint=127.0.0.1:9999&ReceiceBuffer=4096】</param>
        public UdpChannel(string uri)
        {
            Uri = uri;
            var _uri = new Uri(uri);

            var query = _uri.Query.TrimStart('?')
              .Split('&')
              .Select(x => x.Split('='))
              .ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
            if (query.TryGetValue("RemoteEndPoint", out var stringRemoteEndPoint) && !string.IsNullOrEmpty(stringRemoteEndPoint))
                remoteEndPoint = IPEndPoint.Parse(stringRemoteEndPoint);
            if (query.TryGetValue("ReceiceBuffer", out var stringReceiveBuffer) && !string.IsNullOrEmpty(stringReceiveBuffer))
                int.TryParse(stringReceiveBuffer, out bufferSize);
            localEndPoint = IPEndPoint.Parse(_uri.Authority);

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(localEndPoint);
            uint IOC_IN = 0x80000000; uint IOC_VENDOR = 0x18000000;
            uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
            _socket.IOControl((int)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);
            IsClosed = false;
            StartReceive(_cancellationTokenSource.Token);
        }
        private Socket _socket;
        private IPEndPoint localEndPoint;
        private IPEndPoint remoteEndPoint;
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        int bufferSize = 1024 * 6;
        private bool isRunning = true;

        private async void StartReceive(CancellationToken cancellationToken)
        {

            var bufferPool = ArrayPool<byte>.Shared;
            while (isRunning)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                var buffer = bufferPool.Rent(bufferSize);
                try
                {

                    var receive = await _socket.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, remoteEndPoint, cancellationToken).ConfigureAwait(false);

                    if (IsWaitingReceice)
                    {
                        receiveBuffer.AddRange(buffer.Take(receive.ReceivedBytes));
                        AutoResetEvent.Set();
                    }
                    else
                    {
                        _ = Task.Run(() =>
                        {
                            DataReceiceHandler?.Invoke(buffer.Take(receive.ReceivedBytes).ToList());
                        });
                    }

                }
                catch (Exception ex)
                {
                    await CloseAsync();
                    logger?.LogError(ex, "Exception happened in UdpChannel StartReceice method.{uri}", Uri);
                }
                finally
                {
                    bufferPool.Return(buffer);
                }

            }
        }

        public override Task CloseAsync()
        {
            try
            {
                isRunning = false;
                _socket?.Close();
                _socket?.Dispose();
                _cancellationTokenSource.Cancel();
                IsClosed = true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Exception happened in UdpChannel CloseAsync method.{uri}", Uri);

            }
            return Task.CompletedTask;
        }

        public override async ValueTask SendAsync(ReadOnlyMemory<byte> data)
        {
            try
            {
                receiveBuffer.Clear();
                await _socket.SendToAsync(data, SocketFlags.None, remoteEndPoint);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Exception happened in UdpChannel SendAsync method.{uri}", Uri);
            }

        }

        public override void Send(ReadOnlyMemory<byte> data)
        {
            try
            {
                receiveBuffer.Clear();
                 _socket.SendTo(data.Span, SocketFlags.None, remoteEndPoint);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Exception happened in UdpChannel Send method.{uri}", Uri);
            }
        }
    }
}
