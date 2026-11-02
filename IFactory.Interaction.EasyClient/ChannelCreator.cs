using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IFactory.Interaction.EasyClient
{
    public class ChannelCreator : IChannelCreator
    {
        public IChannel CreateChannel(string uri)
        {
            var _uri = new Uri(uri);
            switch (_uri.Scheme)
            {
                case "sio":
                    return new SioChannelCreator().CreateChannel(uri);
                case "udp":
                    return new UdpChannelCreator().CreateChannel(uri);
                case "tcp":
                    return new TcpChannelCreator().CreateChannel(uri);
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
