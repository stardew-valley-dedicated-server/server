using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using JunimoServer.Services.CabinManager;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;
using StardewValley.Network.Compress;
using StardewValley.SDKs.GogGalaxy;
using Steamworks;

namespace JunimoServer.Services.SteamGameServer
{
    /// <summary>
    /// Steam networking server using GameServer APIs for headless dedicated server operation.
    /// Runs alongside GalaxyNetServer - Steam clients connect here, GOG clients use Galaxy P2P.
    ///
    /// Key points:
    /// - Uses SteamGameServerNetworkingSockets (not SteamNetworkingSockets which requires Steam client)
    /// - Connects to Valve's SDR relay network for NAT traversal (~99% success vs ~50% for Galaxy P2P)
    /// - Steam lobby managed separately via SteamKit2 in steam-auth container (not in this class)
    /// - SteamFriends API unavailable in GameServer mode - display names fetched from connection info
    /// </summary>
    internal sealed class SteamGameServerNetServer : HookableServer
    {
        private const int ServerBufferSize = 256;

        // Message type constants (from Multiplayer class)
        private const byte MessageType_PlayerIntroduction = 2;
        private const byte MessageType_ForceKick = 23;

        private static IMonitor _monitor;
        private static IModHelper _helper;

        // Cached netCompression reference (accessed via reflection)
        private static INetCompression _netCompression;

        /// <summary>Connection status changed callback.</summary>
        private Callback<SteamNetConnectionStatusChangedCallback_t> _connectionStatusChangedCallback;

        /// <summary>Connection data by connection handle.</summary>
        private Dictionary<HSteamNetConnection, ConnectionData> _connectionDataMap;

        /// <summary>Connection data by farmer ID.</summary>
        private Dictionary<long, ConnectionData> _farmerConnectionMap;

        /// <summary>Connection data by Steam ID (for O(1) lookup).</summary>
        private Dictionary<ulong, ConnectionData> _steamIdConnectionMap;

        /// <summary>Connections that recently joined (for poll group transitions).</summary>
        private HashSet<HSteamNetConnection> _recentlyJoined;

        /// <summary>Message buffer for receiving.</summary>
        private readonly IntPtr[] _messages = new IntPtr[ServerBufferSize];

        /// <summary>P2P listen socket handle.</summary>
        private HSteamListenSocket _listenSocket = HSteamListenSocket.Invalid;

        /// <summary>Poll group for joining clients (no farmhand selected yet).</summary>
        private HSteamNetPollGroup _joiningGroup = HSteamNetPollGroup.Invalid;

        /// <summary>Poll group for active farmhand connections.</summary>
        private HSteamNetPollGroup _farmhandGroup = HSteamNetPollGroup.Invalid;

        /// <summary>Reference to the game server for callbacks.</summary>
        private readonly IGameServer _gameServer;

        public override int connectionsCount => _connectionDataMap?.Count ?? 0;

        public SteamGameServerNetServer(IGameServer gameServer, IMonitor monitor, IModHelper helper) : base(gameServer)
        {
            _gameServer = gameServer;
            _monitor = monitor;
            _helper = helper;

            // Get netCompression via reflection (it's internal)
            if (_netCompression == null)
            {
                _netCompression = _helper.Reflection.GetField<INetCompression>(typeof(Program), "netCompression").GetValue();
            }
        }

        public override void initialize()
        {
            if (!SteamGameServerService.IsInitialized)
            {
                _monitor.Log("Cannot initialize SteamGameServerNetServer: GameServer not initialized", LogLevel.Error);
                return;
            }

            _monitor.Log("Starting Steam GameServer networking", LogLevel.Info);

            // Set up callbacks (GameServer version)
            _connectionStatusChangedCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnConnectionStatusChanged);

            // Initialize data structures
            _connectionDataMap = new Dictionary<HSteamNetConnection, ConnectionData>();
            _farmerConnectionMap = new Dictionary<long, ConnectionData>();
            _steamIdConnectionMap = new Dictionary<ulong, ConnectionData>();
            _recentlyJoined = new HashSet<HSteamNetConnection>();

            _listenSocket = HSteamListenSocket.Invalid;
            _joiningGroup = HSteamNetPollGroup.Invalid;
            _farmhandGroup = HSteamNetPollGroup.Invalid;

            // Create P2P listen socket using GameServer networking sockets
            var options = GetNetworkingOptions();
            _listenSocket = SteamGameServerNetworkingSockets.CreateListenSocketP2P(0, options.Length, options);

            if (_listenSocket == HSteamListenSocket.Invalid)
            {
                _monitor.Log("Failed to create P2P listen socket", LogLevel.Error);
                return;
            }

            // Create poll groups
            _joiningGroup = SteamGameServerNetworkingSockets.CreatePollGroup();
            _farmhandGroup = SteamGameServerNetworkingSockets.CreatePollGroup();

            _monitor.Log($"Steam GameServer P2P listen socket created, Server Steam ID: {SteamGameServerService.ServerSteamId.m_SteamID}", LogLevel.Info);
        }

        public override void stopServer()
        {
            _monitor.Log("Stopping Steam GameServer networking", LogLevel.Info);

            // Close all connections - copy to list first to avoid modification during iteration
            if (_connectionDataMap != null)
            {
                var connections = new List<ConnectionData>(_connectionDataMap.Values);
                foreach (var conn in connections)
                {
                    CloseConnection(conn.Connection);
                }
            }

            // Destroy poll groups
            if (_joiningGroup != HSteamNetPollGroup.Invalid)
            {
                SteamGameServerNetworkingSockets.DestroyPollGroup(_joiningGroup);
                _joiningGroup = HSteamNetPollGroup.Invalid;
            }

            if (_farmhandGroup != HSteamNetPollGroup.Invalid)
            {
                SteamGameServerNetworkingSockets.DestroyPollGroup(_farmhandGroup);
                _farmhandGroup = HSteamNetPollGroup.Invalid;
            }

            // Close listen socket
            if (_listenSocket != HSteamListenSocket.Invalid)
            {
                SteamGameServerNetworkingSockets.CloseListenSocket(_listenSocket);
                _listenSocket = HSteamListenSocket.Invalid;
            }

            _connectionStatusChangedCallback?.Unregister();
        }

        public override bool connected()
        {
            return _listenSocket != HSteamListenSocket.Invalid &&
                   _joiningGroup != HSteamNetPollGroup.Invalid &&
                   _farmhandGroup != HSteamNetPollGroup.Invalid;
        }

        public override void receiveMessages()
        {
            if (!connected())
                return;

            PollJoiningMessages();
            PollFarmhandMessages();

            // Flush all connection messages
            foreach (var kvp in _connectionDataMap)
            {
                SteamGameServerNetworkingSockets.FlushMessagesOnConnection(kvp.Value.Connection);
            }
        }

        private void PollJoiningMessages()
        {
            _recentlyJoined.Clear();
            int numMessages = SteamGameServerNetworkingSockets.ReceiveMessagesOnPollGroup(_joiningGroup, _messages, ServerBufferSize);

            for (int i = 0; i < numMessages; i++)
            {
                IncomingMessage message = new IncomingMessage();
                ProcessSteamMessage(_messages[i], message, out var messageConnection);

                if (!_connectionDataMap.TryGetValue(messageConnection, out var connectionData))
                {
                    _monitor.Log("Message from invalid connection", LogLevel.Warn);
                    ShutdownConnection(messageConnection);
                    continue;
                }

                bool isRecentlyJoined = _recentlyJoined.Contains(messageConnection);
                if (connectionData.Online && !isRecentlyJoined)
                {
                    _monitor.Log($"Online farmhand {connectionData.FarmerId} in wrong poll group", LogLevel.Warn);
                    ShutdownConnection(messageConnection);
                    continue;
                }

                base.OnProcessingMessage(message,
                    (outgoing) => SendMessageToConnection(messageConnection, outgoing),
                    () =>
                    {
                        if (isRecentlyJoined)
                        {
                            _gameServer.processIncomingMessage(message);
                        }
                        else if (message.MessageType == MessageType_PlayerIntroduction)
                        {
                            HandleFarmhandRequest(message, connectionData);
                        }
                    });
            }
        }

        private void PollFarmhandMessages()
        {
            int numMessages = SteamGameServerNetworkingSockets.ReceiveMessagesOnPollGroup(_farmhandGroup, _messages, ServerBufferSize);

            for (int i = 0; i < numMessages; i++)
            {
                IncomingMessage message = new IncomingMessage();
                ProcessSteamMessage(_messages[i], message, out var messageConnection);

                if (message.MessageType == MessageType_PlayerIntroduction)
                {
                    _monitor.Log("Farmhand request in wrong poll group", LogLevel.Warn);
                    ShutdownConnection(messageConnection);
                    continue;
                }

                if (!_connectionDataMap.TryGetValue(messageConnection, out var value))
                {
                    _monitor.Log("Message from invalid connection", LogLevel.Warn);
                    ShutdownConnection(messageConnection);
                    continue;
                }

                if (!value.Online)
                {
                    _monitor.Log("Non-farmhand connection in wrong poll group", LogLevel.Warn);
                    ShutdownConnection(messageConnection);
                    continue;
                }

                base.OnProcessingMessage(message,
                    (outgoing) => SendMessageToConnection(messageConnection, outgoing),
                    () => _gameServer.processIncomingMessage(message));
            }
        }

        private void HandleFarmhandRequest(IncomingMessage message, ConnectionData connectionData)
        {
            var multiplayer = _helper.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();
            NetFarmerRoot netFarmerRoot = multiplayer.readFarmer(message.Reader);
            long farmerId = netFarmerRoot.Value.UniqueMultiplayerID;

            _monitor.Log($"Farmhand request from {connectionData.SteamId.m_SteamID} for {farmerId}", LogLevel.Debug);

            _gameServer.checkFarmhandRequest("", ConnectionDataToId(connectionData), netFarmerRoot,
                (outgoing) => SendMessageToConnection(connectionData.Connection, outgoing),
                () =>
                {
                    _monitor.Log($"Accepted {connectionData.SteamId.m_SteamID} as farmhand {farmerId}", LogLevel.Info);
                    SteamGameServerNetworkingSockets.SetConnectionUserData(connectionData.Connection, farmerId);
                    SteamGameServerNetworkingSockets.SetConnectionPollGroup(connectionData.Connection, _farmhandGroup);
                    _recentlyJoined.Add(connectionData.Connection);
                    connectionData.FarmerId = farmerId;
                    connectionData.Online = true;
                    _farmerConnectionMap[farmerId] = connectionData;
                });
        }

        public override void sendMessage(long peerId, OutgoingMessage message)
        {
            if (connected() && _farmerConnectionMap.TryGetValue(peerId, out var value) && value.Connection != HSteamNetConnection.Invalid)
            {
                SendMessageToConnection(value.Connection, message);
            }
        }

        private unsafe void SendMessageToConnection(HSteamNetConnection connection, OutgoingMessage message)
        {
            byte[] data;
            using (var stream = new MemoryStream())
            {
                using var writer = new BinaryWriter(stream);
                message.Write(writer);
                stream.Seek(0, SeekOrigin.Begin);
                data = stream.ToArray();
            }

            byte[] compressed = _netCompression.CompressAbove(data, SteamConstants.CompressionThreshold);

            if (compressed == null || compressed.Length == 0)
            {
                _monitor.Log("Compression failed for outgoing message", LogLevel.Error);
                return;
            }

            EResult result;
            fixed (byte* ptr = compressed)
            {
                result = SteamGameServerNetworkingSockets.SendMessageToConnection(
                    connection,
                    (IntPtr)ptr,
                    (uint)compressed.Length,
                    Steamworks.Constants.k_nSteamNetworkingSend_Reliable,
                    out _);
            }

            if (result != EResult.k_EResultOK)
            {
                _monitor.Log($"Failed to send message: {result}", LogLevel.Warn);
                CloseConnection(connection);
            }
            else
            {
                bandwidthLogger?.RecordBytesUp(compressed.Length);
            }
        }

        private void ProcessSteamMessage(IntPtr messagePtr, IncomingMessage message, out HSteamNetConnection messageConnection)
        {
            var steamMessage = (SteamNetworkingMessage_t)Marshal.PtrToStructure(messagePtr, typeof(SteamNetworkingMessage_t));
            messageConnection = steamMessage.m_conn;

            byte[] data = null;
            try
            {
                data = new byte[steamMessage.m_cbSize];
                Marshal.Copy(steamMessage.m_pData, data, 0, data.Length);

                using (var stream = new MemoryStream(_netCompression.DecompressBytes(data)))
                {
                    stream.Position = 0;
                    using var reader = new BinaryReader(stream);
                    message.Read(reader);
                }
            }
            finally
            {
                // Always release the message to prevent memory leaks
                SteamNetworkingMessage_t.Release(messagePtr);
                if (data != null)
                {
                    bandwidthLogger?.RecordBytesDown(data.Length);
                }
            }
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
        {
            var state = callback.m_info.m_eState;
            var steamId = callback.m_info.m_identityRemote.GetSteamID();

            switch (state)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    OnConnecting(callback, steamId);
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    OnConnected(callback, steamId);
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    OnDisconnected(callback, steamId);
                    break;
            }
        }

        private void OnConnecting(SteamNetConnectionStatusChangedCallback_t callback, CSteamID steamId)
        {
            _monitor.Log($"{steamId.m_SteamID} connecting via SDR", LogLevel.Debug);

            if (_gameServer.isUserBanned(steamId.m_SteamID.ToString()))
            {
                _monitor.Log($"{steamId.m_SteamID} is banned", LogLevel.Info);
                ShutdownConnection(callback.m_hConn);
                return;
            }

            // Note: SteamFriends.RequestUserInformation is Client API (not available in GameServer mode)
            // We'll get display names when players actually connect
            SteamGameServerNetworkingSockets.AcceptConnection(callback.m_hConn);
        }

        private void OnConnected(SteamNetConnectionStatusChangedCallback_t callback, CSteamID steamId)
        {
            _monitor.Log($"{steamId.m_SteamID} connected via SDR", LogLevel.Info);

            var connectionData = new ConnectionData(callback.m_hConn, steamId, "");
            _connectionDataMap[callback.m_hConn] = connectionData;
            _steamIdConnectionMap[steamId.m_SteamID] = connectionData;
            SteamGameServerNetworkingSockets.SetConnectionPollGroup(callback.m_hConn, _joiningGroup);

            string connectionId = ConnectionDataToId(connectionData);
            onConnect(connectionId);
            // Pass Steam ID as userId so farmhand ownership can be verified on reconnect
            string odId = steamId.m_SteamID.ToString();
            _gameServer.sendAvailableFarmhands(odId, connectionId, (outgoing) => SendMessageToConnection(callback.m_hConn, outgoing));
        }

        private void OnDisconnected(SteamNetConnectionStatusChangedCallback_t callback, CSteamID steamId)
        {
            if (!steamId.IsValid())
                return;

            _monitor.Log($"{steamId.m_SteamID} disconnected", LogLevel.Debug);

            // Clean up any abandoned cabin claim if player disconnected before completing character customization
            CabinManagerService.CleanupAbandonedCabinClaim(steamId.m_SteamID.ToString());

            if (!_connectionDataMap.TryGetValue(callback.m_hConn, out var value))
            {
                CloseConnection(callback.m_hConn);
                return;
            }

            onDisconnect(ConnectionDataToId(value));
            if (value.Online)
            {
                playerDisconnected(value.FarmerId);
            }

            _connectionDataMap.Remove(callback.m_hConn);
            _steamIdConnectionMap.Remove(value.SteamId.m_SteamID);
            CloseConnection(callback.m_hConn);
        }

        private string ConnectionDataToId(ConnectionData connection)
        {
            return $"SN_{connection.SteamId.m_SteamID}_{connection.Connection.m_HSteamNetConnection}";
        }

        private ConnectionData IdToConnectionData(string connectionId)
        {
            // Validate input format: must be "SN_{steamId}_{connectionHandle}"
            if (string.IsNullOrEmpty(connectionId) || connectionId.Length <= 3 || !connectionId.StartsWith("SN_"))
                return null;

            string text = connectionId.Substring(3);
            int separatorIndex = text.IndexOf('_');
            if (separatorIndex <= 0 || separatorIndex >= text.Length - 1)
                return null;

            // Use TryParse for safe parsing (prevents exceptions from malformed input)
            string steamIdPart = text.Substring(0, separatorIndex);
            string connHandlePart = text.Substring(separatorIndex + 1);

            if (!ulong.TryParse(steamIdPart, out ulong steamId))
            {
                _monitor.Log($"[IdToConnectionData] Invalid Steam ID format: {steamIdPart}", LogLevel.Debug);
                return null;
            }

            if (!uint.TryParse(connHandlePart, out uint connHandle))
            {
                _monitor.Log($"[IdToConnectionData] Invalid connection handle format: {connHandlePart}", LogLevel.Debug);
                return null;
            }

            if (!new CSteamID(steamId).IsValid())
                return null;

            HSteamNetConnection conn = new HSteamNetConnection();
            conn.m_HSteamNetConnection = connHandle;

            if (!_connectionDataMap.TryGetValue(conn, out var value))
                return null;

            if (value.SteamId.m_SteamID != steamId)
                return null;

            return value;
        }

        public override bool isConnectionActive(string connectionId)
        {
            return IdToConnectionData(connectionId) != null;
        }

        public override string getUserId(long farmerId)
        {
            if (!_farmerConnectionMap.TryGetValue(farmerId, out var value))
                return null;
            return value.SteamId.m_SteamID.ToString();
        }

        public override bool hasUserId(string userId)
        {
            if (string.IsNullOrEmpty(userId))
                return false;

            // Use TryParse for safe parsing
            if (!ulong.TryParse(userId, out ulong steamId))
            {
                _monitor.Log($"[hasUserId] Invalid Steam ID format: {userId}", LogLevel.Debug);
                return false;
            }

            return _steamIdConnectionMap.ContainsKey(steamId);
        }

        public override string getUserName(long farmerId)
        {
            if (!_farmerConnectionMap.TryGetValue(farmerId, out var value))
                return "";

            try
            {
                SteamGameServerNetworkingSockets.GetConnectionInfo(value.Connection, out var info);
                string name = info.m_szConnectionDescription;
                if (!string.IsNullOrWhiteSpace(name) && name != "[unknown]")
                {
                    value.DisplayName = name;
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"[getUserName] Error getting connection info: {ex.Message}", LogLevel.Debug);
            }

            return value.DisplayName ?? "";
        }

        public override void setPrivacy(ServerPrivacy privacy)
        {
            // Forward to AuthService to set privacy on the SteamKit2 lobby
            Services.Auth.GalaxyAuthService.SetSteamLobbyPrivacy(privacy);
        }

        public override void setLobbyData(string key, string value)
        {
            // Forward to AuthService to set data on the SteamKit2 lobby
            Services.Auth.GalaxyAuthService.SetSteamLobbyData(key, value);
        }

        public override void kick(long disconnectee)
        {
            base.kick(disconnectee);
            sendMessage(disconnectee, new OutgoingMessage(MessageType_ForceKick, Game1.player.UniqueMultiplayerID));
            if (_farmerConnectionMap.TryGetValue(disconnectee, out var value))
            {
                ShutdownConnection(value.Connection);
            }
        }

        public override void playerDisconnected(long disconnectee)
        {
            if (_farmerConnectionMap.TryGetValue(disconnectee, out _))
            {
                base.playerDisconnected(disconnectee);
                _farmerConnectionMap.Remove(disconnectee);
            }
        }

        public override float getPingToClient(long farmerId)
        {
            if (!_farmerConnectionMap.TryGetValue(farmerId, out var value))
                return -1f;

            SteamGameServerNetworkingSockets.GetQuickConnectionStatus(value.Connection, out var stats);
            return stats.m_nPing;
        }

        public override bool canOfferInvite()
        {
            // Steam overlay not available in headless mode
            return false;
        }

        public override void offerInvite()
        {
            // No-op: Steam overlay not available in headless mode
        }

        private void ShutdownConnection(HSteamNetConnection connection)
        {
            CloseConnection(connection, () =>
            {
                if (_connectionDataMap.TryGetValue(connection, out var data))
                {
                    onDisconnect(ConnectionDataToId(data));
                    if (data.Online)
                    {
                        playerDisconnected(data.FarmerId);
                    }
                    _connectionDataMap.Remove(connection);
                    _steamIdConnectionMap.Remove(data.SteamId.m_SteamID);
                }
            });
        }

        private void CloseConnection(HSteamNetConnection connection, Action beforeClose = null)
        {
            if (connection == HSteamNetConnection.Invalid)
                return;

            SteamGameServerNetworkingSockets.SetConnectionPollGroup(connection, HSteamNetPollGroup.Invalid);
            beforeClose?.Invoke();
            SteamGameServerNetworkingSockets.CloseConnection(connection, 1000, null, true);
        }

        private static SteamNetworkingConfigValue_t[] GetNetworkingOptions()
        {
            return new SteamNetworkingConfigValue_t[]
            {
                new SteamNetworkingConfigValue_t
                {
                    m_eValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                    m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    m_val = new SteamNetworkingConfigValue_t.OptionValue { m_int32 = SteamConstants.SendBufferSize } // 1MB
                }
            };
        }

        /// <summary>
        /// Internal connection data tracking.
        /// </summary>
        private class ConnectionData
        {
            public HSteamNetConnection Connection { get; }
            public CSteamID SteamId { get; }
            public string DisplayName { get; set; }
            public long FarmerId { get; set; }
            public bool Online { get; set; }

            public ConnectionData(HSteamNetConnection connection, CSteamID steamId, string displayName)
            {
                Connection = connection;
                SteamId = steamId;
                DisplayName = displayName;
                FarmerId = 0;
                Online = false;
            }
        }
    }
}
