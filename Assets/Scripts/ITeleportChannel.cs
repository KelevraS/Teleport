﻿using System.IO;

namespace DeBox.Teleport.Transport
{
    public interface ITeleportChannel
    {
        int IncomingMessageCount { get;  }
        byte[] GetNextIncomingData();        
    }

}
