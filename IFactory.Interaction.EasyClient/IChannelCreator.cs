using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IFactory.Interaction.EasyClient
{
    public  interface IChannelCreator
    {
        IChannel CreateChannel(string uri);
        void Validate(string uri) { }
    }
}
