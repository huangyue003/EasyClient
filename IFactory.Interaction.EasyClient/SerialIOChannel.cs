using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers;

namespace IFactory.Interaction.EasyClient
{
    public class SerialIOChannel : ChannelBase
    {
        private SerialPort serialIO;

        public SerialIOEndPoint SioEndPoint { get; set; }
        private List<byte> _oneReceiveBuffer = new List<byte>();
        /// <summary>
        /// 
        /// </summary>
        /// <param name="uri">sio://COM2/?BaudRate=9600&Parity=None&StopBits=1&DataBits=8&RtsEnable=false</param>
        public SerialIOChannel(string uri)
        {
            Uri = uri;
            SioEndPoint = new SerialIOEndPoint(uri);
            serialIO = new SerialPort(SioEndPoint.PortName, SioEndPoint.BaudRate, SioEndPoint.Parity, SioEndPoint.Databits);
            serialIO.RtsEnable = SioEndPoint.RtsEnable; // 兼容性
            serialIO.DtrEnable = SioEndPoint.DtrEnable; // 兼容性
            if (SioEndPoint.WirteTimeout != default) serialIO.WriteTimeout = SioEndPoint.WirteTimeout;
            OpenPort();
        }
        CancellationTokenSource _cts;
        Task _monitorInBufferTask;
        private bool IsMonitorStarted = false;
        private async Task MonitorInBufferAsync()
        {
            if (IsMonitorStarted) return;

            try
            {
                IsMonitorStarted = true;
                _cts = new CancellationTokenSource();
                var bufferPool = ArrayPool<byte>.Shared;
                while (true)
                {
                    if (_cts.Token.IsCancellationRequested)
                        break;
                    var memory = bufferPool.Rent(1024 * 6);
                    try
                    {
                        if (!serialIO.IsOpen) break;
                        var count = await serialIO.BaseStream.ReadAsync(memory, 0, 1024 * 6);
                        if (count < 1)
                            continue;
                        _oneReceiveBuffer.AddRange(memory.Take(count));
                        CheckIsReceiveComplete();
                    }
                    catch (Exception ex)
                    {
                        System.Threading.Thread.Sleep(30);
                        continue;
                    }
                    finally
                    {
                        bufferPool.Return(memory);
                    }

                }
            }
            finally
            {
                IsMonitorStarted = false;
            }


        }

        bool IsReceiving = false;
        private void CheckIsReceiveComplete()
        {
            if (IsReceiving)
                return;
            IsReceiving = true;
            Task.Run(() =>
            {
                try
                {
                    if (_expectReceiveLength > 0)
                    {
                        if (_expectReceiveLength == 100000)//新锂航蓝牙测试用
                        {
                            int time = _millisecondsTimeout - 1000;
                            if (time <= 0) time = 100;
                            Thread.Sleep(time);
                            HandleData();
                        }
                        else
                        {
                            int time = 100;
                            do
                            {
                                if (_oneReceiveBuffer.Count >= _expectReceiveLength)
                                {
                                    HandleData();
                                    return;
                                }
                                Thread.Sleep(1);
                                time--;
                            }
                            while (time > 0);
                            HandleData();
                        }

                    }
                    else
                    {
                        Thread.Sleep(150);
                        HandleData();
                    }
                }
                finally
                {
                    IsReceiving = false;
                }
            });

        }
        private void HandleData()
        {
            if (IsWaitingReceice)
            {
                receiveBuffer.AddRange(_oneReceiveBuffer);
                _oneReceiveBuffer.Clear();
                AutoResetEvent.Set();
            }
            else
            {
                var buffer = _oneReceiveBuffer.ToList();
                _oneReceiveBuffer.Clear();
                Task.Run(() => { DataReceiceHandler?.Invoke(buffer); });
            }
        }
        public override void Send(ReadOnlyMemory<byte> data)
        {
            try
            {
                if (serialIO == null || !serialIO.IsOpen) return;
                serialIO.BaseStream.Write(data.Span);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Exception happened in Send.{SioEndPoint}", SioEndPoint);
            }
        }
        public override async ValueTask SendAsync(ReadOnlyMemory<byte> data)
        {
            if (serialIO == null || !serialIO.IsOpen) return;
            try
            {
                await serialIO.BaseStream.WriteAsync(data.ToArray());
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Exception happened in SendAsync.{SioEndPoint}", SioEndPoint);
            }
        }
        public override async Task CloseAsync()
        {
            _cts?.Cancel();
            if (serialIO != null)
            {
                //if (serialIO.IsOpen)
                try
                {
                    serialIO.Close();
                }
                catch
                {

                }
                serialIO.Dispose();
            }
            if (_monitorInBufferTask != null)
                await _monitorInBufferTask.ConfigureAwait(false);
            else
                await Task.Yield();
            IsClosed = true;
        }
        private void OpenPort()
        {
            int num = 3;
            while (num-- > 0)
            {
                try
                {
                    if (serialIO != null && !serialIO.IsOpen)
                    {
                        serialIO.Open();
                        _monitorInBufferTask = MonitorInBufferAsync();
                        IsClosed = false;
                    }
                    break;
                }
                catch (Exception ex)
                {
                    Thread.Sleep(30);
                    logger?.LogError(ex, "Exception happened in OpenPort.{SioEndPoint}", SioEndPoint);
                }
            }

        }


    }
}
