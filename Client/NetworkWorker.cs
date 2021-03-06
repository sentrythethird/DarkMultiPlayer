using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;
using DarkMultiPlayerCommon;
using MessageStream2;

namespace DarkMultiPlayer
{
    public class NetworkWorker
    {
        //Read from ConnectionWindow
        public ClientState state
        {
            private set;
            get;
        }

        private static NetworkWorker singleton = new NetworkWorker();
        private TcpClient clientConnection = null;
        private float lastSendTime = 0f;
        private AutoResetEvent sendEvent = new AutoResetEvent(false);
        private Queue<ClientMessage> sendMessageQueueHigh = new Queue<ClientMessage>();
        private Queue<ClientMessage> sendMessageQueueSplit = new Queue<ClientMessage>();
        private Queue<ClientMessage> sendMessageQueueLow = new Queue<ClientMessage>();
        private ClientMessageType lastSplitMessageType = ClientMessageType.HEARTBEAT;
        //Receive buffer
        private float lastReceiveTime = 0f;
        private bool isReceivingMessage = false;
        private int receiveMessageBytesLeft = 0;
        private ServerMessage receiveMessage = null;
        //Receive split buffer
        private bool isReceivingSplitMessage = false;
        private int receiveSplitMessageBytesLeft = 0;
        private ServerMessage receiveSplitMessage = null;
        //Used for the initial sync
        private int numberOfKerbals = 0;
        private int numberOfKerbalsReceived = 0;
        private int numberOfVessels = 0;
        private int numberOfVesselsReceived = 0;
        //Connection tracking
        private bool terminateOnNextMessageSend;
        private string connectionEndReason;
        private bool terminateThreadsOnNextUpdate;
        //Network traffic tracking
        private long bytesQueuedOut;
        private long bytesSent;
        private long bytesReceived;
        //Locking
        private int connectingThreads = 0;
        private object connectLock = new object();
        private object disconnectLock = new object();
        private object messageQueueLock = new object();
        private Thread connectThread;
        private List<Thread> parallelConnectThreads = new List<Thread>();
        private Thread receiveThread;
        private Thread sendThread;
        private string serverMotd;
        private bool displayMotd;

        public NetworkWorker()
        {
            lock (Client.eventLock)
            {
                Client.updateEvent.Add(this.Update);
            }
        }

        public static NetworkWorker fetch
        {
            get
            {
                return singleton;
            }
        }
        //Called from main
        private void Update()
        {
            if (terminateThreadsOnNextUpdate)
            {
                terminateThreadsOnNextUpdate = false;
                TerminateThreads();
            }

            if (state == ClientState.CONNECTED)
            {
                Client.fetch.status = "Connected";
            }

            if (state == ClientState.HANDSHAKING)
            {
                Client.fetch.status = "Handshaking";
            }

            if (state == ClientState.AUTHENTICATED)
            {
                NetworkWorker.fetch.SendPlayerStatus(PlayerStatusWorker.fetch.myPlayerStatus);
                DarkLog.Debug("Sending time sync!");
                state = ClientState.TIME_SYNCING;
                Client.fetch.status = "Syncing server clock";
                SendTimeSync();
            }
            if (TimeSyncer.fetch.synced && state == ClientState.TIME_SYNCING)
            {
                DarkLog.Debug("Time Synced!");
                state = ClientState.TIME_SYNCED;
            }
            if (state == ClientState.TIME_SYNCED)
            {
                DarkLog.Debug("Requesting kerbals!");
                Client.fetch.status = "Syncing kerbals";
                state = ClientState.SYNCING_KERBALS;
                SendKerbalsRequest();
            }
            if (state == ClientState.VESSELS_SYNCED)
            {
                DarkLog.Debug("Vessels Synced!");
                Client.fetch.status = "Syncing universe time";
                state = ClientState.TIME_LOCKING;
                //The subspaces are held in the warp control messages, but the warp worker will create a new subspace if we aren't locked.
                //Process the messages so we get the subspaces, but don't enable the worker until the game is started.
                WarpWorker.fetch.ProcessWarpMessages();
                TimeSyncer.fetch.workerEnabled = true;
                ChatWorker.fetch.workerEnabled = true;
                PlayerColorWorker.fetch.workerEnabled = true;
                FlagSyncer.fetch.workerEnabled = true;
                FlagSyncer.fetch.SendFlagList();
                PlayerColorWorker.fetch.SendPlayerColorToServer();
                PartKiller.fetch.RegisterGameHooks();
            }
            if (state == ClientState.TIME_LOCKING)
            {
                if (TimeSyncer.fetch.locked)
                {
                    DarkLog.Debug("Time Locked!");
                    DarkLog.Debug("Starting Game!");
                    Client.fetch.status = "Starting game";
                    state = ClientState.STARTING;
                    Client.fetch.startGame = true;
                }
            }
            if ((state == ClientState.STARTING) && (HighLogic.LoadedScene == GameScenes.SPACECENTER))
            {
                state = ClientState.RUNNING;
                Client.fetch.status = "Running";
                Client.fetch.gameRunning = true;
                AsteroidWorker.fetch.workerEnabled = true;
                VesselWorker.fetch.workerEnabled = true;
                HackyInAtmoLoader.fetch.workerEnabled = true;
                PlayerStatusWorker.fetch.workerEnabled = true;
                ScenarioWorker.fetch.workerEnabled = true;
                DynamicTickWorker.fetch.workerEnabled = true;
                WarpWorker.fetch.workerEnabled = true;
                CraftLibraryWorker.fetch.workerEnabled = true;
                ScreenshotWorker.fetch.workerEnabled = true;
                SendMotdRequest();
                ToolbarSupport.fetch.EnableToolbar();
            }
            if (displayMotd && (HighLogic.LoadedScene != GameScenes.LOADING) && (Time.timeSinceLevelLoad > 2f))
            {
                displayMotd = false;
                ScreenMessages.PostScreenMessage(serverMotd, 10f, ScreenMessageStyle.UPPER_CENTER);
                //Control locks will bug out the space centre sceen, so remove them before starting.
                DeleteAllTheControlLocksSoTheSpaceCentreBugGoesAway();
            }
        }

        private void DeleteAllTheControlLocksSoTheSpaceCentreBugGoesAway()
        {
            DarkLog.Debug("Clearing " + InputLockManager.lockStack.Count + " control locks");
            InputLockManager.ClearControlLocks();
        }

        //This isn't tied to frame rate, During the loading screen Update doesn't fire.
        public void SendThreadMain()
        {
            try
            {
                while (true)
                {
                    CheckDisconnection();
                    SendHeartBeat();
                    bool sentMessage = SendOutgoingMessages();
                    if (!sentMessage)
                    {
                        sendEvent.WaitOne(100);
                    }
                }
            }
            catch (ThreadAbortException)
            {
                //Don't care
            }
            catch (Exception e)
            {
                DarkLog.Debug("Send thread error: " + e);
            }
        }

        #region Connecting to server

        //Called from main
        public void ConnectToServer(string address, int port)
        {
            DMPServerAddress connectAddress = new DMPServerAddress();
            connectAddress.ip = address;
            connectAddress.port = port;
            connectThread = new Thread(new ParameterizedThreadStart(ConnectToServerMain));
            connectThread.IsBackground = true;
            //ParameterizedThreadStart only takes one object.
            connectThread.Start(connectAddress);
        }

        private void ConnectToServerMain(object connectAddress)
        {
            DMPServerAddress connectAddressCast = (DMPServerAddress)connectAddress;
            string address = connectAddressCast.ip;
            int port = connectAddressCast.port;
            if (state == ClientState.DISCONNECTED)
            {
                DarkLog.Debug("Trying to connect to " + address + ", port " + port);
                Client.fetch.status = "Connecting to " + address + " port " + port;
                sendMessageQueueHigh = new Queue<ClientMessage>();
                sendMessageQueueSplit = new Queue<ClientMessage>();
                sendMessageQueueLow = new Queue<ClientMessage>();
                numberOfKerbals = 0;
                numberOfKerbalsReceived = 0;
                numberOfVessels = 0;
                numberOfVesselsReceived = 0;
                bytesReceived = 0;
                bytesQueuedOut = 0;
                bytesSent = 0;
                receiveSplitMessage = null;
                receiveSplitMessageBytesLeft = 0;
                isReceivingSplitMessage = false;
                IPAddress destinationAddress;
                if (!IPAddress.TryParse(address, out destinationAddress))
                {
                    try
                    {
                        IPHostEntry dnsResult = Dns.GetHostEntry(address);
                        if (dnsResult.AddressList.Length > 0)
                        {
                            List<IPEndPoint> addressToConnectTo = new List<IPEndPoint>();
                            foreach (IPAddress testAddress in dnsResult.AddressList)
                            {
                                if (testAddress.AddressFamily == AddressFamily.InterNetwork || testAddress.AddressFamily == AddressFamily.InterNetworkV6)
                                {
                                    Interlocked.Increment(ref connectingThreads);
                                    Client.fetch.status = "Connecting";
                                    lastSendTime = UnityEngine.Time.realtimeSinceStartup;
                                    lastReceiveTime = UnityEngine.Time.realtimeSinceStartup;
                                    state = ClientState.CONNECTING;
                                    addressToConnectTo.Add(new IPEndPoint(testAddress, port));
                                }
                            }
                            foreach (IPEndPoint endpoint in addressToConnectTo)
                            {
                                Thread parallelConnectThread = new Thread(new ParameterizedThreadStart(ConnectToServerAddress));
                                parallelConnectThreads.Add(parallelConnectThread);
                                parallelConnectThread.Start(endpoint);
                            }
                            if (addressToConnectTo.Count == 0)
                            {
                                DarkLog.Debug("DNS does not contain a valid address entry");
                                Client.fetch.status = "DNS does not contain a valid address entry";
                                return;
                            }
                        }
                        else
                        {
                            DarkLog.Debug("Address is not a IP or DNS name");
                            Client.fetch.status = "Address is not a IP or DNS name";
                            return;
                        }
                    }
                    catch (Exception e)
                    {
                        DarkLog.Debug("DNS Error: " + e.ToString());
                        Client.fetch.status = "DNS Error: " + e.Message;
                        return;
                    }
                }
                else
                {
                    Interlocked.Increment(ref connectingThreads);
                    Client.fetch.status = "Connecting";
                    lastSendTime = UnityEngine.Time.realtimeSinceStartup;
                    lastReceiveTime = UnityEngine.Time.realtimeSinceStartup;
                    state = ClientState.CONNECTING;
                    ConnectToServerAddress(new IPEndPoint(destinationAddress, port));
                }
            }

            while (state == ClientState.CONNECTING)
            {
                Thread.Sleep(500);
                CheckInitialDisconnection();
            }
        }

        private void ConnectToServerAddress(object destinationObject)
        {
            IPEndPoint destination = (IPEndPoint)destinationObject;
            TcpClient testConnection = new TcpClient(destination.AddressFamily);
            testConnection.NoDelay = true;
            try
            {
                DarkLog.Debug("Connecting to " + destination.Address + " port " + destination.Port + "...");
                testConnection.Connect(destination.Address, destination.Port);
                lock (connectLock)
                {
                    if (state == ClientState.CONNECTING)
                    {
                        if (testConnection.Connected)
                        {
                            clientConnection = testConnection;
                            //Timeout didn't expire.
                            DarkLog.Debug("Connected to " + destination.Address + " port " + destination.Port);
                            Client.fetch.status = "Connected";
                            state = ClientState.CONNECTED;
                            sendThread = new Thread(new ThreadStart(SendThreadMain));
                            sendThread.IsBackground = true;
                            sendThread.Start();
                            receiveThread = new Thread(new ThreadStart(StartReceivingIncomingMessages));
                            receiveThread.IsBackground = true;
                            receiveThread.Start();
                        }
                        else
                        {
                            //The connection actually comes good, but after the timeout, so we can send the disconnect message.
                            if ((connectingThreads == 1) && (state == ClientState.CONNECTING))
                            {
                                DarkLog.Debug("Failed to connect within the timeout!");
                                Disconnect("Initial connection timeout");
                            }
                        }
                    }
                    else
                    {
                        if (testConnection.Connected)
                        {
                            testConnection.GetStream().Close();
                            testConnection.GetStream().Dispose();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if ((connectingThreads == 1) && (state == ClientState.CONNECTING))
                {
                    HandleDisconnectException(e);
                }
            }
            Interlocked.Decrement(ref connectingThreads);
            lock (parallelConnectThreads)
            {
                parallelConnectThreads.Remove(Thread.CurrentThread);
            }
        }

        #endregion

        #region Connection housekeeping

        private void CheckInitialDisconnection()
        {
            if (state == ClientState.CONNECTING)
            {
                if ((UnityEngine.Time.realtimeSinceStartup - lastReceiveTime) > (Common.INITIAL_CONNECTION_TIMEOUT / 1000))
                {
                    Disconnect("Failed to connect!");
                    Client.fetch.status = "Failed to connect - no reply";
                    if (connectThread != null)
                    {
                        try
                        {
                            lock (parallelConnectThreads)
                            {
                                foreach (Thread parallelConnectThread in parallelConnectThreads)
                                {
                                    parallelConnectThread.Abort();
                                }
                                parallelConnectThreads.Clear();
                                connectingThreads = 0;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private void CheckDisconnection()
        {
            if (state >= ClientState.CONNECTED)
            {
                if ((UnityEngine.Time.realtimeSinceStartup - lastReceiveTime) > (Common.CONNECTION_TIMEOUT / 1000))
                {
                    Disconnect("Connection timeout");
                }
            }
        }

        public void Disconnect(string reason)
        {
            lock (disconnectLock)
            {
                if (state != ClientState.DISCONNECTED)
                {
                    DarkLog.Debug("Disconnected, reason: " + reason);
                    if (!HighLogic.LoadedSceneIsEditor && !HighLogic.LoadedSceneIsFlight)
                    {
                        NetworkWorker.fetch.SendDisconnect("Force quit to main menu");
                        Client.fetch.forceQuit = true;
                    }
                    else
                    {
                        Client.fetch.displayDisconnectMessage = true;
                    }
                    Client.fetch.status = reason;
                    state = ClientState.DISCONNECTED;

                    try
                    {
                        if (clientConnection != null)
                        {
                            clientConnection.GetStream().Close();
                            clientConnection.Close();
                            clientConnection = null;
                        }
                    }
                    catch (Exception e)
                    {
                        DarkLog.Debug("Error closing connection: " + e.Message);
                    }
                    terminateThreadsOnNextUpdate = true;
                }
            }
        }

        private void TerminateThreads()
        {
            foreach (Thread parallelConnectThread in parallelConnectThreads)
            {
                try
                {
                    parallelConnectThread.Abort();
                }
                catch
                {
                    //Don't care
                }
            }
            parallelConnectThreads.Clear();
            connectingThreads = 0;
            try
            {
                connectThread.Abort();
            }
            catch
            {
                //Don't care
            }
            try
            {
                sendThread.Abort();
            }
            catch
            {
                //Don't care
            }
            try
            {
                receiveThread.Abort();
            }
            catch
            {
                //Don't care
            }
        }

        #endregion

        #region Network writers/readers

        private void StartReceivingIncomingMessages()
        {
            lastReceiveTime = UnityEngine.Time.realtimeSinceStartup;
            //Allocate byte for header
            isReceivingMessage = false;
            receiveMessage = new ServerMessage();
            receiveMessage.data = new byte[8];
            receiveMessageBytesLeft = receiveMessage.data.Length;
            try
            {
                while (true)
                {
                    int bytesRead = clientConnection.GetStream().Read(receiveMessage.data, receiveMessage.data.Length - receiveMessageBytesLeft, receiveMessageBytesLeft);
                    bytesReceived += bytesRead;
                    receiveMessageBytesLeft -= bytesRead;
                    if (bytesRead > 0)
                    {
                        lastReceiveTime = UnityEngine.Time.realtimeSinceStartup;
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }
                    if (receiveMessageBytesLeft == 0)
                    {
                        //We either have the header or the message data, let's do something
                        if (!isReceivingMessage)
                        {
                            //We have the header
                            using (MessageReader mr = new MessageReader(receiveMessage.data))
                            {
                                int messageType = mr.Read<int>();
                                int messageLength = mr.Read<int>();
                                //This is from the little endian -> big endian format change.
                                //The handshake challange type is 1, and the payload length is always 1032 bytes.
                                //Little endian (the previous format) DMPServer sends 01 00 00 00 | 08 04 00 00 as the first message, the handshake challange.
                                if (messageType == 16777216 && messageLength == 134479872)
                                {
                                    Disconnect("Disconnected from pre-v0.2 DMP server");
                                    return;
                                }
                                if (messageType > (Enum.GetNames(typeof(ServerMessageType)).Length - 1))
                                {
                                    //Malformed message, most likely from a non DMP-server.
                                    Disconnect("Disconnected from non-DMP server");
                                    //Returning from ReceiveCallback will break the receive loop and stop processing any further messages.
                                    return;
                                }
                                receiveMessage.type = (ServerMessageType)messageType;
                                if (messageLength == 0)
                                {
                                    //Null message, handle it.
                                    receiveMessage.data = null;
                                    HandleMessage(receiveMessage);
                                    receiveMessage.type = 0;
                                    receiveMessage.data = new byte[8];
                                    receiveMessageBytesLeft = receiveMessage.data.Length;
                                }
                                else
                                {
                                    if (messageLength < Common.MAX_MESSAGE_SIZE)
                                    {
                                        isReceivingMessage = true;
                                        receiveMessage.data = new byte[messageLength];
                                        receiveMessageBytesLeft = receiveMessage.data.Length;
                                    }
                                    else
                                    {
                                        //Malformed message, most likely from a non DMP-server.
                                        Disconnect("Disconnected from non-DMP server");
                                        //Returning from ReceiveCallback will break the receive loop and stop processing any further messages.
                                        return;
                                    }
                                }
                            }
                        }
                        else
                        {
                            //We have the message data to a non-null message, handle it
                            isReceivingMessage = false;
                            HandleMessage(receiveMessage);
                            receiveMessage.type = 0;
                            receiveMessage.data = new byte[8];
                            receiveMessageBytesLeft = receiveMessage.data.Length;
                        }
                    }
                    if (state < ClientState.CONNECTED || state == ClientState.DISCONNECTING)
                    {
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                HandleDisconnectException(e);
            }
        }

        private void QueueOutgoingMessage(ClientMessage message, bool highPriority)
        {
            lock (messageQueueLock)
            {
                //All messages have an 8 byte header
                bytesQueuedOut += 8;
                if (message.data != null && message.data.Length > 0)
                {
                    //Count the payload if we have one.
                    bytesQueuedOut += message.data.Length;
                }
                if (highPriority)
                {
                    sendMessageQueueHigh.Enqueue(message);
                }
                else
                {
                    sendMessageQueueLow.Enqueue(message);
                }
            }
            sendEvent.Set();
        }

        private bool SendOutgoingMessages()
        {
            ClientMessage sendMessage = null;
            lock (messageQueueLock)
            {
                if (state >= ClientState.CONNECTED)
                {
                    if (sendMessageQueueHigh.Count > 0)
                    {
                        sendMessage = sendMessageQueueHigh.Dequeue();
                    }
                    if ((sendMessage == null) && (sendMessageQueueSplit.Count > 0))
                    {
                        sendMessage = sendMessageQueueSplit.Dequeue();
                        //We just sent the last piece of a split message
                        if (sendMessageQueueSplit.Count == 0)
                        {
                            if (lastSplitMessageType == ClientMessageType.CRAFT_LIBRARY)
                            {
                                CraftLibraryWorker.fetch.finishedUploadingCraft = true;
                            }
                            if (lastSplitMessageType == ClientMessageType.SCREENSHOT_LIBRARY)
                            {
                                ScreenshotWorker.fetch.finishedUploadingScreenshot = true;
                            }
                        }
                    }
                    if ((sendMessage == null) && (sendMessageQueueLow.Count > 0))
                    {
                        sendMessage = sendMessageQueueLow.Dequeue();
                        //Splits large messages to higher priority messages can get into the queue faster
                        SplitAndRewriteMessage(ref sendMessage);
                    }
                }
            }
            if (sendMessage != null)
            {
                SendNetworkMessage(sendMessage);
                return true;
            }
            return false;
        }

        private void SplitAndRewriteMessage(ref ClientMessage message)
        {
            if (message == null)
            {
                return;
            }
            if (message.data == null)
            {
                return;
            }
            if (message.data.Length > Common.SPLIT_MESSAGE_LENGTH)
            {
                lastSplitMessageType = message.type;
                ClientMessage newSplitMessage = new ClientMessage();
                newSplitMessage.type = ClientMessageType.SPLIT_MESSAGE;
                int splitBytesLeft = message.data.Length;
                using (MessageWriter mw = new MessageWriter())
                {
                    mw.Write<int>((int)message.type);
                    mw.Write<int>(message.data.Length);
                    byte[] firstSplit = new byte[Common.SPLIT_MESSAGE_LENGTH];
                    Array.Copy(message.data, 0, firstSplit, 0, Common.SPLIT_MESSAGE_LENGTH);
                    mw.Write<byte[]>(firstSplit);
                    splitBytesLeft -= Common.SPLIT_MESSAGE_LENGTH;
                    newSplitMessage.data = mw.GetMessageBytes();
                    //SPLIT_MESSAGE adds a 12 byte header.
                    bytesQueuedOut += 12;
                    sendMessageQueueSplit.Enqueue(newSplitMessage);
                }

                while (splitBytesLeft > 0)
                {
                    ClientMessage currentSplitMessage = new ClientMessage();
                    currentSplitMessage.type = ClientMessageType.SPLIT_MESSAGE;
                    currentSplitMessage.data = new byte[Math.Min(splitBytesLeft, Common.SPLIT_MESSAGE_LENGTH)];
                    Array.Copy(message.data, message.data.Length - splitBytesLeft, currentSplitMessage.data, 0, currentSplitMessage.data.Length);
                    splitBytesLeft -= currentSplitMessage.data.Length;
                    //Add the SPLIT_MESSAGE header to the out queue count.
                    bytesQueuedOut += 8;
                    sendMessageQueueSplit.Enqueue(currentSplitMessage);
                }
                message = sendMessageQueueSplit.Dequeue();
            }
        }

        private void SendNetworkMessage(ClientMessage message)
        {
            byte[] messageBytes = Common.PrependNetworkFrame((int)message.type, message.data);

            lock (messageQueueLock)
            {
                bytesQueuedOut -= messageBytes.Length;
                bytesSent += messageBytes.Length;
            }
            //Disconnect after EndWrite completes
            if (message.type == ClientMessageType.CONNECTION_END)
            {
                using (MessageReader mr = new MessageReader(message.data))
                {
                    terminateOnNextMessageSend = true;
                    connectionEndReason = mr.Read<string>();
                }
            }
            lastSendTime = UnityEngine.Time.realtimeSinceStartup;
            try
            {
                clientConnection.GetStream().Write(messageBytes, 0, messageBytes.Length);
                if (terminateOnNextMessageSend)
                {
                    Disconnect("Connection ended: " + connectionEndReason);
                    connectionEndReason = null;
                    terminateOnNextMessageSend = false;
                }
            }
            catch (Exception e)
            {
                HandleDisconnectException(e);
            }
        }

        private void HandleDisconnectException(Exception e)
        {
            if (e.InnerException != null)
            {
                DarkLog.Debug("Connection error: " + e.Message + ", " + e.InnerException);
                Disconnect("Connection error: " + e.Message + ", " + e.InnerException.Message);
            }
            else
            {
                DarkLog.Debug("Connection error: " + e);
                Disconnect("Connection error: " + e.Message);
            }
        }

        #endregion

        #region Message Handling

        private void HandleMessage(ServerMessage message)
        {
            try
            {
                switch (message.type)
                {
                    case ServerMessageType.HEARTBEAT:
                        break;
                    case ServerMessageType.HANDSHAKE_CHALLANGE:
                        HandleHandshakeChallange(message.data);
                        break;
                    case ServerMessageType.HANDSHAKE_REPLY:
                        HandleHandshakeReply(message.data);
                        break;
                    case ServerMessageType.CHAT_MESSAGE:
                        HandleChatMessage(message.data);
                        break;
                    case ServerMessageType.SERVER_SETTINGS:
                        HandleServerSettings(message.data);
                        break;
                    case ServerMessageType.PLAYER_STATUS:
                        HandlePlayerStatus(message.data);
                        break;
                    case ServerMessageType.PLAYER_COLOR:
                        PlayerColorWorker.fetch.HandlePlayerColorMessage(message.data);
                        break;
                    case ServerMessageType.PLAYER_JOIN:
                        HandlePlayerJoin(message.data);
                        break;
                    case ServerMessageType.PLAYER_DISCONNECT:
                        HandlePlayerDisconnect(message.data);
                        break;
                    case ServerMessageType.SCENARIO_DATA:
                        HandleScenarioModuleData(message.data);
                        break;
                    case ServerMessageType.KERBAL_REPLY:
                        HandleKerbalReply(message.data);
                        break;
                    case ServerMessageType.KERBAL_COMPLETE:
                        HandleKerbalComplete();
                        break;
                    case ServerMessageType.VESSEL_LIST:
                        HandleVesselList(message.data);
                        break;
                    case ServerMessageType.VESSEL_PROTO:
                        HandleVesselProto(message.data);
                        break;
                    case ServerMessageType.VESSEL_UPDATE:
                        HandleVesselUpdate(message.data);
                        break;
                    case ServerMessageType.VESSEL_COMPLETE:
                        HandleVesselComplete();
                        break;
                    case ServerMessageType.VESSEL_REMOVE:
                        HandleVesselRemove(message.data);
                        break;
                    case ServerMessageType.CRAFT_LIBRARY:
                        HandleCraftLibrary(message.data);
                        break;
                    case ServerMessageType.SCREENSHOT_LIBRARY:
                        HandleScreenshotLibrary(message.data);
                        break;
                    case ServerMessageType.FLAG_SYNC:
                        FlagSyncer.fetch.HandleMessage(message.data);
                        break;
                    case ServerMessageType.SET_SUBSPACE:
                        WarpWorker.fetch.HandleSetSubspace(message.data);
                        break;
                    case ServerMessageType.SYNC_TIME_REPLY:
                        HandleSyncTimeReply(message.data);
                        break;
                    case ServerMessageType.PING_REPLY:
                        HandlePingReply(message.data);
                        break;
                    case ServerMessageType.MOTD_REPLY:
                        HandleMotdReply(message.data);
                        break;
                    case ServerMessageType.WARP_CONTROL:
                        HandleWarpControl(message.data);
                        break;
                    case ServerMessageType.ADMIN_SYSTEM:
                        AdminSystem.fetch.HandleAdminMessage(message.data);
                        break;
                    case ServerMessageType.LOCK_SYSTEM:
                        LockSystem.fetch.HandleLockMessage(message.data);
                        break;
                    case ServerMessageType.MOD_DATA:
                        DMPModInterface.fetch.HandleModData(message.data);
                        break;
                    case ServerMessageType.SPLIT_MESSAGE:
                        HandleSplitMessage(message.data);
                        break;
                    case ServerMessageType.CONNECTION_END:
                        HandleConnectionEnd(message.data);
                        break;
                    default:
                        DarkLog.Debug("Unhandled message type " + message.type);
                        break;
                }
            }
            catch (Exception e)
            {
                DarkLog.Debug("Error handling message type " + message.type + ", exception: " + e);
                SendDisconnect("Error handling " + message.type + " message");
            }
        }

        private void HandleHandshakeChallange(byte[] messageData)
        {
            try
            {
                using (MessageReader mr = new MessageReader(messageData))
                {
                    byte[] challange = mr.Read<byte[]>();
                    using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(1024))
                    {
                        rsa.PersistKeyInCsp = false;
                        rsa.FromXmlString(Settings.fetch.playerPrivateKey);
                        byte[] signature = rsa.SignData(challange, CryptoConfig.CreateFromName("SHA256"));
                        SendHandshakeResponse(signature);
                        state = ClientState.HANDSHAKING;
                    }
                }
            }
            catch (Exception e)
            {
                DarkLog.Debug("Error handling HANDSHAKE_CHALLANGE message, exception: " + e);
            }
        }

        private void HandleHandshakeReply(byte[] messageData)
        {

            int reply = 0;
            string reason = "";
            string modFileData = "";
            int serverProtocolVersion = -1;
            string serverVersion = "Unknown";
            try
            {
                using (MessageReader mr = new MessageReader(messageData))
                {
                    reply = mr.Read<int>();
                    reason = mr.Read<string>();
                    try
                    {
                        serverProtocolVersion = mr.Read<int>();
                        serverVersion = mr.Read<string>();
                    }
                    catch
                    {
                        //We don't care about this throw on pre-protocol-9 servers.
                    }
                    //If we handshook successfully, the mod data will be available to read.
                    if (reply == 0)
                    {
                        Compression.compressionEnabled = mr.Read<bool>() && Settings.fetch.compressionEnabled;
                        ModWorker.fetch.modControl = (ModControlMode)mr.Read<int>();
                        if (ModWorker.fetch.modControl != ModControlMode.DISABLED)
                        {
                            modFileData = mr.Read<string>();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                DarkLog.Debug("Error handling HANDSHAKE_REPLY message, exception: " + e);
                reply = 99;
                reason = "Incompatible HANDSHAKE_REPLY message";
            }
            switch (reply)
            {
                case 0:
                    {
                        if (ModWorker.fetch.ParseModFile(modFileData))
                        {
                            DarkLog.Debug("Handshake successful");
                            state = ClientState.AUTHENTICATED;
                        }
                        else
                        {
                            DarkLog.Debug("Failed to pass mod validation");
                            SendDisconnect("Failed mod validation");
                        }
                    }
                    break;
                default:
                    string disconnectReason = "Handshake failure: " + reason;
                    //If it's a protocol mismatch, append the client/server version.
                    if (reply == 1)
                    {
                        string clientTrimmedVersion = Common.PROGRAM_VERSION;
                        //Trim git tags
                        if (Common.PROGRAM_VERSION.Length == 40)
                        {
                            clientTrimmedVersion = Common.PROGRAM_VERSION.Substring(0, 7);
                        }
                        string serverTrimmedVersion = serverVersion;
                        if (serverVersion.Length == 40)
                        {
                            serverTrimmedVersion = serverVersion.Substring(0, 7);
                        }
                        disconnectReason += "\nClient: " + clientTrimmedVersion + ", Server: " + serverTrimmedVersion;
                        //If they both aren't a release version, display the actual protocol version.
                        if (!serverVersion.Contains("v") || !Common.PROGRAM_VERSION.Contains("v"))
                        {
                            if (serverProtocolVersion != -1)
                            {
                                disconnectReason += "\nClient protocol: " + Common.PROTOCOL_VERSION + ", Server: " + serverProtocolVersion;
                            }
                            else
                            {
                                disconnectReason += "\nClient protocol: " + Common.PROTOCOL_VERSION + ", Server: 8-";
                            }
                        }
                    }
                    DarkLog.Debug(disconnectReason);
                    Disconnect(disconnectReason);
                    break;
            }
        }

        private void HandleChatMessage(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                ChatMessageType chatMessageType = (ChatMessageType)mr.Read<int>();

                switch (chatMessageType)
                {
                    case ChatMessageType.LIST:
                        {
                            string[] playerList = mr.Read<string[]>();
                            foreach (string playerName in playerList)
                            {
                                string[] channelList = mr.Read<string[]>();
                                foreach (string channelName in channelList)
                                {
                                    ChatWorker.fetch.QueueChatJoin(playerName, channelName);
                                }
                            }
                        }
                        break;
                    case ChatMessageType.JOIN:
                        {
                            string playerName = mr.Read<string>();
                            string channelName = mr.Read<string>();
                            ChatWorker.fetch.QueueChatJoin(playerName, channelName);
                        }
                        break;
                    case ChatMessageType.LEAVE:
                        {
                            string playerName = mr.Read<string>();
                            string channelName = mr.Read<string>();
                            ChatWorker.fetch.QueueChatLeave(playerName, channelName);
                        }
                        break;
                    case ChatMessageType.CHANNEL_MESSAGE:
                        {
                            string playerName = mr.Read<string>();
                            string channelName = mr.Read<string>();
                            string channelMessage = mr.Read<string>();
                            ChatWorker.fetch.QueueChannelMessage(playerName, channelName, channelMessage);
                        }
                        break;
                    case ChatMessageType.PRIVATE_MESSAGE:
                        {
                            string fromPlayer = mr.Read<string>();
                            string toPlayer = mr.Read<string>();
                            string privateMessage = mr.Read<string>();
                            if (toPlayer == Settings.fetch.playerName || fromPlayer == Settings.fetch.playerName)
                            {
                                ChatWorker.fetch.QueuePrivateMessage(fromPlayer, toPlayer, privateMessage);
                            }
                        }
                        break;
                    case ChatMessageType.CONSOLE_MESSAGE:
                        {
                            string message = mr.Read<string>();
                            ChatWorker.fetch.QueueSystemMessage(message);
                        }
                        break;
                }
            }
        }

        private void HandleServerSettings(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                WarpWorker.fetch.warpMode = (WarpMode)mr.Read<int>();
                Client.fetch.gameMode = (GameMode)mr.Read<int>();
                Client.fetch.serverAllowCheats = mr.Read<bool>();
                numberOfKerbals = mr.Read<int>();
                numberOfVessels = mr.Read<int>();
                ScreenshotWorker.fetch.screenshotHeight = mr.Read<int>();
                AsteroidWorker.fetch.maxNumberOfUntrackedAsteroids = mr.Read<int>();
                ChatWorker.fetch.consoleIdentifier = mr.Read<string>();
                Client.fetch.serverDifficulty = (GameDifficulty)mr.Read<int>();
                if (Client.fetch.serverDifficulty != GameDifficulty.CUSTOM)
                {
                    Client.fetch.serverParameters = GameParameters.GetDefaultParameters(Client.fetch.ConvertGameMode(Client.fetch.gameMode), (GameParameters.Preset)Client.fetch.serverDifficulty);
                }
                else
                {
                    GameParameters newParameters = new GameParameters();
                    newParameters.Difficulty.AllowStockVessels = mr.Read<bool>();
                    newParameters.Difficulty.AutoHireCrews = mr.Read<bool>();
                    newParameters.Difficulty.BypassEntryPurchaseAfterResearch = mr.Read<bool>();
                    newParameters.Difficulty.IndestructibleFacilities = mr.Read<bool>();
                    newParameters.Difficulty.MissingCrewsRespawn = mr.Read<bool>();
                    newParameters.Career.FundsGainMultiplier = mr.Read<float>();
                    newParameters.Career.FundsLossMultiplier = mr.Read<float>();
                    newParameters.Career.RepGainMultiplier = mr.Read<float>();
                    newParameters.Career.RepLossMultiplier = mr.Read<float>();
                    newParameters.Career.ScienceGainMultiplier = mr.Read<float>();
                    newParameters.Career.StartingFunds = mr.Read<float>();
                    newParameters.Career.StartingReputation = mr.Read<float>();
                    newParameters.Career.StartingScience = mr.Read<float>();
                    Client.fetch.serverParameters = newParameters;
                }
            }
        }

        private void HandlePlayerStatus(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                string playerName = mr.Read<string>();
                string vesselText = mr.Read<string>();
                string statusText = mr.Read<string>();
                PlayerStatus newStatus = new PlayerStatus();
                newStatus.playerName = playerName;
                newStatus.vesselText = vesselText;
                newStatus.statusText = statusText;
                PlayerStatusWorker.fetch.AddPlayerStatus(newStatus);
            }
        }

        private void HandlePlayerJoin(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                string playerName = mr.Read<string>();
                ChatWorker.fetch.QueueChannelMessage(ChatWorker.fetch.consoleIdentifier, "", playerName + " has joined the server");
            }
        }

        private void HandlePlayerDisconnect(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                string playerName = mr.Read<string>();
                ScenarioWorker.fetch.SaveScenarioOnCall();
                WarpWorker.fetch.RemovePlayer(playerName);
                PlayerStatusWorker.fetch.RemovePlayerStatus(playerName);
                ChatWorker.fetch.QueueRemovePlayer(playerName);
                LockSystem.fetch.ReleasePlayerLocks(playerName);
                ChatWorker.fetch.QueueChannelMessage(ChatWorker.fetch.consoleIdentifier, "", playerName + " has left the server");
            }
        }

        private void HandleSyncTimeReply(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                long clientSend = mr.Read<long>();
                long serverReceive = mr.Read<long>();
                long serverSend = mr.Read<long>();
                TimeSyncer.fetch.HandleSyncTime(clientSend, serverReceive, serverSend);
            }
        }

        private void HandleScenarioModuleData(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                string[] scenarioName = mr.Read<string[]>();
                for (int i = 0; i < scenarioName.Length; i++)
                {
                    byte[] scenarioData = Compression.DecompressIfNeeded(mr.Read<byte[]>());
                    ConfigNode scenarioNode = ConfigNodeSerializer.fetch.Deserialize(scenarioData);
                    if (scenarioNode != null)
                    {
                        ScenarioWorker.fetch.QueueScenarioData(scenarioName[i], scenarioNode);
                    }
                    else
                    {
                        DarkLog.Debug("Scenario data has been lost for " + scenarioName[i]);
                        ScreenMessages.PostScreenMessage("Scenario data has been lost for " + scenarioName[i], 5f, ScreenMessageStyle.UPPER_CENTER);
                    }
                }
            }
        }

        private void HandleKerbalReply(byte[] messageData)
        {
            numberOfKerbalsReceived++;
            using (MessageReader mr = new MessageReader(messageData))
            {
                double planetTime = mr.Read<double>();
                string kerbalName = mr.Read<string>();
                byte[] kerbalData = mr.Read<byte[]>();
                ConfigNode kerbalNode = ConfigNodeSerializer.fetch.Deserialize(kerbalData);
                if (kerbalNode != null)
                {
                    VesselWorker.fetch.QueueKerbal(planetTime, kerbalName, kerbalNode);
                }
                else
                {
                    DarkLog.Debug("Failed to load kerbal!");
                }
            }
            if (state == ClientState.SYNCING_KERBALS)
            {
                if (numberOfKerbals != 0)
                {
                    Client.fetch.status = "Syncing kerbals " + numberOfKerbalsReceived + "/" + numberOfKerbals + " (" + (int)((numberOfKerbalsReceived / (float)numberOfKerbals) * 100) + "%)";
                }
            }
        }

        private void HandleKerbalComplete()
        {
            state = ClientState.KERBALS_SYNCED;
            DarkLog.Debug("Kerbals Synced!");
            Client.fetch.status = "Kerbals synced";
        }

        private void HandleVesselList(byte[] messageData)
        {
            state = ClientState.SYNCING_VESSELS;
            Client.fetch.status = "Syncing vessels";
            using (MessageReader mr = new MessageReader(messageData))
            {
                List<string> serverVessels = new List<string>(mr.Read<string[]>());
                List<string> cacheObjects = new List<string>(UniverseSyncCache.fetch.GetCachedObjects());
                List<string> requestedObjects = new List<string>();
                foreach (string serverVessel in serverVessels)
                {
                    if (!cacheObjects.Contains(serverVessel))
                    {
                        requestedObjects.Add(serverVessel);
                    }
                    else
                    {
                        bool added = false;
                        byte[] vesselBytes = UniverseSyncCache.fetch.GetFromCache(serverVessel);
                        if (vesselBytes.Length != 0)
                        {
                            ConfigNode vesselNode = ConfigNodeSerializer.fetch.Deserialize(vesselBytes);
                            if (vesselNode != null)
                            {
                                string vesselIDString = Common.ConvertConfigStringToGUIDString(vesselNode.GetValue("pid"));
                                if (vesselIDString != null)
                                {
                                    Guid vesselID = new Guid(vesselIDString);
                                    if (vesselID != Guid.Empty)
                                    {
                                        VesselWorker.fetch.QueueVesselProto(vesselID, 0, vesselNode);
                                        added = true;
                                        numberOfVesselsReceived++;
                                    }
                                    else
                                    {
                                        DarkLog.Debug("Cached object " + serverVessel + " is damaged - Returned GUID.Empty");
                                    }
                                }
                                else
                                {
                                    DarkLog.Debug("Cached object " + serverVessel + " is damaged - Failed to get vessel ID");
                                }
                            }
                            else
                            {
                                DarkLog.Debug("Cached object " + serverVessel + " is damaged - Failed to create a config node");
                            }
                        }
                        else
                        {
                            DarkLog.Debug("Cached object " + serverVessel + " is damaged - Object is a 0 length file!");
                        }
                        if (!added)
                        {
                            requestedObjects.Add(serverVessel);
                        }
                    }
                }
                if (numberOfVessels != 0)
                {
                    Client.fetch.status = "Syncing vessels " + numberOfVesselsReceived + "/" + numberOfVessels + " (" + (int)((numberOfVesselsReceived / (float)numberOfVessels) * 100) + "%)";
                }
                SendVesselsRequest(requestedObjects.ToArray());
            }
        }

        private void HandleVesselProto(byte[] messageData)
        {
            numberOfVesselsReceived++;
            using (MessageReader mr = new MessageReader(messageData))
            {
                double planetTime = mr.Read<double>();
                string vesselID = mr.Read<string>();
                //Docking - don't care.
                mr.Read<bool>();
                //Flying - don't care.
                mr.Read<bool>();
                byte[] vesselData = Compression.DecompressIfNeeded(mr.Read<byte[]>());
                UniverseSyncCache.fetch.QueueToCache(vesselData);
                ConfigNode vesselNode = ConfigNodeSerializer.fetch.Deserialize(vesselData);
                if (vesselNode != null)
                {
                    string configGuid = vesselNode.GetValue("pid");
                    if (!String.IsNullOrEmpty(configGuid) && vesselID == Common.ConvertConfigStringToGUIDString(configGuid))
                    {
                        VesselWorker.fetch.QueueVesselProto(new Guid(vesselID), planetTime, vesselNode);
                    }
                    else
                    {
                        DarkLog.Debug("Failed to load vessel " + vesselID + "!");
                    }
                }
                else
                {
                    DarkLog.Debug("Failed to load vessel" + vesselID + "!");
                }
            }
            if (state == ClientState.SYNCING_VESSELS)
            {
                if (numberOfVessels != 0)
                {
                    if (numberOfVesselsReceived > numberOfVessels)
                    {
                        //Received 102 / 101 vessels!
                        numberOfVessels = numberOfVesselsReceived;
                    }
                    Client.fetch.status = "Syncing vessels " + numberOfVesselsReceived + "/" + numberOfVessels + " (" + (int)((numberOfVesselsReceived / (float)numberOfVessels) * 100) + "%)";
                }
            }
        }

        private void HandleVesselUpdate(byte[] messageData)
        {
            VesselUpdate update = new VesselUpdate();
            using (MessageReader mr = new MessageReader(messageData))
            {
                update.planetTime = mr.Read<double>();
                update.vesselID = new Guid(mr.Read<string>());
                update.bodyName = mr.Read<string>();
                update.rotation = mr.Read<float[]>();
                update.angularVelocity = mr.Read<float[]>();
                //FlightState variables
                update.flightState = new FlightCtrlState();
                update.flightState.mainThrottle = mr.Read<float>();
                update.flightState.wheelThrottleTrim = mr.Read<float>();
                update.flightState.X = mr.Read<float>();
                update.flightState.Y = mr.Read<float>();
                update.flightState.Z = mr.Read<float>();
                update.flightState.killRot = mr.Read<bool>();
                update.flightState.gearUp = mr.Read<bool>();
                update.flightState.gearDown = mr.Read<bool>();
                update.flightState.headlight = mr.Read<bool>();
                update.flightState.wheelThrottle = mr.Read<float>();
                update.flightState.roll = mr.Read<float>();
                update.flightState.yaw = mr.Read<float>();
                update.flightState.pitch = mr.Read<float>();
                update.flightState.rollTrim = mr.Read<float>();
                update.flightState.yawTrim = mr.Read<float>();
                update.flightState.pitchTrim = mr.Read<float>();
                update.flightState.wheelSteer = mr.Read<float>();
                update.flightState.wheelSteerTrim = mr.Read<float>();
                //Action group controls
                update.actiongroupControls = mr.Read<bool[]>();
                //Position/velocity
                update.isSurfaceUpdate = mr.Read<bool>();
                if (update.isSurfaceUpdate)
                {
                    update.position = mr.Read<double[]>();
                    update.velocity = mr.Read<double[]>();
                    update.acceleration = mr.Read<double[]>();
                    update.terrainNormal = mr.Read<float[]>();
                }
                else
                {
                    update.orbit = mr.Read<double[]>();
                }
                VesselWorker.fetch.QueueVesselUpdate(update);
            }
        }

        private void HandleSetActiveVessel(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                string player = mr.Read<string>();
                Guid vesselID = new Guid(mr.Read<string>());
                VesselWorker.fetch.QueueActiveVessel(player, vesselID);
            }
        }

        private void HandleVesselComplete()
        {
            state = ClientState.VESSELS_SYNCED;
        }

        private void HandleVesselRemove(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                double planetTime = mr.Read<double>();
                Guid vesselID = new Guid(mr.Read<string>());
                bool isDockingUpdate = mr.Read<bool>();
                string dockingPlayer = null;
                if (isDockingUpdate)
                {
                    DarkLog.Debug("Got a docking update!");
                    dockingPlayer = mr.Read<string>();
                }
                else
                {
                    DarkLog.Debug("Got removal command for vessel " + vesselID);
                }
                VesselWorker.fetch.QueueVesselRemove(vesselID, planetTime, isDockingUpdate, dockingPlayer);
            }
        }

        private void HandleCraftLibrary(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                CraftMessageType messageType = (CraftMessageType)mr.Read<int>();
                switch (messageType)
                {
                    case CraftMessageType.LIST:
                        {
                            string[] playerList = mr.Read<string[]>();
                            foreach (string player in playerList)
                            {
                                bool vabExists = mr.Read<bool>();
                                bool sphExists = mr.Read<bool>();
                                bool subassemblyExists = mr.Read<bool>();
                                DarkLog.Debug("Player: " + player + ", VAB: " + vabExists + ", SPH: " + sphExists + ", SUBASSEMBLY" + subassemblyExists);
                                if (vabExists)
                                {
                                    string[] vabCrafts = mr.Read<string[]>();
                                    foreach (string vabCraft in vabCrafts)
                                    {
                                        CraftChangeEntry cce = new CraftChangeEntry();
                                        cce.playerName = player;
                                        cce.craftType = CraftType.VAB;
                                        cce.craftName = vabCraft;
                                        CraftLibraryWorker.fetch.QueueCraftAdd(cce);
                                    }
                                }
                                if (sphExists)
                                {
                                    string[] sphCrafts = mr.Read<string[]>();
                                    foreach (string sphCraft in sphCrafts)
                                    {
                                        CraftChangeEntry cce = new CraftChangeEntry();
                                        cce.playerName = player;
                                        cce.craftType = CraftType.SPH;
                                        cce.craftName = sphCraft;
                                        CraftLibraryWorker.fetch.QueueCraftAdd(cce);
                                    }
                                }
                                if (subassemblyExists)
                                {
                                    string[] subassemblyCrafts = mr.Read<string[]>();
                                    foreach (string subassemblyCraft in subassemblyCrafts)
                                    {
                                        CraftChangeEntry cce = new CraftChangeEntry();
                                        cce.playerName = player;
                                        cce.craftType = CraftType.SUBASSEMBLY;
                                        cce.craftName = subassemblyCraft;
                                        CraftLibraryWorker.fetch.QueueCraftAdd(cce);
                                    }
                                }
                            }
                        }
                        break;
                    case CraftMessageType.ADD_FILE:
                        {
                            CraftChangeEntry cce = new CraftChangeEntry();
                            cce.playerName = mr.Read<string>();
                            cce.craftType = (CraftType)mr.Read<int>();
                            cce.craftName = mr.Read<string>();
                            CraftLibraryWorker.fetch.QueueCraftAdd(cce);
                            ChatWorker.fetch.QueueChannelMessage(ChatWorker.fetch.consoleIdentifier, "", cce.playerName + " shared " + cce.craftName + " (" + cce.craftType + ")");
                        }
                        break;
                    case CraftMessageType.DELETE_FILE:
                        {
                            CraftChangeEntry cce = new CraftChangeEntry();
                            cce.playerName = mr.Read<string>();
                            cce.craftType = (CraftType)mr.Read<int>();
                            cce.craftName = mr.Read<string>();
                            CraftLibraryWorker.fetch.QueueCraftDelete(cce);
                        }
                        break;
                    case CraftMessageType.RESPOND_FILE:
                        {
                            CraftResponseEntry cre = new CraftResponseEntry();
                            cre.playerName = mr.Read<string>();
                            cre.craftType = (CraftType)mr.Read<int>();
                            cre.craftName = mr.Read<string>();
                            bool hasCraft = mr.Read<bool>();
                            if (hasCraft)
                            {
                                cre.craftData = mr.Read<byte[]>();
                                CraftLibraryWorker.fetch.QueueCraftResponse(cre);
                            }
                            else
                            {
                                ScreenMessages.PostScreenMessage("Craft " + cre.craftName + " from " + cre.playerName + " not available", 5f, ScreenMessageStyle.UPPER_CENTER);
                            }
                        }
                        break;
                }
            }
        }

        private void HandleScreenshotLibrary(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                ScreenshotMessageType messageType = (ScreenshotMessageType)mr.Read<int>();
                switch (messageType)
                {
                    case ScreenshotMessageType.SEND_START_NOTIFY:
                        {
                            string fromPlayer = mr.Read<string>();
                            ScreenshotWorker.fetch.downloadingScreenshotFromPlayer = fromPlayer;
                        }
                        break;
                    case ScreenshotMessageType.NOTIFY:
                        {
                            string fromPlayer = mr.Read<string>();
                            ScreenshotWorker.fetch.QueueNewNotify(fromPlayer);
                        }
                        break;
                    case ScreenshotMessageType.SCREENSHOT:
                        {
                            string fromPlayer = mr.Read<string>();
                            byte[] screenshotData = mr.Read<byte[]>();
                            ScreenshotWorker.fetch.QueueNewScreenshot(fromPlayer, screenshotData);
                        }
                        break;
                    case ScreenshotMessageType.WATCH:
                        {
                            string fromPlayer = mr.Read<string>();
                            string watchPlayer = mr.Read<string>();
                            ScreenshotWorker.fetch.QueueNewScreenshotWatch(fromPlayer, watchPlayer);
                        }
                        break;

                }
            }
        }

        private void HandlePingReply(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                int pingTime = (int)((DateTime.UtcNow.Ticks - mr.Read<long>()) / 10000f);
                ChatWorker.fetch.QueueChannelMessage(ChatWorker.fetch.consoleIdentifier, "", "Ping: " + pingTime + "ms.");
            }

        }

        private void HandleMotdReply(byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                serverMotd = mr.Read<string>();
                if (serverMotd != "")
                {
                    displayMotd = true;
                    ChatWorker.fetch.QueueChannelMessage(ChatWorker.fetch.consoleIdentifier, "", serverMotd);
                }
            }
        }

        private void HandleWarpControl(byte[] messageData)
        {
            WarpWorker.fetch.QueueWarpMessage(messageData);
        }

        private void HandleSplitMessage(byte[] messageData)
        {
            if (!isReceivingSplitMessage)
            {
                //New split message
                using (MessageReader mr = new MessageReader(messageData))
                {
                    receiveSplitMessage = new ServerMessage();
                    receiveSplitMessage.type = (ServerMessageType)mr.Read<int>();
                    receiveSplitMessage.data = new byte[mr.Read<int>()];
                    receiveSplitMessageBytesLeft = receiveSplitMessage.data.Length;
                    byte[] firstSplitData = mr.Read<byte[]>();
                    firstSplitData.CopyTo(receiveSplitMessage.data, 0);
                    receiveSplitMessageBytesLeft -= firstSplitData.Length;
                }
                isReceivingSplitMessage = true;
            }
            else
            {
                //Continued split message
                messageData.CopyTo(receiveSplitMessage.data, receiveSplitMessage.data.Length - receiveSplitMessageBytesLeft);
                receiveSplitMessageBytesLeft -= messageData.Length;
            }
            if (receiveSplitMessageBytesLeft == 0)
            {
                HandleMessage(receiveSplitMessage);
                receiveSplitMessage = null;
                isReceivingSplitMessage = false;
            }
        }

        private void HandleConnectionEnd(byte[] messageData)
        {
            string reason = "";
            using (MessageReader mr = new MessageReader(messageData))
            {
                reason = mr.Read<string>();
            }
            Disconnect("Server closed connection: " + reason);
        }

        #endregion

        #region Message Sending

        private void SendHeartBeat()
        {
            if (state >= ClientState.CONNECTED && sendMessageQueueHigh.Count == 0)
            {
                if ((UnityEngine.Time.realtimeSinceStartup - lastSendTime) > (Common.HEART_BEAT_INTERVAL / 1000))
                {
                    lastSendTime = UnityEngine.Time.realtimeSinceStartup;
                    ClientMessage newMessage = new ClientMessage();
                    newMessage.type = ClientMessageType.HEARTBEAT;
                    QueueOutgoingMessage(newMessage, true);
                }
            }
        }

        private void SendHandshakeResponse(byte[] signature)
        {
            byte[] messageBytes;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<int>(Common.PROTOCOL_VERSION);
                mw.Write<string>(Settings.fetch.playerName);
                mw.Write<string>(Settings.fetch.playerPublicKey);
                mw.Write<byte[]>(signature);
                mw.Write<string>(Common.PROGRAM_VERSION);
                mw.Write<bool>(Settings.fetch.compressionEnabled);
                messageBytes = mw.GetMessageBytes();
            }
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.HANDSHAKE_RESPONSE;
            newMessage.data = messageBytes;
            QueueOutgoingMessage(newMessage, true);
        }
        //Called from ChatWindow
        public void SendChatMessage(byte[] messageData)
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.CHAT_MESSAGE;
            newMessage.data = messageData;
            QueueOutgoingMessage(newMessage, true);
        }
        //Called from PlayerStatusWorker
        public void SendPlayerStatus(PlayerStatus playerStatus)
        {
            byte[] messageBytes;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<string>(playerStatus.playerName);
                mw.Write<string>(playerStatus.vesselText);
                mw.Write<string>(playerStatus.statusText);
                messageBytes = mw.GetMessageBytes();
            }
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.PLAYER_STATUS;
            newMessage.data = messageBytes;
            QueueOutgoingMessage(newMessage, true);
        }
        //Called from PlayerColorWorker
        public void SendPlayerColorMessage(byte[] messageData)
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.PLAYER_COLOR;
            newMessage.data = messageData;
            QueueOutgoingMessage(newMessage, false);
        }
        //Called from timeSyncer
        public void SendTimeSync()
        {
            byte[] messageBytes;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<long>(DateTime.UtcNow.Ticks);
                messageBytes = mw.GetMessageBytes();
            }
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.SYNC_TIME_REQUEST;
            newMessage.data = messageBytes;
            QueueOutgoingMessage(newMessage, true);
        }

        private void SendKerbalsRequest()
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.KERBALS_REQUEST;
            QueueOutgoingMessage(newMessage, true);
        }

        private void SendVesselsRequest(string[] requestList)
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.VESSELS_REQUEST;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<string[]>(requestList);
                newMessage.data = mw.GetMessageBytes();
            }
            QueueOutgoingMessage(newMessage, true);
        }

        private bool VesselHasNaNPosition(ProtoVessel pv)
        {
            if (pv.landed || pv.splashed)
            {
                //Ground checks
                if (double.IsNaN(pv.latitude) || double.IsInfinity(pv.latitude))
                {
                    return true;
                }
                if (double.IsNaN(pv.longitude) || double.IsInfinity(pv.longitude))
                {
                    return true;
                }
                if (double.IsNaN(pv.altitude) || double.IsInfinity(pv.altitude))
                {
                    return true;
                }
            }
            else
            {
                //Orbit checks
                if (double.IsNaN(pv.orbitSnapShot.argOfPeriapsis) || double.IsInfinity(pv.orbitSnapShot.argOfPeriapsis))
                {
                    return true;
                }
                if (double.IsNaN(pv.orbitSnapShot.eccentricity) || double.IsInfinity(pv.orbitSnapShot.eccentricity))
                {
                    return true;
                }
                if (double.IsNaN(pv.orbitSnapShot.epoch) || double.IsInfinity(pv.orbitSnapShot.epoch))
                {
                    return true;
                }
                if (double.IsNaN(pv.orbitSnapShot.inclination) || double.IsInfinity(pv.orbitSnapShot.inclination))
                {
                    return true;
                }
                if (double.IsNaN(pv.orbitSnapShot.LAN) || double.IsInfinity(pv.orbitSnapShot.LAN))
                {
                    return true;
                }
                if (double.IsNaN(pv.orbitSnapShot.meanAnomalyAtEpoch) || double.IsInfinity(pv.orbitSnapShot.meanAnomalyAtEpoch))
                {
                    return true;
                }
                if (double.IsNaN(pv.orbitSnapShot.semiMajorAxis) || double.IsInfinity(pv.orbitSnapShot.semiMajorAxis))
                {
                    return true;
                }
            }
            
            return false;
        }

        //Called from vesselWorker
        public void SendVesselProtoMessage(ProtoVessel vessel, bool isDockingUpdate, bool isFlyingUpdate)
        {
            //Defend against NaN orbits
            if (VesselHasNaNPosition(vessel))
            {
                DarkLog.Debug("Vessel " + vessel.vesselID + " has NaN position");
                return;
            }
            foreach (ProtoPartSnapshot pps in vessel.protoPartSnapshots)
            {
                foreach (ProtoCrewMember pcm in pps.protoModuleCrew.ToArray())
                {
                    if (pcm.type == ProtoCrewMember.KerbalType.Tourist)
                    {
                        pps.protoModuleCrew.Remove(pcm);
                    }
                }
            }
            ConfigNode vesselNode = new ConfigNode();
            vessel.Save(vesselNode);
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.VESSEL_PROTO;
            byte[] vesselBytes = ConfigNodeSerializer.fetch.Serialize(vesselNode);
            File.WriteAllBytes(Path.Combine(KSPUtil.ApplicationRootPath, "lastVessel.txt"), vesselBytes);
            if (vesselBytes != null && vesselBytes.Length > 0)
            {
                UniverseSyncCache.fetch.QueueToCache(vesselBytes);
                using (MessageWriter mw = new MessageWriter())
                {
                    mw.Write<double>(Planetarium.GetUniversalTime());
                    mw.Write<string>(vessel.vesselID.ToString());
                    mw.Write<bool>(isDockingUpdate);
                    mw.Write<bool>(isFlyingUpdate);
                    mw.Write<byte[]>(Compression.CompressIfNeeded(vesselBytes));
                    newMessage.data = mw.GetMessageBytes();
                }
                DarkLog.Debug("Sending vessel " + vessel.vesselID + ", name " + vessel.vesselName + ", type: " + vessel.vesselType + ", size: " + newMessage.data.Length);
                QueueOutgoingMessage(newMessage, false);
            }
            else
            {
                DarkLog.Debug("Failed to create byte[] data for " + vessel.vesselID);
            }
        }
        //Called from vesselWorker
        public void SendVesselUpdate(VesselUpdate update)
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.VESSEL_UPDATE;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<double>(update.planetTime);
                mw.Write<string>(update.vesselID.ToString());
                mw.Write<string>(update.bodyName);
                mw.Write<float[]>(update.rotation);
                //mw.Write<float[]>(update.vesselForward);
                //mw.Write<float[]>(update.vesselUp);
                mw.Write<float[]>(update.angularVelocity);
                //FlightState variables
                mw.Write<float>(update.flightState.mainThrottle);
                mw.Write<float>(update.flightState.wheelThrottleTrim);
                mw.Write<float>(update.flightState.X);
                mw.Write<float>(update.flightState.Y);
                mw.Write<float>(update.flightState.Z);
                mw.Write<bool>(update.flightState.killRot);
                mw.Write<bool>(update.flightState.gearUp);
                mw.Write<bool>(update.flightState.gearDown);
                mw.Write<bool>(update.flightState.headlight);
                mw.Write<float>(update.flightState.wheelThrottle);
                mw.Write<float>(update.flightState.roll);
                mw.Write<float>(update.flightState.yaw);
                mw.Write<float>(update.flightState.pitch);
                mw.Write<float>(update.flightState.rollTrim);
                mw.Write<float>(update.flightState.yawTrim);
                mw.Write<float>(update.flightState.pitchTrim);
                mw.Write<float>(update.flightState.wheelSteer);
                mw.Write<float>(update.flightState.wheelSteerTrim);
                //Action group controls
                mw.Write<bool[]>(update.actiongroupControls);
                //Position/velocity
                mw.Write<bool>(update.isSurfaceUpdate);
                if (update.isSurfaceUpdate)
                {
                    mw.Write<double[]>(update.position);
                    mw.Write<double[]>(update.velocity);
                    mw.Write<double[]>(update.acceleration);
                    mw.Write<float[]>(update.terrainNormal);
                }
                else
                {
                    mw.Write<double[]>(update.orbit);
                }
                newMessage.data = mw.GetMessageBytes();
            }
            QueueOutgoingMessage(newMessage, false);
        }
        //Called from vesselWorker
        public void SendVesselRemove(Guid vesselID, bool isDockingUpdate)
        {
            DarkLog.Debug("Removing " + vesselID + " from the server");
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.VESSEL_REMOVE;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<double>(Planetarium.GetUniversalTime());
                mw.Write<string>(vesselID.ToString());
                mw.Write<bool>(isDockingUpdate);
                if (isDockingUpdate)
                {
                    mw.Write<string>(Settings.fetch.playerName);
                }
                newMessage.data = mw.GetMessageBytes();
            }
            QueueOutgoingMessage(newMessage, false);
        }
        //Called fro craftLibraryWorker
        public void SendCraftLibraryMessage(byte[] messageData)
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.CRAFT_LIBRARY;
            newMessage.data = messageData;
            QueueOutgoingMessage(newMessage, false);
        }
        //Called from ScreenshotWorker
        public void SendScreenshotMessage(byte[] messageData)
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.SCREENSHOT_LIBRARY;
            newMessage.data = messageData;
            QueueOutgoingMessage(newMessage, false);
        }
        //Called from ScenarioWorker
        public void SendScenarioModuleData(string[] scenarioNames, byte[][] scenarioData)
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.SCENARIO_DATA;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<string[]>(scenarioNames);
                foreach (byte[] scenarioBytes in scenarioData)
                {
                    mw.Write<byte[]>(Compression.CompressIfNeeded(scenarioBytes));
                }
                newMessage.data = mw.GetMessageBytes();
            }
            DarkLog.Debug("Sending " + scenarioNames.Length + " scenario modules");
            QueueOutgoingMessage(newMessage, false);
        }

        public void SendKerbalProtoMessage(string kerbalName, byte[] kerbalBytes)
        {
            if (kerbalBytes != null && kerbalBytes.Length > 0)
            {
                ClientMessage newMessage = new ClientMessage();
                newMessage.type = ClientMessageType.KERBAL_PROTO;
                using (MessageWriter mw = new MessageWriter())
                {
                    mw.Write<double>(Planetarium.GetUniversalTime());
                    mw.Write<string>(kerbalName);
                    mw.Write<byte[]>(kerbalBytes);
                    newMessage.data = mw.GetMessageBytes();
                }
                DarkLog.Debug("Sending kerbal " + kerbalName + ", size: " + newMessage.data.Length);
                QueueOutgoingMessage(newMessage, false);
            }
            else
            {
                DarkLog.Debug("Failed to create byte[] data for kerbal " + kerbalName);
            }
        }
        //Called from chatWorker
        public void SendPingRequest()
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.PING_REQUEST;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<long>(DateTime.UtcNow.Ticks);
                newMessage.data = mw.GetMessageBytes();
            }
            QueueOutgoingMessage(newMessage, true);
        }
        //Called from networkWorker
        public void SendMotdRequest()
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.MOTD_REQUEST;
            QueueOutgoingMessage(newMessage, true);
        }
        //Called from FlagSyncer
        public void SendFlagMessage(byte[] messageData)
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.FLAG_SYNC;
            newMessage.data = messageData;
            QueueOutgoingMessage(newMessage, false);
        }
        //Called from warpWorker
        public void SendWarpMessage(byte[] messageData)
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.WARP_CONTROL;
            newMessage.data = messageData;
            QueueOutgoingMessage(newMessage, true);
        }
        //Called from lockSystem
        public void SendLockSystemMessage(byte[] messageData)
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.LOCK_SYSTEM;
            newMessage.data = messageData;
            QueueOutgoingMessage(newMessage, true);
        }

        /// <summary>
        /// If you are a mod, call DMPModInterface.fetch.SendModMessage.
        /// </summary>
        public void SendModMessage(byte[] messageData, bool highPriority)
        {
            ClientMessage newMessage = new ClientMessage();
            newMessage.type = ClientMessageType.MOD_DATA;
            newMessage.data = messageData;
            QueueOutgoingMessage(newMessage, highPriority);
        }
        //Called from main
        public void SendDisconnect(string disconnectReason = "Unknown")
        {
            if (state != ClientState.DISCONNECTING && state >= ClientState.CONNECTED)
            {
                DarkLog.Debug("Sending disconnect message, reason: " + disconnectReason);
                Client.fetch.status = "Disconnected: " + disconnectReason;
                state = ClientState.DISCONNECTING;
                byte[] messageBytes;
                using (MessageWriter mw = new MessageWriter())
                {
                    mw.Write<string>(disconnectReason);
                    messageBytes = mw.GetMessageBytes();
                }
                ClientMessage newMessage = new ClientMessage();
                newMessage.type = ClientMessageType.CONNECTION_END;
                newMessage.data = messageBytes;
                QueueOutgoingMessage(newMessage, true);
            }
        }

        public long GetStatistics(string statType)
        {
            switch (statType)
            {
                case "HighPriorityQueueLength":
                    return sendMessageQueueHigh.Count;
                case "SplitPriorityQueueLength":
                    return sendMessageQueueSplit.Count;
                case "LowPriorityQueueLength":
                    return sendMessageQueueLow.Count;
                case "QueuedOutBytes":
                    return bytesQueuedOut;
                case "SentBytes":
                    return bytesSent;
                case "ReceivedBytes":
                    return bytesReceived;
                case "LastReceiveTime":
                    return (int)((UnityEngine.Time.realtimeSinceStartup - lastReceiveTime) * 1000);
                case "LastSendTime":
                    return (int)((UnityEngine.Time.realtimeSinceStartup - lastSendTime) * 1000);
            }
            return 0;
        }

        #endregion
    }

    class DMPServerAddress
    {
        public string ip;
        public int port;
    }
}

