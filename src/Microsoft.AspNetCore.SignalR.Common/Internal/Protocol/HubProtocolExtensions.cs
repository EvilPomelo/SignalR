using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Microsoft.AspNetCore.SignalR.Internal.Protocol
{
    public static class HubProtocolExtensions
    {
        public static byte[] WriteToArray(this IHubProtocol hubProtocol, HubMessage message)
        {
            using(var ms = new MemoryStream())
            {
                hubProtocol.WriteMessage(message, ms);
                return ms.ToArray();
            }
        }
    }
}
