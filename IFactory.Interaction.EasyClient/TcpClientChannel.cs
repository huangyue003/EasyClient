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

namespace IFactory.Interaction.EasyClient
{
    public class TcpClientChannel : ChannelBase
    {

        /// <summary>
        /// 
        /// </summary>
        /// <param name="uri">format【tcp://127.0.0.1:8888/?RemoteEndPoint=127.0.0.1:9999&ReceiceBuffer=4096】</param>
        public TcpClientChannel(string uri)
        {
            Uri = uri;
            var _uri = new Uri(uri);
            _endPoint = IPEndPoint.Parse(_uri.Authority);
            var query = _uri.Query.TrimStart('?')
                .Split('&')
                .Select(x => x.Split('='))
                .ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
            if (query.TryGetValue("ReceiceBuffer", out var stringReceiveBuffer) && !string.IsNullOrEmpty(stringReceiveBuffer))
                int.TryParse(stringReceiveBuffer, out bufferSize);
            IsClosed = false;
            StartConnectAsync(_cancellationTokenSource.Token);
            StartReceive(_cancellationTokenSource.Token);
        }
        private Socket _socket;
        private IPEndPoint _endPoint;
        int bufferSize = 1024 * 6;
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private async void StartConnectAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                if (!IsConnected)
                {
                    try
                    {
                        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        await Task.Delay(1);
                        await _socket.ConnectAsync(_endPoint, cancellationToken).ConfigureAwait(false);
                        IsConnected = true;
                        continue;
                    }
                    catch (Exception ex)
                    {

                    }
                }
                Thread.Sleep(1000);
            }
        }
        private async void StartReceive(CancellationToken cancellationToken)
        {
            var bufferPool = ArrayPool<byte>.Shared;
            await Task.Delay(1);
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                var buffer = bufferPool.Rent(bufferSize);
                try
                {
                    if (!IsConnected)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }
                    //await Task.Delay(1);
                    var receive = await _socket.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, _endPoint, cancellationToken).ConfigureAwait(false);
                    if (receive.ReceivedBytes == 0)
                    {
                        IsConnected = false;
                        continue;
                    }
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
        public async override ValueTask SendAsync(ReadOnlyMemory<byte> data)
        {
            try
            {
                receiveBuffer.Clear();
                await _socket.SendAsync(data, SocketFlags.None);
            }
            catch (Exception ex)
            {
                IsConnected = false;
                logger?.LogError(ex, "Exception happened in UdpChannel SendAsync method.{uri}", Uri);
            }
        }

        public override void Send(ReadOnlyMemory<byte> data)
        {
            try
            {
                receiveBuffer.Clear();
                _socket.Send(data.Span, SocketFlags.None);
            }
            catch (Exception ex)
            {
                IsConnected = false;
                logger?.LogError(ex, "Exception happened in UdpChannel Send method.{uri}", Uri);
            }
        }
    }
}
