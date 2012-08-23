﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using System.IO;
using System.Collections;


namespace XLibrary.Remote
{
    public class XRemote
    {
        public RijndaelManaged Encryption = new RijndaelManaged();

        public List<XConnection> Connections = new List<XConnection>();

        public List<string> DebugLog = new List<string>();

        // listening
        int ListenPort = 4566;
        Socket ListenSocket;

        // logging
        public BandwidthLog Bandwidth = new BandwidthLog(10);
        public Queue<PacketLogEntry> LoggedPackets = new Queue<PacketLogEntry>();

        Dictionary<string, Action<XConnection, GenericPacket>> RouteGeneric = new Dictionary<string, Action<XConnection, GenericPacket>>();

        // downloading
        List<Download> Downloads = new List<Download>();
        const int DownloadChunkSize = XConnection.BUFF_SIZE / 2; // should be 8kb
       
        // sync
        public List<SyncState> SyncClients = new List<SyncState>();


        // client specific
        public string RemoteStatus = "";
        public string RemoteCachePath;
        public string RemoteDatHash;
        public long RemoteDatSize;
        public string LocalDatPath;
        public string LocalDatTempPath;
        public Stream LocalTempFile;


        public XRemote()
        {
            RouteGeneric["Ping"] = Receive_Ping;
            RouteGeneric["Pong"] = Receive_Pong;

            RouteGeneric["Bye"] = Receive_Bye;

            RouteGeneric["DatHashRequest"] = Receive_DatHashRequest;
            RouteGeneric["DatHashResponse"] = Receive_DatHashResponse;

            RouteGeneric["DatFileRequest"] = Receive_DatFileRequest;

            RouteGeneric["StartSync"] = Receive_StartSync;
        }

        public void StartListening()
        {
            // todo use key embedded with dat file
            Encryption.Key = Utilities.HextoBytes("43a6e878b76fc485698f2d3b2cfbd93b9f90907e1c81e8821dceac82d45252f3");

            try
            {
                if(ListenSocket == null)
                    ListenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                ListenSocket.Bind(new IPEndPoint(System.Net.IPAddress.Any, ListenPort));

                ListenSocket.Listen(10);
                ListenSocket.BeginAccept(new AsyncCallback(ListenSocket_Accept), ListenSocket);

               Log("Listening for TCP on port {0}", ListenPort);
            }
            catch (Exception ex)
            {
               Log("TcpHandler::TcpHandler Exception: " + ex.Message);
            }
        }

        public void Shutdown()
        {
            try
            {
                Socket oldSocket = ListenSocket; // do this to prevent listen exception
                ListenSocket = null;

                if (oldSocket != null)
                    oldSocket.Close();

                lock (Connections)
                    foreach (var connection in Connections)
                        connection.CleanClose("Client shutting down");
            }
            catch (Exception ex)
            {
               Log("TcpHandler::Shudown Exception: " + ex.Message);
            }
        }

        public void SecondTimer()
        {
            // Run through socket connections
            var deadSockets = new List<XConnection>();

            lock (Connections)
                foreach (var socket in Connections)
                {
                    socket.SecondTimer();

                    // only let socket linger in connecting state for 10 secs
                    if (socket.State == TcpState.Closed)
                        deadSockets.Add(socket);
                }

            foreach (var socket in deadSockets)
            {
                Connections.Remove(socket);

                string message = "Connection to " + socket.ToString() + " Removed";
                if (socket.ByeMessage != null)
                    message += ", Reason: " + socket.ByeMessage;

               Log(message);

                // socket.TcpSocket = null; causing endrecv to fail on disconnect
            }
        }

        public XConnection MakeOutbound(IPAddress address, ushort tcpPort)
        {
            // only allow 1 outbound connection at a time
            if (Connections.Count != 0)
                return null;

            try
            {
               var outbound = new XConnection(this, address, tcpPort);
               Log("Attempting Connection to " + address.ToString() + ":" + tcpPort.ToString());

                lock (Connections)
                    Connections.Add(outbound);

                return outbound;
            }
            catch (Exception ex)
            {
                Log("TcpHandler::MakeOutbound Exception: " + ex.Message);
                return null;
            }
        }

        internal void Log(string text, params object[] args)
        {
            DebugLog.Add(string.Format(text, args));
        }

        void ListenSocket_Accept(IAsyncResult asyncResult)
        {
            if (ListenSocket == null)
                return;

            try
            {
                Socket tempSocket = ListenSocket.EndAccept(asyncResult); // do first to catch

                OnAccept(tempSocket, (IPEndPoint)tempSocket.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                Log("TcpHandler::ListenSocket_Accept:1 Exception: " + ex.Message);
            }

            // exception handling not combined because endreceive can fail legit, still need begin receive to run
            try
            {
                ListenSocket.BeginAccept(new AsyncCallback(ListenSocket_Accept), ListenSocket);
            }
            catch (Exception ex)
            {
                Log("TcpHandler::ListenSocket_Accept:2 Exception: " + ex.Message);
            }
        }

        public XConnection OnAccept(Socket socket, IPEndPoint source)
        {
            var inbound = new XConnection(this);

            inbound.TcpSocket = socket;
            inbound.RemoteIP = source.Address;
            inbound.SetConnected();

            // it's not until the host sends us traffic that we can send traffic back because we don't know
            // connecting node's dhtID (and hence encryption key) until ping is sent

            lock (Connections)
                Connections.Add(inbound);

           Log("Accepted Connection from {0}", inbound);

            return inbound;
        }

        internal void OnConnected(XConnection connection)
        {
            if (XRay.IsInvokeRequired())
            {
                XRay.RunInCoreAsync(() => OnConnected(connection));
                return;
            }
            // runs when client connects to server, not the other way around (that's what OnAccept is for)
            RemoteStatus = "Requesting Dat Hash";

            connection.SendPacket(new GenericPacket("DatHashRequest"));
        }

        internal void IncomingPacket(XConnection connection, G2ReceivedPacket packet)
        {
            if (XRay.IsInvokeRequired())
            {
                XRay.RunInCoreAsync(() => IncomingPacket(connection, packet));
                return;
            }

            switch (packet.Root.Name)
            {
                case PacketType.Generic:

                    var generic = GenericPacket.Decode(packet.Root);

                    Log("Generic Packet Received: " + generic.Name);

                    if(RouteGeneric.ContainsKey(generic.Name))
                        RouteGeneric[generic.Name](connection, generic);
                    else
                       Log("Unknown generic packet: " + generic.Name);

                    break;

                case PacketType.Dat:
                    ReceiveDatPacket(connection, packet);
                    break;
            }
        }

        void Receive_Ping(XConnection connection, GenericPacket ping)
        {
            connection.SendPacket(new GenericPacket("Pong"));
        }

        void Receive_Pong(XConnection connection, GenericPacket pong)
        {
            // lower level socket on receiving data marks connection as alive
        }

        void Receive_Bye(XConnection connection, GenericPacket bye)
        {
            Log("Received bye from {0}: {1}", connection, bye.Data["Reason"]);
            connection.Disconnect();
        }

        void Receive_DatHashRequest(XConnection connection, GenericPacket request)
        {
            var response = new GenericPacket("DatHashResponse");

            response.Data = new Dictionary<string, string>
            {
                {"Hash", XRay.DatHash},
                {"Size", XRay.DatSize.ToString()}
            };

            Log("Sending Dat Hash");

            connection.SendPacket(response);
        }

        void Receive_DatHashResponse(XConnection connection, GenericPacket response)
        {
            // only one instance type per builder instance because xray is static
            if (XRay.InitComplete && RemoteDatHash != null && RemoteDatHash != response.Data["Hash"])
            {
                RemoteStatus = "Open a new builder instance to connect to a new server";
                connection.Disconnect();
                return;
            }

            // check if we have this hash.dat file locally, if not then request download
            RemoteDatHash = response.Data["Hash"];
            RemoteDatSize = long.Parse(response.Data["Size"]);

            LocalDatPath = Path.Combine(RemoteCachePath, RemoteDatHash + ".dat");
            LocalDatTempPath = Path.Combine(RemoteCachePath, RemoteDatHash + ".tmp");

            if (RemoteDatSize == 0)
                RemoteStatus = "Error - Remote Dat Empty";

            else if (File.Exists(LocalDatPath))
                Send_StartSync(connection);
            
            else
            {
                Log("Requesting Dat File, size: " + RemoteDatSize.ToString());

                RemoteStatus = "Requesting Dat File";

                var request = new GenericPacket("DatFileRequest");

                connection.SendPacket(request);
            }
        }

        void Receive_DatFileRequest(XConnection connection, GenericPacket request)
        {
            // received by server from client

            Log("Creating download for connection, size: " + XRay.DatSize.ToString());

            Downloads.Add(new Download()
            {
                Connection = connection,
                Stream = File.OpenRead(XRay.DatPath),
                FilePos = 0
            });
            
        }

        public void ProcessDownloads()
        {
            if (Downloads.Count == 0)
                return;

            var removeDownloads = new List<Download>();

            foreach (var download in Downloads)
            {
                if (download.Connection.State != TcpState.Connected)
                {
                    removeDownloads.Add(download);
                    continue;
                }

                // while connection has 8kb in buffer free
                while (download.Connection.SendBufferBytesAvailable > DownloadChunkSize + 200) // read 8k, 200b overflow buffer
                {
                    // read 8k of file
                    long readSize = XRay.DatSize - download.FilePos;
                    if (readSize > DownloadChunkSize)
                        readSize = DownloadChunkSize;

                    var chunk = new DatPacket(download.FilePos, download.Stream.Read((int)readSize));
       
                    Log("Sending dat pos: {0}, length: {1}", chunk.Pos, chunk.Data.Length); //todo delete

                    // send
                    if(chunk.Data.Length > 0)
                    {
                        int bytesSent = download.Connection.SendPacket(chunk);
                        if(bytesSent < 0)
                            break;
                    }

                    download.FilePos += chunk.Data.Length;

                    // remove when complete
                    if (download.FilePos >= XRay.DatSize)
                    {
                        removeDownloads.Add(download);
                        break;
                    }
                }
            }

            foreach (var download in removeDownloads)
            {
                download.Stream.Close();
                Downloads.Remove(download);
            }
        }

        void ReceiveDatPacket(XConnection connection, G2ReceivedPacket packet)
        {
            // received by client from server
            var chunk = DatPacket.Decode(packet.Root);

            // write to tmp file
            if (LocalTempFile == null)
            {
                LocalTempFile = File.Create(LocalDatTempPath);
                LocalTempFile.SetLength(0);
            }

            Log("Received dat pos: {0}, length: {1}", chunk.Pos, chunk.Data.Length); //todo delete

            LocalTempFile.Write(chunk.Data);

            var percentComplete = LocalTempFile.Length * 100 / RemoteDatSize;

            RemoteStatus = string.Format("Downloading Dat File - {0}% Complete", percentComplete);

            // hash when complete
            if (LocalTempFile.Length >= RemoteDatSize)
            {
                LocalTempFile.Close();
                LocalTempFile = null;

                var checkHash = Utilities.MD5HashFile(LocalDatTempPath);

                if (checkHash == RemoteDatHash)
                {
                    File.Move(LocalDatTempPath, LocalDatPath);
                    Send_StartSync(connection);
                }
                else
                    RemoteStatus = string.Format("Dat integrity check failed - Expecting {0}, got {1}", RemoteDatHash, checkHash);
            }

        }

        void Send_StartSync(XConnection connection)
        {
            RemoteStatus = "Starting Sync";

            // send packet telling server to start syncing us

            XRay.Init(LocalDatPath, true, true, true, true);

            connection.SendPacket(new GenericPacket("StartSync"));
        }

        void Receive_StartSync(XConnection connection, GenericPacket packet)
        {
            var state = new SyncState();
            state.Connection = connection;
            state.HitArray = new BitArray(XRay.Nodes.Length);
            state.HitArrayAlt = new HashSet<int>();

            SyncClients.Add(state);
        }

        internal void RunSyncClients()
        {
            /*foreach (var state in SyncClients)
            {
                if (state.Connection.State != TcpState.Connected)
                    continue;

                // save current set and create a new one so other threads dont get tripped up
                var sendSet = state.HitArrayAlt;
                state.HitArrayAlt = new HashSet<int>();

                // check that there's space in the send buffer to send state
                state.Connection.SendPacket(
            }*/
        }
    }

    class Download
    {
        public XConnection Connection;
        public FileStream Stream;
        public long FilePos;
    }

    public class SyncState
    {
        public XConnection Connection;
        public BitArray HitArray;
        public HashSet<int> HitArrayAlt;
    }
}
