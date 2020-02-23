﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.IO;
using UnityEngine;

namespace DeBox.Teleport.Core
{
    public class TeleportUdpTransport
    {
        private enum TransportType
        {
            None,
            Client,
            Server,
        }

        private struct ClientParams
        {
            public string host;
            public int port;
        }

        private Thread _thread;
        private bool _stopRequested;
        private TransportType _transportType = TransportType.None;
        private Func<BaseTeleportChannel>[] _channelCreators;
        private readonly double _endpointTimeout = 30;
        private EndpointCollection _endpointCollection = null;

        public TeleportUdpTransport(params Func<BaseTeleportChannel>[] channelCreators)
        {
            _channelCreators = channelCreators;
        }

        public void Send(Action<TeleportWriter> serializer, byte channelId = 0)
        {
            byte[] data;
            using (var stream = new MemoryStream())
            {
                using (var writer = new TeleportWriter(stream))
                {
                    serializer(writer);
                    data = stream.ToArray();
                }
            }
            foreach (var ep in _endpointCollection.GetEndpoints())
            {
                var channel = _endpointCollection.GetChannelOfEndpoint(ep, channelId);
                channel.Send(channel.PrepareToSend(data));
            }
        }



        public void ProcessIncoming(Action<EndPoint, TeleportReader> deserializer)
        {
            
            foreach (var endpoint in _endpointCollection.GetEndpoints())
            {                
                var endpointChannels = _endpointCollection.GetChannelsOfEndpoint(endpoint);
                
                for (byte channelId = 0; channelId < endpointChannels.Length; channelId++)
                {                    
                    var channel = _endpointCollection.GetChannelOfEndpoint(endpoint, channelId);
                    
                    while (channel.IncomingMessageCount > 0)
                    {                        
                        var next = channel.GetNextIncomingData();
                        using (var stream = new MemoryStream(next))
                        {
                            using (var reader = new TeleportReader(stream))
                            {

                                deserializer(endpoint, reader);
                            }                                
                        }                            
                    }
                }
            }
        }

        public void Send(byte[] data, byte channelId = 0)
        {            
            foreach (var ep in _endpointCollection.GetEndpoints())
            {
                var channel = _endpointCollection.GetChannelOfEndpoint(ep, channelId);
                channel.Send(channel.PrepareToSend(data));
            }
        }

        public void Send(byte[] data, byte channelId = 0, params EndPoint[] endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                var channel = _endpointCollection.GetChannelOfEndpoint(endpoint, channelId);
                channel.Send(channel.PrepareToSend(data));
            }
        }

        public void InternalListen(object portObj)
        {
            _endpointCollection = new EndpointCollection(_endpointTimeout, _channelCreators);
            _transportType = TransportType.Server;
            var port = (int)portObj;

            byte[] data = new byte[1024];
            IPEndPoint ip = new IPEndPoint(IPAddress.Any, port);

            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            socket.Bind(ip);

            IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
            EndPoint endpoint = (EndPoint)(sender);
            TeleportPacketBuffer packetBuffer;
            byte[] packetData = new byte[256];
            int packetLength;
            byte channelId;
            int receivedDataLength;
            _stopRequested = false;
            while (!_stopRequested)
            {
                SendOutgoingDataAllChannelsOfAllEndpoints(socket, _endpointCollection);
                data = new byte[1024];

                while (socket.Available > 0)
                {
                    receivedDataLength = socket.ReceiveFrom(data, ref endpoint);
                    _endpointCollection.Ping(endpoint);
                    packetBuffer = _endpointCollection.GetBufferOfEndpoint(endpoint);
                    packetBuffer.ReceiveRawData(data, receivedDataLength);
                    do
                    {
                        packetLength = packetBuffer.TryParseNextIncomingPacket(packetData, out channelId);
                        if (packetLength > 0)
                        {
                            ReceiveIncomingData(channelId, packetData, packetLength, endpoint, _endpointCollection);
                        }   
                    }
                    while (packetLength > 0);
                    
                }
                

                Upkeep();
            }
        }

        private void Upkeep()
        {
            foreach (var ep in _endpointCollection.GetEndpoints())
            {
                foreach (var channel in _endpointCollection.GetChannelsOfEndpoint(ep))
                {
                    channel.Upkeep();
                }
            }
        }
        
        private void ReceiveIncomingChannelData(BaseTeleportChannel channel, byte[] data, int startIndex, int length, EndPoint endpoint, EndpointCollection endpointCollection)
        {
            endpointCollection.Ping(endpoint);
            channel.Receive(data, startIndex, length);
        }


        private void ReceiveIncomingData(byte channelId, byte[] data, int receivedDataLength, EndPoint endpoint, EndpointCollection endpointCollection)
        {
            var endpointChannel = endpointCollection.GetChannelOfEndpoint(endpoint, channelId);
            ReceiveIncomingChannelData(endpointChannel, data, 0, receivedDataLength, endpoint, endpointCollection);            
        }

        private void SendOutgoingDataAllChannelsOfAllEndpoints(Socket socket, EndpointCollection endpointCollection)
        {
            byte[] data;
            BaseTeleportChannel channel;
            TeleportPacketBuffer packetBuffer;
            foreach (var endpoint in endpointCollection.GetEndpoints())
            {
                packetBuffer = _endpointCollection.GetBufferOfEndpoint(endpoint);
                var endpointChannels = endpointCollection.GetChannelsOfEndpoint(endpoint);
                for (byte channelId = 0; channelId < endpointChannels.Length; channelId++)
                {
                    
                    channel = endpointChannels[channelId];
                    while (channel.OutgoingMessageCount > 0)
                    {
                        data = channel.GetNextOutgoingData();
                        data = packetBuffer.CreatePacket(channelId, data, 0, (byte)data.Length);
                        if (_transportType == TransportType.Client)
                        {
                            socket.Send(data, data.Length, SocketFlags.None);
                        }
                        else
                        {
                            socket.SendTo(data, data.Length, SocketFlags.None, endpoint);
                        }
   
                    }
                }
            }
        }

        private void InternalClient(object clientParamsObj)
        {
            _endpointCollection = new EndpointCollection(_endpointTimeout, _channelCreators);
            
            var clientParams = (ClientParams)clientParamsObj;
            var udpClient = new UdpClient();
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var endpoint = new IPEndPoint(IPAddress.Parse(clientParams.host), clientParams.port);
            byte[] data;
            byte[] packetData = new byte[256];
            int packetLength;
            TeleportPacketBuffer packetBuffer;
            byte channelId;
            _transportType = TransportType.Client;
            socket.Connect(endpoint);
            _endpointCollection.Ping(endpoint);

            int receivedDataLength;
            _stopRequested = false;
            while (!_stopRequested)
            {
                SendOutgoingDataAllChannelsOfAllEndpoints(socket, _endpointCollection);
                data = new byte[1024];

                while (socket.Available > 0)
                {
                    receivedDataLength = socket.Receive(data);
                    _endpointCollection.Ping(endpoint);
                    packetBuffer = _endpointCollection.GetBufferOfEndpoint(endpoint);
                    packetBuffer.ReceiveRawData(data, receivedDataLength);
                    do
                    {
                        packetLength = packetBuffer.TryParseNextIncomingPacket(packetData, out channelId);
                        if (packetLength > 0)
                        {
                            ReceiveIncomingData(channelId, packetData, packetLength, endpoint, _endpointCollection);
                        }
                    }
                    while (packetLength > 0);
                }

                Upkeep();
            }
        }


        public void StartClient(string host, int port)
        {
            if (_thread != null)
            {
                throw new Exception("Thread already active");
            }
            Debug.Log("Starting client");
            _stopRequested = false;
            _thread = new Thread(InternalClient);
            _thread.Start(new ClientParams() { host = host, port = port });
        }

        public void StopClient()
        {
            Debug.Log("Stopping client");
            _stopRequested = true;
            _thread.Join();
            _thread = null;
            Debug.Log("Stopped client");
        }

        public void StartListener(int port)
        {
            if (_thread != null)
            {
                throw new Exception("Thread already active");
            }
            Debug.Log("Starting server");
            _stopRequested = false;
            _thread = new Thread(new ParameterizedThreadStart(InternalListen));
            _thread.Start(port);
        }

        public void StopListener()
        {
            Debug.Log("Stopping");
            _stopRequested = true;
            _thread.Join();
            _thread = null;
            Debug.Log("Stopped server");
        }

    }

}