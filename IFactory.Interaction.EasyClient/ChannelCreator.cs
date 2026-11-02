using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IFactory.Interaction.EasyClient
{
    public class ChannelCreator : IChannelCreator
    {
        public void Validate(string uri) => ChannelOptions.Validate(uri);
        public IChannel CreateChannel(string uri)
        {
            Validate(uri);
            var _uri = ChannelOptions.Address(uri);
            switch (_uri.Scheme)
            {
                case "sio":
                    return new SerialIOChannel(uri);
                case "udp":
                    return new UdpChannel(uri);
                case "tcp":
                    return new TcpClientChannel(uri);
                default:
                    throw new ArgumentException($"uri is incorrect.uri{uri} ");
            }

        }
    }

    public class SioChannelCreator : IChannelCreator
    {
        public IChannel CreateChannel(string uri)
        {
            return new SerialIOChannel(uri);
        }
    }
    public class TcpChannelCreator : IChannelCreator
    {
        public IChannel CreateChannel(string uri)
        {
            return new TcpClientChannel(uri);
        }
    }
    public class UdpChannelCreator : IChannelCreator
    {
        public IChannel CreateChannel(string uri)
        {
            return new UdpChannel(uri);
        }
    }

}
