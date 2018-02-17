﻿using Barotrauma.Items.Components;
using Lidgren.Network;
using Microsoft.Xna.Framework;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Barotrauma.Networking
{
    partial class GameServer : NetworkMember
    {
        private List<Client> connectedClients = new List<Client>();

        //for keeping track of disconnected clients in case the reconnect shortly after
        private List<Client> disconnectedClients = new List<Client>();

        private int roundStartSeed;
        
        //is the server running
        private bool started;

        public NetServer server;
        private NetPeerConfiguration config;
       
        private DateTime refreshMasterTimer;

        private DateTime roundStartTime;

        private RestClient restClient;
        private bool masterServerResponded;
        private IRestResponse masterServerResponse;

        private ServerLog log;

        private bool initiatedStartGame;
        private CoroutineHandle startGameCoroutine;

        public TraitorManager TraitorManager;

        private ServerEntityEventManager entityEventManager;

        private FileSender fileSender;

        public override List<Client> ConnectedClients
        {
            get
            {
                return connectedClients;
            }
        }

        
        public ServerEntityEventManager EntityEventManager
        {
            get { return entityEventManager; }
        }

        public ServerLog ServerLog
        {
            get { return log; }
        }

        public TimeSpan UpdateInterval
        {
            get { return updateInterval; }
        }
        
        public GameServer(string name, int port, bool isPublic = false, string password = "", bool attemptUPnP = false, int maxPlayers = 10)
        {
            name = name.Replace(":", "");
            name = name.Replace(";", "");

            //AdminAuthPass = "";

            //Nilmod AdminAuthPass
            AdminAuthPass = GameMain.NilMod.AdminAuth;

            this.name = name;
            this.isPublic = isPublic;
            this.maxPlayers = maxPlayers;
            this.password = "";
            if (password.Length>0)
            {
                this.password = Encoding.UTF8.GetString(NetUtility.ComputeSHAHash(Encoding.UTF8.GetBytes(password)));
            }

            config = new NetPeerConfiguration("barotrauma");

#if CLIENT
            netStats = new NetStats();
#endif

            /*
            #if DEBUG
            
                config.SimulatedLoss = 0.05f;
                config.SimulatedRandomLatency = 0.05f;
                config.SimulatedDuplicatesChance = 0.05f;
                config.SimulatedMinimumLatency = 0.1f;

                config.ConnectionTimeout = 60.0f;

            #endif 
            */

            //NilMod DebugLagActive
            if (GameMain.NilMod.DebugLag)
            {
                config.SimulatedLoss = GameMain.NilMod.DebugLagSimulatedPacketLoss;
                config.SimulatedRandomLatency = GameMain.NilMod.DebugLagSimulatedRandomLatency;
                config.SimulatedDuplicatesChance = GameMain.NilMod.DebugLagSimulatedDuplicatesChance;
                config.SimulatedMinimumLatency = GameMain.NilMod.DebugLagSimulatedMinimumLatency;

                config.ConnectionTimeout = GameMain.NilMod.DebugLagConnectionTimeout;
            }

            //NetIdUtils.Test();

            config.Port = port;
            Port = port;

            if (attemptUPnP)
            {
                config.EnableUPnP = true;
            }

            config.MaximumConnections = maxPlayers * 2; //double the lidgren connections for unauthenticated players            

            config.DisableMessageType(NetIncomingMessageType.DebugMessage |
                NetIncomingMessageType.WarningMessage | NetIncomingMessageType.Receipt |
                NetIncomingMessageType.ErrorMessage |
                NetIncomingMessageType.UnconnectedData);

            config.EnableMessageType(NetIncomingMessageType.Error);

            config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);

            log = new ServerLog(name);

            InitProjSpecific();

            entityEventManager = new ServerEntityEventManager(this);

            whitelist = new WhiteList();
            banList = new BanList();

            LoadSettings();
            LoadClientPermissions();
            
            CoroutineManager.StartCoroutine(StartServer(isPublic));
        }

        private IEnumerable<object> StartServer(bool isPublic)
        {
            bool error = false;
            try
            {
                Log("Starting the server...", ServerLog.MessageType.ServerMessage);
                server = new NetServer(config);
                netPeer = server;

                fileSender = new FileSender(this);
                fileSender.OnEnded += FileTransferChanged;
                fileSender.OnStarted += FileTransferChanged;
                
                server.Start();
            }
            catch (Exception e)
            {
                Log("Error while starting the server (" + e.Message + ")", ServerLog.MessageType.Error);

                System.Net.Sockets.SocketException socketException = e as System.Net.Sockets.SocketException;

#if CLIENT
                if (socketException != null && socketException.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
                {
                    new GUIMessageBox("Starting the server failed", e.Message + ". Are you trying to run multiple servers on the same port?");
                }
                else
                {
                    new GUIMessageBox("Starting the server failed", e.Message);
                }
#endif

                error = true;
            }                  
      
            if (error)
            {
                if (server != null) server.Shutdown("Error while starting the server");

#if CLIENT
                GameMain.NetworkMember = null;
#elif SERVER
                Environment.Exit(-1);
#endif
                yield return CoroutineStatus.Success;
            }
            
            if (config.EnableUPnP)
            {
                InitUPnP();

                //DateTime upnpTimeout = DateTime.Now + new TimeSpan(0,0,5);
                while (DiscoveringUPnP())// && upnpTimeout>DateTime.Now)
                {
                    yield return null;
                }

                FinishUPnP();
            }

            if (isPublic)
            {
                CoroutineManager.StartCoroutine(RegisterToMasterServer());
            }
                        
            updateInterval = new TimeSpan(0, 0, 0, 0, 150);

            Log("Server started", ServerLog.MessageType.ServerMessage);
                        
            GameMain.NetLobbyScreen.Select();

#if CLIENT
            GameMain.NetLobbyScreen.DefaultServerStartupSubSelect();
            GameSession.inGameInfo.Initialize();
#endif

            GameMain.NilMod.GameInitialize(true);

            started = true;
            yield return CoroutineStatus.Success;
        }

        private IEnumerable<object> RegisterToMasterServer()
        {
            if (restClient==null)
            {
                restClient = new RestClient(NetConfig.MasterServerUrl);            
            }

            GameMain.NilMod.Admins = Math.Min(ConnectedClients.FindAll(c => c.HasPermission(ClientPermissions.Ban)).Count, GameMain.NilMod.MaxAdminSlots);
            GameMain.NilMod.Moderators = Math.Min(ConnectedClients.FindAll(c => c.HasPermission(ClientPermissions.Kick) && !c.HasPermission(ClientPermissions.Ban)).Count, GameMain.NilMod.MaxModeratorSlots);
            GameMain.NilMod.Spectators = Math.Min(ConnectedClients.FindAll(c => c.SpectateOnly).Count, GameMain.NilMod.MaxSpectatorSlots);

            //Code so they don't reduce current player counts multiple times when counted for multiple  slots
            int ModAdminSpectators = Math.Min((Math.Min(ConnectedClients.FindAll(c => c.HasPermission(ClientPermissions.Ban) && c.SpectateOnly).Count, GameMain.NilMod.MaxAdminSlots)
                + Math.Min(ConnectedClients.FindAll(c => c.HasPermission(ClientPermissions.Kick) && !c.HasPermission(ClientPermissions.Ban) && c.SpectateOnly).Count, GameMain.NilMod.MaxModeratorSlots)), GameMain.NilMod.MaxSpectatorSlots);

            int CurrentPlayers = ConnectedClients.Count - (GameMain.NilMod.Admins + GameMain.NilMod.Moderators + GameMain.NilMod.Spectators - ModAdminSpectators);

            var request = new RestRequest("masterserver3.php", Method.GET);            
            request.AddParameter("action", "addserver");
            request.AddParameter("servername", name);
            request.AddParameter("serverport", Port);
            request.AddParameter("currplayers", CurrentPlayers);
            request.AddParameter("maxplayers", maxPlayers);
            request.AddParameter("password", string.IsNullOrWhiteSpace(password) ? 0 : 1);
            request.AddParameter("version", GameMain.Version.ToString());
            if (GameMain.Config.SelectedContentPackage != null)
            {
                request.AddParameter("contentpackage", GameMain.Config.SelectedContentPackage.Name);
            }

            masterServerResponded = false;
            masterServerResponse = null;
            var restRequestHandle = restClient.ExecuteAsync(request, response => MasterServerCallBack(response));
            
            DateTime timeOut = DateTime.Now + new TimeSpan(0, 0, 15);
            while (!masterServerResponded)
            {
                if (DateTime.Now > timeOut)
                {
                    restRequestHandle.Abort();
                    DebugConsole.NewMessage("Couldn't register to master server (request timed out)", Color.Red);
                    Log("Couldn't register to master server (request timed out)", ServerLog.MessageType.Error);
                    yield return CoroutineStatus.Success;
                }

                yield return CoroutineStatus.Running;
            }

            if (masterServerResponse.StatusCode != System.Net.HttpStatusCode.OK)
            {
                DebugConsole.ThrowError("Error while connecting to master server (" + masterServerResponse.StatusCode + ": " + masterServerResponse.StatusDescription + ")");
            }
            else if (masterServerResponse != null && !string.IsNullOrWhiteSpace(masterServerResponse.Content))
            {
                DebugConsole.ThrowError("Error while connecting to master server (" + masterServerResponse.Content + ")");
            }
            else
            {
                registeredToMaster = true;
                refreshMasterTimer = DateTime.Now + refreshMasterInterval;
            }

            yield return CoroutineStatus.Success;
        }

        private IEnumerable<object> RefreshMaster()
        {
            if (restClient == null)
            {
                restClient = new RestClient(NetConfig.MasterServerUrl);
            }

            GameMain.NilMod.Admins = Math.Min(ConnectedClients.FindAll(c => c.HasPermission(ClientPermissions.Ban)).Count, GameMain.NilMod.MaxAdminSlots);
            GameMain.NilMod.Moderators = Math.Min(ConnectedClients.FindAll(c => c.HasPermission(ClientPermissions.Kick) && !c.HasPermission(ClientPermissions.Ban)).Count, GameMain.NilMod.MaxModeratorSlots);
            GameMain.NilMod.Spectators = Math.Min(ConnectedClients.FindAll(c => c.SpectateOnly).Count, GameMain.NilMod.MaxSpectatorSlots);

            //Code so they don't reduce current player counts multiple times when counted for multiple  slots
            int ModAdminSpectators = Math.Min((Math.Min(ConnectedClients.FindAll(c => c.HasPermission(ClientPermissions.Ban) && c.SpectateOnly).Count, GameMain.NilMod.MaxAdminSlots)
                + Math.Min(ConnectedClients.FindAll(c => c.HasPermission(ClientPermissions.Kick) && !c.HasPermission(ClientPermissions.Ban) && c.SpectateOnly).Count, GameMain.NilMod.MaxModeratorSlots)), GameMain.NilMod.MaxSpectatorSlots);

            int CurrentPlayers = ConnectedClients.Count - (GameMain.NilMod.Admins + GameMain.NilMod.Moderators + GameMain.NilMod.Spectators - ModAdminSpectators);
            
            var request = new RestRequest("masterserver3.php", Method.GET);
            request.AddParameter("action", "refreshserver");
            request.AddParameter("serverport", Port);
            request.AddParameter("gamestarted", gameStarted ? 1 : 0);
            request.AddParameter("currplayers", CurrentPlayers);
            request.AddParameter("maxplayers", maxPlayers);

            if (GameMain.NilMod.ShowMasterServerSuccess)
            {
                Log("Refreshing connection with master server...", ServerLog.MessageType.ServerMessage);
            }

            var sw = new Stopwatch();
            sw.Start();

            masterServerResponded = false;
            masterServerResponse = null;
            var restRequestHandle = restClient.ExecuteAsync(request, response => MasterServerCallBack(response));

            DateTime timeOut = DateTime.Now + new TimeSpan(0, 0, 15);
            while (!masterServerResponded)
            {
                if (DateTime.Now > timeOut)
                {
                    restRequestHandle.Abort();
                    DebugConsole.NewMessage("Couldn't connect to master server (request timed out)", Color.Red);
                    Log("Couldn't connect to master server (request timed out)", ServerLog.MessageType.Error);
                    yield return CoroutineStatus.Success;
                }
                
                yield return CoroutineStatus.Running;
            }

            if (masterServerResponse.Content == "Error: server not found")
            {
                Log("Not registered to master server, re-registering...", ServerLog.MessageType.Error);
                CoroutineManager.StartCoroutine(RegisterToMasterServer());
            }
            else if (masterServerResponse.ErrorException != null)
            {
                DebugConsole.NewMessage("Error while registering to master server (" + masterServerResponse.ErrorException + ")", Color.Red);
                Log("Error while registering to master server (" + masterServerResponse.ErrorException + ")", ServerLog.MessageType.Error);
            }
            else if (masterServerResponse.StatusCode != System.Net.HttpStatusCode.OK)
            {
                DebugConsole.NewMessage("Error while reporting to master server (" + masterServerResponse.StatusCode + ": " + masterServerResponse.StatusDescription + ")", Color.Red);
                Log("Error while reporting to master server (" + masterServerResponse.StatusCode + ": " + masterServerResponse.StatusDescription + ")", ServerLog.MessageType.Error);
            }
            else
            {
                if (GameMain.NilMod.ShowMasterServerSuccess)
                {
                    Log("Master server responded", ServerLog.MessageType.ServerMessage);
                }
            }

            System.Diagnostics.Debug.WriteLine("took "+sw.ElapsedMilliseconds+" ms");

            yield return CoroutineStatus.Success;
        }

        private void MasterServerCallBack(IRestResponse response)
        {
            masterServerResponse = response;
            masterServerResponded = true;
        }
        
        public override void Update(float deltaTime)
        {
#if CLIENT
            if (ShowNetStats) netStats.Update(deltaTime);
            if (settingsFrame != null) settingsFrame.Update(deltaTime);
            if (log.LogFrame != null) log.LogFrame.Update(deltaTime);
#endif
            
            if (!started) return;
            
            base.Update(deltaTime);

            foreach (UnauthenticatedClient unauthClient in unauthenticatedClients)
            {
                unauthClient.AuthTimer -= deltaTime;
                if (unauthClient.AuthTimer <= 0.0f)
                {
                    unauthClient.Connection.Disconnect("Connection timed out");
                }
            }

            unauthenticatedClients.RemoveAll(uc => uc.AuthTimer <= 0.0f);

            fileSender.Update(deltaTime);         
            
            if (gameStarted)
            {
                if (respawnManager != null) respawnManager.Update(deltaTime);

                entityEventManager.Update(connectedClients);
#if CLIENT
                if (GameMain.NilMod.ActiveClickCommand)
                {
                    ClickCommandUpdate(deltaTime);
                }
#endif

                bool isCrewDead =
                    connectedClients.All(c => c.Character == null || c.Character.IsDead || c.Character.IsUnconscious) &&
                    (myCharacter == null || myCharacter.IsDead || myCharacter.IsUnconscious);

                //restart if all characters are dead or submarine is at the end of the level
                if ((autoRestart && isCrewDead)
                    ||
                    (EndRoundAtLevelEnd && Submarine.MainSub != null && Submarine.MainSub.AtEndPosition && Submarine.MainSubs[1] == null))
                {
                    if (AutoRestart && isCrewDead)
                    {
                        Log("Ending round (entire crew dead)", ServerLog.MessageType.ServerMessage);
                    }
                    else
                    {
                        Log("Ending round (submarine reached the end of the level)", ServerLog.MessageType.ServerMessage);
                    }

                    EndGame();
                    return;
                }
            }
            else if (initiatedStartGame)
            {
                //tried to start up the game and StartGame coroutine is not running anymore
                // -> something wen't wrong during startup, re-enable start button and reset AutoRestartTimer
                if (startGameCoroutine != null && !CoroutineManager.IsCoroutineRunning(startGameCoroutine))
                {
                    if (autoRestart) AutoRestartTimer = Math.Max(AutoRestartInterval, 5.0f);
                    GameMain.NetLobbyScreen.StartButtonEnabled = true;

                    GameMain.NetLobbyScreen.LastUpdateID++;

                    startGameCoroutine = null;
                    initiatedStartGame = false;
                }
            }
            else if (autoRestart && Screen.Selected == GameMain.NetLobbyScreen && connectedClients.Count > 0)
            {
                AutoRestartTimer -= deltaTime;
                if (AutoRestartTimer < 0.0f && !initiatedStartGame)
                {
                    StartGame();
                }
            }

            for (int i = disconnectedClients.Count - 1; i >= 0; i-- )
            {
                disconnectedClients[i].DeleteDisconnectedTimer -= deltaTime;
                if (disconnectedClients[i].DeleteDisconnectedTimer > 0.0f) continue;

                if (gameStarted && disconnectedClients[i].Character!=null)
                {
                    if(!GameMain.NilMod.AllowReconnect)
                    {
                        disconnectedClients[i].Character.Kill(CauseOfDeath.Damage, true);
                    }
                    else
                    {
                        disconnectedClients[i].Character.ClearInputs();
                    }
                    disconnectedClients[i].Character = null;
                }

                disconnectedClients.RemoveAt(i);
            }

            foreach (Client c in connectedClients)
            {
                //slowly reset spam timers
                c.ChatSpamTimer = Math.Max(0.0f, c.ChatSpamTimer - deltaTime);
                c.ChatSpamSpeed = Math.Max(0.0f, c.ChatSpamSpeed - deltaTime);
            }

            NetIncomingMessage inc = null; 
            while ((inc = server.ReadMessage()) != null)
            {
                try
                {
                    switch (inc.MessageType)
                    {
                        case NetIncomingMessageType.Data:
                            ReadDataMessage(inc);
                            break;
                        case NetIncomingMessageType.StatusChanged:
                            switch (inc.SenderConnection.Status)
                            {
                                case NetConnectionStatus.Disconnected:
                                    var connectedClient = connectedClients.Find(c => c.Connection == inc.SenderConnection);
                                    /*if (connectedClient != null && !disconnectedClients.Contains(connectedClient))
                                    {
                                        connectedClient.deleteDisconnectedTimer = NetConfig.DeleteDisconnectedTime;
                                        disconnectedClients.Add(connectedClient);
                                    }
                                    */
                                    DisconnectClient(inc.SenderConnection,
                                        connectedClient != null ? connectedClient.Name + " has disconnected" : "");
                                    break;
                            }
                            break;
                        case NetIncomingMessageType.ConnectionApproval:
                            /*
                            if (banList.IsBanned(inc.SenderEndPoint.Address.ToString()))
                            {
                                DebugConsole.NewMessage("Banned Player tried to join the server (" + inc.SenderEndPoint.Address.ToString() + ")", Color.Red);
                                inc.SenderConnection.Deny("You have been banned from the server");
                            }
                            else */

                            GameMain.NilMod.Admins = Math.Min(ConnectedClients.FindAll(c => c.HasPermission(ClientPermissions.Ban)).Count, GameMain.NilMod.MaxAdminSlots);
                            GameMain.NilMod.Moderators = Math.Min(ConnectedClients.FindAll(c => c.HasPermission(ClientPermissions.Kick) && !c.HasPermission(ClientPermissions.Ban)).Count, GameMain.NilMod.MaxModeratorSlots);
                            GameMain.NilMod.Spectators = Math.Min(ConnectedClients.FindAll(c => c.SpectateOnly).Count, GameMain.NilMod.MaxSpectatorSlots);

                            //Code so they don't reduce current player counts multiple times when counted for multiple  slots
                            int ModAdminSpectators = Math.Min((Math.Min(ConnectedClients.FindAll(c => c.HasPermission(ClientPermissions.Ban) && c.SpectateOnly).Count, GameMain.NilMod.MaxAdminSlots)
                                + Math.Min(ConnectedClients.FindAll(c => c.HasPermission(ClientPermissions.Kick) && !c.HasPermission(ClientPermissions.Ban) && c.SpectateOnly).Count, GameMain.NilMod.MaxModeratorSlots)), GameMain.NilMod.MaxSpectatorSlots);

                            int CurrentPlayers = ConnectedClients.Count - (GameMain.NilMod.Admins + GameMain.NilMod.Moderators + GameMain.NilMod.Spectators - ModAdminSpectators);

                            var precheckPermissions = clientPermissions.Find(cp => cp.IP == inc.SenderConnection.RemoteEndPoint.Address.ToString());

                            if ((CurrentPlayers + unauthenticatedClients.Count) >= maxPlayers)
                            {
                                if (precheckPermissions.Permissions.HasFlag(ClientPermissions.Ban) || precheckPermissions.Permissions.HasFlag(ClientPermissions.Kick))
                                {
                                    if ((ClientPacketHeader)inc.SenderConnection.RemoteHailMessage.ReadByte() == ClientPacketHeader.REQUEST_AUTH)
                                    {
                                        inc.SenderConnection.Approve();
                                        ClientAuthRequest(inc.SenderConnection);
                                    }
                                }
                                else
                                {
                                    //server is full, can't allow new connection
                                    inc.SenderConnection.Deny("Server full");
                                    return;
                                }
                            }
                            else
                            {
                                if ((ClientPacketHeader)inc.SenderConnection.RemoteHailMessage.ReadByte() == ClientPacketHeader.REQUEST_AUTH)
                                {
                                    inc.SenderConnection.Approve();
                                    ClientAuthRequest(inc.SenderConnection);
                                }
                            }
                            break;
                    }                            
                }

                catch (Exception e)
                {
                    if (GameSettings.VerboseLogging)
                    {
                        DebugConsole.ThrowError("Failed to read an incoming message. {" + e + "}\n" + e.StackTrace);
                    }
                }
            }
            
            // if 30ms has passed
            if (updateTimer < DateTime.Now)
            {
                for (int i = Character.CharacterList.Count - 1; i >= 0; i--)
                {
                    //Don't check status updates of temp removed characters
                    if (GameMain.NilMod.convertinghusklist.Find(ch => ch.character == Character.CharacterList[i]) != null) continue;
                    Character.CharacterList[i].CheckForStatusEvent();
                }


                if (server.ConnectionsCount > 0)
                {
                    foreach (Client c in ConnectedClients)
                    {
                        try
                        {
                            ClientWrite(c);
                        }
                        catch (Exception e)
                        {
                            DebugConsole.ThrowError("Failed to write a network message for the client \""+c.Name+"\"!", e);
                        }
                    }


                    //Reset the send timer
                    if(GameMain.NilMod.SyncResendTimer <= 0f)
                    {
                        GameMain.NilMod.SyncResendTimer = NilMod.SyncResendInterval;
                    }

                    foreach (Item item in Item.ItemList)
                    {
                        item.NeedsPositionUpdate = false;
                    }
                }

                updateTimer = DateTime.Now + updateInterval;
            }

            if (!registeredToMaster || refreshMasterTimer >= DateTime.Now) return;

            CoroutineManager.StartCoroutine(RefreshMaster());
            refreshMasterTimer = DateTime.Now + refreshMasterInterval;
        }

        private void ReadDataMessage(NetIncomingMessage inc)
        {
            ClientPacketHeader header = (ClientPacketHeader)inc.ReadByte();
            switch (header)
            {
                case ClientPacketHeader.REQUEST_AUTH:
                    ClientAuthRequest(inc.SenderConnection);
                    break;
                case ClientPacketHeader.REQUEST_INIT:

                    ClientInitRequest(inc);
                    break;

                case ClientPacketHeader.RESPONSE_STARTGAME:
                    if (banList.IsBanned(inc.SenderEndPoint.Address.ToString()))
                    {
                        if (BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()) != null)
                        {
                            KickBannedClient(inc.SenderConnection, "\nReason: "
                                + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));

                            //DisconnectClient(inc.SenderConnection,"", "You have been banned from the server." + "\nReason: "
                            //    + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));

                            //KickClient(inc.SenderConnection, "You have been banned from the server." + "\nReason: " 
                            //    + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));
                        }
                        else
                        {
                            KickBannedClient(inc.SenderConnection, "");
                            //KickClient(inc.SenderConnection, "You have been banned from the server.");
                        }

                        //KickClient(inc.SenderConnection, "You have been banned from the server.");
                        return;
                    }
                    var connectedClient = connectedClients.Find(c => c.Connection == inc.SenderConnection);
                    if (connectedClient != null)
                    {
                        connectedClient.ReadyToStart = inc.ReadBoolean();
                        UpdateCharacterInfo(inc, connectedClient);

                        //game already started -> send start message immediately
                        if (gameStarted)
                        {
                            SendStartMessage(roundStartSeed, Submarine.MainSub, GameMain.GameSession.GameMode.Preset, connectedClient);
                        }
                    }
                    break;
                case ClientPacketHeader.UPDATE_LOBBY:
                    if (banList.IsBanned(inc.SenderEndPoint.Address.ToString()))
                    {
                        if (BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()) != null)
                        {
                            KickBannedClient(inc.SenderConnection, "\nReason: "
                                + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));

                            //DisconnectClient(inc.SenderConnection,"", "You have been banned from the server." + "\nReason: "
                            //    + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));

                            //KickClient(inc.SenderConnection, "You have been banned from the server." + "\nReason: " 
                            //    + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));
                        }
                        else
                        {
                            KickBannedClient(inc.SenderConnection, "");
                            //KickClient(inc.SenderConnection, "You have been banned from the server.");
                        }

                        //KickClient(inc.SenderConnection, "You have been banned from the server.");
                        return;
                    }
                    ClientReadLobby(inc);
                    break;
                case ClientPacketHeader.UPDATE_INGAME:
                    if (!gameStarted) return;
                    if (banList.IsBanned(inc.SenderEndPoint.Address.ToString()))
                    {
                        if(BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()) != null)
                        {
                            KickBannedClient(inc.SenderConnection, "\nReason: "
                                + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));

                            //DisconnectClient(inc.SenderConnection,"", "You have been banned from the server." + "\nReason: "
                            //    + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));

                            //KickClient(inc.SenderConnection, "You have been banned from the server." + "\nReason: " 
                            //    + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));
                        }
                        else
                        {
                            KickBannedClient(inc.SenderConnection, "");
                            //KickClient(inc.SenderConnection, "You have been banned from the server.");
                        }
                        
                        return;
                    }
                    ClientReadIngame(inc);
                    break;
                case ClientPacketHeader.SERVER_COMMAND:
                    if (banList.IsBanned(inc.SenderEndPoint.Address.ToString()))
                    {
                        if (BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()) != null)
                        {
                            KickBannedClient(inc.SenderConnection, "\nReason: "
                                + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));

                            //DisconnectClient(inc.SenderConnection,"", "You have been banned from the server." + "\nReason: "
                            //    + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));

                            //KickClient(inc.SenderConnection, "You have been banned from the server." + "\nReason: " 
                            //    + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));
                        }
                        else
                        {
                            KickBannedClient(inc.SenderConnection, "");
                            //KickClient(inc.SenderConnection, "You have been banned from the server.");
                        }

                        //KickClient(inc.SenderConnection, "You have been banned from the server.");
                        return;
                    }
                    ClientReadServerCommand(inc);
                    break;
                case ClientPacketHeader.FILE_REQUEST:
                    if (AllowFileTransfers)
                    {
                        if (banList.IsBanned(inc.SenderEndPoint.Address.ToString()))
                        {
                            if (BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()) != null)
                            {
                                KickBannedClient(inc.SenderConnection, "\nReason: "
                                + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));

                                //DisconnectClient(inc.SenderConnection,"", "You have been banned from the server." + "\nReason: "
                                //    + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));

                                //KickClient(inc.SenderConnection, "You have been banned from the server." + "\nReason: " 
                                //    + BanList.GetBanReason(inc.SenderEndPoint.Address.ToString()));
                            }
                            else
                            {
                                KickBannedClient(inc.SenderConnection, "");
                                //KickClient(inc.SenderConnection, "You have been banned from the server.");
                            }

                            //KickClient(inc.SenderConnection, "You have been banned from the server.");
                            return;
                        }
                        fileSender.ReadFileRequest(inc);
                    }
                    break;
                case ClientPacketHeader.NILMODSYNCRECEIVED:
                    var syncreceivedclient = connectedClients.Find(c => c.Connection == inc.SenderConnection);
                    Byte NilModSyncState = inc.ReadByte();

                    //Version is Correct
                    if(NilModSyncState == 0)
                    {
                        if (syncreceivedclient != null)
                        {
                            syncreceivedclient.RequiresNilModSync = false;
                        }
                    }
                    //Version is Earlier
                    else if(NilModSyncState == 1)
                    {
                        if (syncreceivedclient != null)
                        {
                            syncreceivedclient.IsNilModClient = false;
                            syncreceivedclient.RequiresNilModSync = false;
                        }
                    }
                    //Version is later
                    else if(NilModSyncState == 2)
                    {
                        syncreceivedclient.IsNilModClient = false;
                        syncreceivedclient.RequiresNilModSync = false;
                    }
                    
                    inc.ReadPadBits();
                    break;
            }            
        }
        
        public void CreateEntityEvent(IServerSerializable entity, object[] extraData = null)
        {
            entityEventManager.CreateEvent(entity, extraData);
        }

        private byte GetNewClientID()
        {
            byte userID = 1;
            while (connectedClients.Any(c => c.ID == userID))
            {
                userID++;
            }
            return userID;
        }

        private void ClientReadLobby(NetIncomingMessage inc)
        {
            Client c = ConnectedClients.Find(x => x.Connection == inc.SenderConnection);
            if (c == null)
            {
                inc.SenderConnection.Disconnect("You're not a connected client.");
                return;
            }
            
            ClientNetObject objHeader;
            while ((objHeader = (ClientNetObject)inc.ReadByte()) != ClientNetObject.END_OF_MESSAGE)
            {
                switch (objHeader)
                {
                    case ClientNetObject.SYNC_IDS:
                        //TODO: might want to use a clever class for this
                        c.LastRecvGeneralUpdate = NetIdUtils.Clamp(inc.ReadUInt16(), c.LastRecvGeneralUpdate, GameMain.NetLobbyScreen.LastUpdateID);
                        c.LastRecvChatMsgID     = NetIdUtils.Clamp(inc.ReadUInt16(), c.LastRecvChatMsgID, c.LastChatMsgQueueID);

                        c.LastRecvCampaignSave      = inc.ReadUInt16();
                        if (c.LastRecvCampaignSave > 0)
                        {
                            c.LastRecvCampaignUpdate    = inc.ReadUInt16();
                        }
                        break;
                    case ClientNetObject.CHAT_MESSAGE:
                        ChatMessage.ServerRead(inc, c);
                        break;
                    case ClientNetObject.VOTE:
                        Voting.ServerRead(inc, c);
                        break;
                    default:
                        return;
                }

                //don't read further messages if the client has been disconnected (kicked due to spam for example)
                if (!connectedClients.Contains(c)) break;
            }
        }

        private void ClientReadIngame(NetIncomingMessage inc)
        {
            Client c = ConnectedClients.Find(x => x.Connection == inc.SenderConnection);
            if (c == null)
            {
                inc.SenderConnection.Disconnect("You're not a connected client.");
                return;
            }

            if (gameStarted)
            {
                if (!c.InGame)
                {
                    //check if midround syncing is needed due to missed unique events
                    entityEventManager.InitClientMidRoundSync(c);                    
                    c.InGame = true;
                }
            }
            
            ClientNetObject objHeader;
            while ((objHeader = (ClientNetObject)inc.ReadByte()) != ClientNetObject.END_OF_MESSAGE)
            {
                switch (objHeader)
                {
                    case ClientNetObject.SYNC_IDS:
                        //TODO: might want to use a clever class for this
                        
                        UInt16 lastRecvChatMsgID        = inc.ReadUInt16();
                        UInt16 lastRecvEntityEventID    = inc.ReadUInt16();

                        //last msgs we've created/sent, the client IDs should never be higher than these
                        UInt16 lastEntityEventID = entityEventManager.Events.Count == 0 ? (UInt16)0 : entityEventManager.Events.Last().ID;

                        if (c.NeedsMidRoundSync)
                        {
                            //received all the old events -> client in sync, we can switch to normal behavior
                            if (lastRecvEntityEventID >= c.UnreceivedEntityEventCount - 1 ||
                                c.UnreceivedEntityEventCount == 0)
                            {
                                c.NeedsMidRoundSync = false;
                                lastRecvEntityEventID = (UInt16)(c.FirstNewEventID - 1);
                                c.LastRecvEntityEventID = lastRecvEntityEventID;

                                DisconnectedCharacter disconnectedcharcheck = GameMain.NilMod.DisconnectedCharacters.Find(dc => dc.character.Name == c.Name && c.Connection.RemoteEndPoint.Address.ToString() == dc.IPAddress);

                                if(disconnectedcharcheck != null)
                                {
                                    GameMain.Server.SetClientCharacter(c, disconnectedcharcheck.character);
                                    disconnectedcharcheck.TimeUntilKill = GameMain.NilMod.ReconnectTimeAllowed * 1.5f;
                                }
                            }
                            else
                            {
                                lastEntityEventID = (UInt16)(c.UnreceivedEntityEventCount - 1);
                            }
                        }

                        if (NetIdUtils.IdMoreRecent(lastRecvChatMsgID, c.LastRecvChatMsgID) &&   //more recent than the last ID received by the client
                            !NetIdUtils.IdMoreRecent(lastRecvChatMsgID, c.LastChatMsgQueueID)) //NOT more recent than the latest existing ID
                        {
                            c.LastRecvChatMsgID = lastRecvChatMsgID;
                        }
                        else if (lastRecvChatMsgID != c.LastRecvChatMsgID && GameSettings.VerboseLogging)
                        {
                            DebugConsole.ThrowError(
                                "Invalid lastRecvChatMsgID  " + lastRecvChatMsgID + 
                                " (previous: " + c.LastChatMsgQueueID + ", latest: "+c.LastChatMsgQueueID+")");
                        }

                        if (NetIdUtils.IdMoreRecent(lastRecvEntityEventID, c.LastRecvEntityEventID) &&
                            !NetIdUtils.IdMoreRecent(lastRecvEntityEventID, lastEntityEventID))
                        {
                            c.LastRecvEntityEventID = lastRecvEntityEventID;
                        }
                        else if (lastRecvEntityEventID != c.LastRecvEntityEventID && GameSettings.VerboseLogging)
                        {
                            DebugConsole.ThrowError(
                                "Invalid lastRecvEntityEventID  " + lastRecvEntityEventID + 
                                " (previous: " + c.LastRecvEntityEventID + ", latest: " + lastEntityEventID + ")");
                        }
                        break;
                    case ClientNetObject.CHAT_MESSAGE:
                        ChatMessage.ServerRead(inc, c);
                        break;
                    case ClientNetObject.CHARACTER_INPUT:
                        if (c.Character != null)
                        {
                            c.Character.ServerRead(objHeader, inc, c);
                        }
                        break;
                    case ClientNetObject.ENTITY_STATE:
                        entityEventManager.Read(inc, c);
                        break;
                    case ClientNetObject.VOTE:
                        Voting.ServerRead(inc, c);
                        break;
                    default:
                        return;
                }

                //don't read further messages if the client has been disconnected (kicked due to spam for example)
                if (!connectedClients.Contains(c)) break;
            }
        }

        private void ClientReadServerCommand(NetIncomingMessage inc)
        {
            Client sender = ConnectedClients.Find(x => x.Connection == inc.SenderConnection);
            if (sender == null)
            {
                inc.SenderConnection.Disconnect("You're not a connected client.");
                return;
            }

            ClientPermissions command = ClientPermissions.None;
            try
            {
                command = (ClientPermissions)inc.ReadByte();
            }

            catch
            {
                return;
            }

            if (!sender.HasPermission(command))
            {
                Log("Client \"" + sender.Name + "\" sent a server command \"" + command + "\". Permission denied.", ServerLog.MessageType.ServerMessage);
                return;
            }

            switch (command)
            {
                case ClientPermissions.Kick:
                    string kickedName = inc.ReadString().ToLowerInvariant();
                    string kickReason = inc.ReadString();
                    var kickedClient = connectedClients.Find(cl => cl != sender && cl.Name.ToLowerInvariant() == kickedName);
                    if (kickedClient != null)
                    {
                        Log("Client \"" + sender.Name + "\" kicked \"" + kickedClient.Name + "\".", ServerLog.MessageType.ServerMessage);
                        KickClient(kickedClient, string.IsNullOrEmpty(kickReason) ? "Kicked by " + sender.Name : kickReason, GameMain.NilMod.AdminKickStateNameTimer, GameMain.NilMod.AdminKickDenyRejoinTimer);
                    }
                    break;
                case ClientPermissions.Ban:
                    string bannedName = inc.ReadString().ToLowerInvariant();
                    string banReason = inc.ReadString();
                    var bannedClient = connectedClients.Find(cl => cl != sender && cl.Name.ToLowerInvariant() == bannedName);
                    if (bannedClient != null)
                    {
                        Log("Client \"" + sender.Name + "\" banned \"" + bannedClient.Name + "\".", ServerLog.MessageType.ServerMessage);
                        BanClient(bannedClient, string.IsNullOrEmpty(banReason) ? "Banned by " + sender.Name : banReason, false);
                    }
                    break;
                case ClientPermissions.EndRound:
                    if (gameStarted)
                    {
                        Log("Client \"" + sender.Name + "\" ended the round.", ServerLog.MessageType.ServerMessage);
                        EndGame();
                    }
                    break;
                case ClientPermissions.SelectSub:
                    UInt16 subIndex = inc.ReadUInt16();
                    var subList = GameMain.NetLobbyScreen.GetSubList();
                    if (subIndex >= subList.Count)
                    {
                        DebugConsole.NewMessage("Client \"" + sender.Name + "\" attempted to select a sub, index out of bounds (" + subIndex + ")", Color.Red);
                    }
                    else
                    {
                        GameMain.NetLobbyScreen.SelectedSub = subList[subIndex];
                    }
                    break;
                case ClientPermissions.SelectMode:
                    UInt16 modeIndex = inc.ReadUInt16();
                    var modeList = GameMain.NetLobbyScreen.SelectedModeIndex = modeIndex;
                    break;
                case ClientPermissions.ManageCampaign:
                    MultiplayerCampaign campaign = GameMain.GameSession.GameMode as MultiplayerCampaign;
                    if (campaign != null)
                    {
                        campaign.ServerRead(inc, sender);
                    }
                    break;
            }

            inc.ReadPadBits();
        }


        private void ClientWrite(Client c)
        {
            //Send a packet
            if(c.NilModSyncResendTimer >= 0f && c.RequiresNilModSync)
            {
                GameMain.NilMod.ServerSyncWrite(c);
                c.NilModSyncResendTimer = NilMod.SyncResendInterval;
            }

            if (gameStarted && c.InGame)
            {
                if(GameMain.NilMod.UseAlternativeNetworking)
                {
                    ClientWriteIngamenew(c);
                }
                else
                {
                    ClientWriteIngame(c);
                }
            }
            else
            {
                //if 30 seconds have passed since the round started and the client isn't ingame yet,
                //kill the client's character
                if (gameStarted && c.Character != null && (DateTime.Now - roundStartTime).Seconds > (GameMain.NilMod.AllowReconnect ? Math.Max(GameMain.NilMod.ReconnectTimeAllowed, 30f) : 30.0f))
                {
                    if (GameMain.NilMod.DisconnectedCharacters.Count > 0)
                    {
                        if(GameMain.NilMod.DisconnectedCharacters.Find(dc => dc.character == c.Character) == null) c.Character.Kill(CauseOfDeath.Disconnected);
                    }
                    else
                    {
                        c.Character.Kill(CauseOfDeath.Disconnected);
                    }
                    //c.Character = null;
                }

                ClientWriteLobby(c);

                MultiplayerCampaign campaign = GameMain.GameSession?.GameMode as MultiplayerCampaign;
                if (campaign != null && NetIdUtils.IdMoreRecent(campaign.LastSaveID, c.LastRecvCampaignSave))
                { 
                    if (!fileSender.ActiveTransfers.Any(t => t.Connection == c.Connection && t.FileType == FileTransferType.CampaignSave))
                    {
                        fileSender.StartTransfer(c.Connection, FileTransferType.CampaignSave, GameMain.GameSession.SavePath);
                    }
                }
            }
        }

        /// <summary>
        /// Write info that the client needs when joining the server
        /// </summary>
        private void ClientWriteInitial(Client c, NetBuffer outmsg)
        {
            if (GameSettings.VerboseLogging)
            {
                DebugConsole.NewMessage("Sending initial lobby update", Color.Gray);
            }

            outmsg.Write(c.ID);

            var subList = GameMain.NetLobbyScreen.GetSubList();
            outmsg.Write((UInt16)subList.Count);
            for (int i = 0; i < subList.Count; i++)
            {
                outmsg.Write(subList[i].Name);
                outmsg.Write(subList[i].MD5Hash.ToString());
            }

            //Nilmod Sync joining client code
            //if(c.IsNilModClient)
            //{
            //    c.RequiresNilModSync = true;
            //    c.SyncResendTimer = 4f;
            //}

            outmsg.Write(GameStarted);
            outmsg.Write(AllowSpectating);

            outmsg.Write((byte)c.Permissions);
        }

        private void ClientWriteIngame(Client c)
        {
            //don't send position updates to characters who are still midround syncing
            //characters or items spawned mid-round don't necessarily exist at the client's end yet
            if (!c.NeedsMidRoundSync)
            {
                foreach (Character character in Character.CharacterList)
                {
                    if (!character.Enabled) continue;
                    if (c.Character != null &&
                        (Vector2.DistanceSquared(character.WorldPosition, c.Character.WorldPosition) >=
                        NetConfig.CharacterIgnoreDistanceSqr) && (!character.IsRemotePlayer && !c.Character.IsDead))
                    {
                        continue;
                    }
                    if (!c.PendingPositionUpdates.Contains(character)) c.PendingPositionUpdates.Enqueue(character);
                }

                foreach (Submarine sub in Submarine.Loaded)
                {
                    //if docked to a sub with a smaller ID, don't send an update
                    //  (= update is only sent for the docked sub that has the smallest ID, doesn't matter if it's the main sub or a shuttle)
                    if (sub.DockedTo.Any(s => s.ID < sub.ID)) continue;
                    if (!c.PendingPositionUpdates.Contains(sub)) c.PendingPositionUpdates.Enqueue(sub);
                }

                foreach (Item item in Item.ItemList)
                {
                    if (!item.NeedsPositionUpdate) continue;
                    if (!c.PendingPositionUpdates.Contains(item)) c.PendingPositionUpdates.Enqueue(item);
                }
            }

            NetOutgoingMessage outmsg = server.CreateMessage();
            outmsg.Write((byte)ServerPacketHeader.UPDATE_INGAME);
            
            outmsg.Write((float)NetTime.Now);

            outmsg.Write((byte)ServerNetObject.SYNC_IDS);
            outmsg.Write(c.LastSentChatMsgID); //send this to client so they know which chat messages weren't received by the server
            outmsg.Write(c.LastSentEntityEventID);

            entityEventManager.Write(c, outmsg);

            WriteChatMessages(outmsg, c);

            //write as many position updates as the message can fit
            while (outmsg.LengthBytes < config.MaximumTransmissionUnit - 20 && 
                c.PendingPositionUpdates.Count > 0)
            {
                var entity = c.PendingPositionUpdates.Dequeue();
                if (entity == null || entity.Removed) continue;

                outmsg.Write((byte)ServerNetObject.ENTITY_POSITION);
                if (entity is Item)
                {
                    ((Item)entity).ServerWritePosition(outmsg, c);
                }
                else
                {
                    ((IServerSerializable)entity).ServerWrite(outmsg, c);
                }
                outmsg.WritePadBits();
            }

            outmsg.Write((byte)ServerNetObject.END_OF_MESSAGE);

            if (outmsg.LengthBytes > config.MaximumTransmissionUnit)
            {
                if (GameMain.NilMod.ShowPacketMTUErrors)
                {
                    DebugConsole.NewMessage("Maximum packet size exceeded (" + outmsg.LengthBytes + " > " + config.MaximumTransmissionUnit + ") in GameServer.ClientWriteLobby()", Color.Red);
                }
            }

            server.SendMessage(outmsg, c.Connection, NetDeliveryMethod.Unreliable);
        }

        private void ClientWriteLobby(Client c)
        {
            bool isInitialUpdate = false;

            NetOutgoingMessage outmsg = server.CreateMessage();
            outmsg.Write((byte)ServerPacketHeader.UPDATE_LOBBY);

            outmsg.Write((byte)ServerNetObject.SYNC_IDS);

            if (NetIdUtils.IdMoreRecent(GameMain.NetLobbyScreen.LastUpdateID, c.LastRecvGeneralUpdate))
            {
                outmsg.Write(true);
                outmsg.WritePadBits();

                outmsg.Write(GameMain.NetLobbyScreen.LastUpdateID);
                outmsg.Write(GameMain.NetLobbyScreen.GetServerName());
                outmsg.Write(GameMain.NetLobbyScreen.ServerMessageText);
                
                outmsg.Write(c.LastRecvGeneralUpdate < 1);
                if (c.LastRecvGeneralUpdate < 1)
                {
                    isInitialUpdate = true;
                    ClientWriteInitial(c, outmsg);
                }
                outmsg.Write(GameMain.NetLobbyScreen.SelectedSub.Name);
                outmsg.Write(GameMain.NetLobbyScreen.SelectedSub.MD5Hash.ToString());
                outmsg.Write(GameMain.NetLobbyScreen.SelectedShuttle.Name);
                outmsg.Write(GameMain.NetLobbyScreen.SelectedShuttle.MD5Hash.ToString());

                outmsg.Write(Voting.AllowSubVoting);
                outmsg.Write(Voting.AllowModeVoting);

                outmsg.Write(AllowSpectating);

                outmsg.WriteRangedInteger(0, 2, (int)TraitorsEnabled);

                outmsg.WriteRangedInteger(0, Mission.MissionTypes.Count - 1, (GameMain.NetLobbyScreen.MissionTypeIndex));

                outmsg.Write((byte)GameMain.NetLobbyScreen.SelectedModeIndex);
                outmsg.Write(GameMain.NetLobbyScreen.LevelSeed);

                outmsg.Write(AutoRestart);
                if (autoRestart)
                {
                    outmsg.Write(AutoRestartTimer);
                }

                outmsg.Write((byte)connectedClients.Count);
                foreach (Client client in connectedClients)
                {
                    outmsg.Write(client.ID);
                    outmsg.Write(client.Name);
                    outmsg.Write(client.Character == null || !gameStarted ? (ushort)0 : client.Character.ID);
                }
            }
            else
            {
                outmsg.Write(false);
                outmsg.WritePadBits();
            }

            var campaign = GameMain.GameSession?.GameMode as MultiplayerCampaign;
            if (campaign != null)
            {
                if (NetIdUtils.IdMoreRecent(campaign.LastUpdateID, c.LastRecvCampaignUpdate))
                {
                    outmsg.Write(true);
                    outmsg.WritePadBits();
                    campaign.ServerWrite(outmsg, c);
                }
                else
                {
                    outmsg.Write(false);
                    outmsg.WritePadBits();
                }
            }
            else
            {
                outmsg.Write(false);
                outmsg.WritePadBits();
            }
            
            outmsg.Write(c.LastSentChatMsgID); //send this to client so they know which chat messages weren't received by the server
            
            WriteChatMessages(outmsg, c);

            outmsg.Write((byte)ServerNetObject.END_OF_MESSAGE);

            if (isInitialUpdate)
            {
                //the initial update may be very large if the host has a large number
                //of submarine files, so the message may have to be fragmented

                //unreliable messages don't play nicely with fragmenting, so we'll send the message reliably
                server.SendMessage(outmsg, c.Connection, NetDeliveryMethod.ReliableUnordered);

                //and assume the message was received, so we don't have to keep resending
                //these large initial messages until the client acknowledges receiving them
                c.LastRecvGeneralUpdate++;

                //Nilmod Rules code

                if (NilMod.NilModEventChatter.NilModRules.Count() > 0 && NilMod.NilModEventChatter.ChatModServerJoin)
                {
                    foreach (string message in NilMod.NilModEventChatter.NilModRules)
                    {
                        NilMod.NilModEventChatter.SendServerMessage(message, c);
                    }

                }

                SendVoteStatus(new List<Client>() { c });
            }
            else
            {
                if (outmsg.LengthBytes > config.MaximumTransmissionUnit)
                {
                    if (GameMain.NilMod.ShowPacketMTUErrors)
                    {
                        DebugConsole.NewMessage("Maximum packet size exceeded (" + outmsg.LengthBytes + " > " + config.MaximumTransmissionUnit + ") in GameServer.ClientWriteLobby()", Color.Red);
                    }
                }

                server.SendMessage(outmsg, c.Connection, NetDeliveryMethod.Unreliable);
            }
        }

        private void WriteChatMessages(NetOutgoingMessage outmsg, Client c)
        {
            c.ChatMsgQueue.RemoveAll(cMsg => !NetIdUtils.IdMoreRecent(cMsg.NetStateID, c.LastRecvChatMsgID));
            for (int i = 0; i < c.ChatMsgQueue.Count && i < ChatMessage.MaxMessagesPerPacket; i++)
            {
                if (outmsg.LengthBytes + c.ChatMsgQueue[i].EstimateLengthBytesServer(c) > config.MaximumTransmissionUnit - 5)
                {
                    //not enough room in this packet
                    return;
                }
                c.ChatMsgQueue[i].ServerWrite(outmsg, c);
            }
        }
        
        public bool StartGame()
        {

            GameMain.NilMod.SubmarineVoters = null;
            Submarine selectedSub = null;
            Submarine selectedShuttle = GameMain.NetLobbyScreen.SelectedShuttle;

            if (Voting.AllowSubVoting)
            {
                selectedSub = Voting.HighestVoted<Submarine>(VoteType.Sub, connectedClients);

                if (selectedSub != null)
                {
                    //record the voters
                    GameMain.NilMod.SubmarineVoters = selectedSub + " Voted by:";
                    foreach (Client c in ConnectedClients)
                    {
                        if (c.GetVote<Submarine>(VoteType.Sub) == selectedSub)
                        {

                            GameMain.NilMod.SubmarineVoters += " " + c.Name + ",";
                        }
                    }

                    //remove the comma
                    GameMain.NilMod.SubmarineVoters = GameMain.NilMod.SubmarineVoters.Substring(0, GameMain.NilMod.SubmarineVoters.Length - 1);
                }

                if (GameMain.NilMod.SubmarineVoters != null)
                {
                    if (GameMain.NilMod.SubVotingAnnounce)
                    {
                        foreach (Client c in ConnectedClients)
                        {
                            if (GameMain.NilMod.SubmarineVoters.Length > 160)
                            {
                                NilMod.NilModEventChatter.SendServerMessage(GameMain.NilMod.SubmarineVoters.Substring(0, 157) + "...", c);
                            }
                            else
                            {
                                NilMod.NilModEventChatter.SendServerMessage(GameMain.NilMod.SubmarineVoters, c);
                            }

                        }
                    }
                    if(GameMain.NilMod.SubVotingConsoleLog)
                    {
                        DebugConsole.NewMessage(GameMain.NilMod.SubmarineVoters, Color.White);
                    }
                }

                if (selectedSub == null)
                {
                    if (GameMain.NilMod.SubVotingConsoleLog)
                    {
                        DebugConsole.NewMessage("No clients voted a submarine, choosing default submarine: " + GameMain.NetLobbyScreen.SelectedSub, Color.White);
                    }
                    selectedSub = GameMain.NetLobbyScreen.SelectedSub;
                }
            }
            else
            {
                selectedSub = GameMain.NetLobbyScreen.SelectedSub;
            }

            if (selectedSub == null)
            {
#if CLIENT
                GameMain.NetLobbyScreen.SubList.Flash();
#endif
                return false;
            }

            if (selectedShuttle == null)
            {
#if CLIENT
                GameMain.NetLobbyScreen.ShuttleList.Flash();
#endif
                return false;
            }

            GameModePreset selectedMode = Voting.HighestVoted<GameModePreset>(VoteType.Mode, connectedClients);
            if (selectedMode == null) selectedMode = GameMain.NetLobbyScreen.SelectedMode;

            if (selectedMode == null)
            {
#if CLIENT
                GameMain.NetLobbyScreen.ModeList.Flash();
#endif
                return false;
            }

            CoroutineManager.StartCoroutine(InitiateStartGame(selectedSub, selectedShuttle, selectedMode), "InitiateStartGame");

            return true;
        }

        private IEnumerable<object> InitiateStartGame(Submarine selectedSub, Submarine selectedShuttle, GameModePreset selectedMode)
        {
            initiatedStartGame = true;
            GameMain.NetLobbyScreen.StartButtonEnabled = false;

            if (connectedClients.Any())
            {
                NetOutgoingMessage msg = server.CreateMessage();
                msg.Write((byte)ServerPacketHeader.QUERY_STARTGAME);

                msg.Write(selectedSub.Name);
                msg.Write(selectedSub.MD5Hash.Hash);

                msg.Write(selectedShuttle.Name);
                msg.Write(selectedShuttle.MD5Hash.Hash);

                connectedClients.ForEach(c => c.ReadyToStart = false);

                server.SendMessage(msg, connectedClients.Select(c => c.Connection).ToList(), NetDeliveryMethod.ReliableUnordered, 0);

                //give the clients a few seconds to request missing sub/shuttle files before starting the round
                float waitForResponseTimer = 5.0f;
                while (connectedClients.Any(c => !c.ReadyToStart) && waitForResponseTimer > 0.0f)
                {
                    waitForResponseTimer -= CoroutineManager.UnscaledDeltaTime;
                    yield return CoroutineStatus.Running;
                }

                if (fileSender.ActiveTransfers.Count > 0)
                {
#if CLIENT
                    var msgBox = new GUIMessageBox("", "Waiting for file transfers to finish before starting the round...", new string[] { "Start now" });
                    msgBox.Buttons[0].OnClicked += msgBox.Close;
#endif

                    float waitForTransfersTimer = 20.0f;
                    while (fileSender.ActiveTransfers.Count > 0 && waitForTransfersTimer > 0.0f)
                    {
                        waitForTransfersTimer -= CoroutineManager.UnscaledDeltaTime;

#if CLIENT
                        //message box close, break and start the round immediately
                        if (!GUIMessageBox.MessageBoxes.Contains(msgBox))
                        {
                            break;
                        }
#endif

                        yield return CoroutineStatus.Running;
                    }
                }
            }
#if CLIENT
            LoadingScreen.loadType = LoadType.Server;
#endif
            startGameCoroutine = GameMain.Instance.ShowLoading(StartGame(selectedSub, selectedShuttle, selectedMode), false);

            yield return CoroutineStatus.Success;
        }

        private IEnumerable<object> StartGame(Submarine selectedSub, Submarine selectedShuttle, GameModePreset selectedMode)
        {
            entityEventManager.Clear();

            GameMain.NetLobbyScreen.StartButtonEnabled = false;

#if CLIENT
            GUIMessageBox.CloseAll();
#endif

            roundStartSeed = DateTime.Now.Millisecond;
            Rand.SetSyncedSeed(roundStartSeed);
            
            int teamCount = 1;
            byte hostTeam = 1;

            //Saves the log into a file
            if (GameMain.NilMod.ClearLogRoundStart)
            {
                ServerLog.ClearLog();
            }

            //Reload the banlist on round starts
            BanList.load();

            LoadClientPermissions();

            MultiplayerCampaign campaign = GameMain.NetLobbyScreen.SelectedMode == GameMain.GameSession?.GameMode.Preset ? 
                GameMain.GameSession?.GameMode as MultiplayerCampaign : null;
        
            //don't instantiate a new gamesession if we're playing a campaign
            if (campaign == null || GameMain.GameSession == null)
            {
                GameMain.GameSession = new GameSession(selectedSub, "", selectedMode, Mission.MissionTypes[GameMain.NetLobbyScreen.MissionTypeIndex]);
            }

            if (GameMain.GameSession.GameMode.Mission != null &&
                GameMain.GameSession.GameMode.Mission.AssignTeamIDs(connectedClients, out hostTeam))
            {
                teamCount = 2;
            }
            else
            {
                connectedClients.ForEach(c => c.TeamID = hostTeam);
            }

            //Initialize server defaults
            GameMain.NilMod.GameInitialize(false);

            if (campaign != null)
            {
#if CLIENT
                if (GameMain.GameSession?.CrewManager != null) GameMain.GameSession.CrewManager.Reset();
#endif
                GameMain.GameSession.StartRound(campaign.Map.SelectedConnection.Level, true, teamCount > 1);
            }
            else
            {
                GameMain.GameSession.StartRound(GameMain.NetLobbyScreen.LevelSeed, teamCount > 1);
            }

            if(GameMain.NilMod.SubVotingServerLog && GameMain.NilMod.SubmarineVoters != null)
            {
                GameServer.Log(GameMain.NilMod.SubmarineVoters, ServerLog.MessageType.ServerMessage);
            }

            GameServer.Log("Starting a new round...", ServerLog.MessageType.ServerMessage);
            GameServer.Log("Submarine: " + selectedSub.Name, ServerLog.MessageType.ServerMessage);
            GameServer.Log("Game mode: " + selectedMode.Name, ServerLog.MessageType.ServerMessage);
            GameServer.Log("Level seed: " + GameMain.NetLobbyScreen.LevelSeed, ServerLog.MessageType.ServerMessage);

            bool missionAllowRespawn = campaign == null &&
                (!(GameMain.GameSession.GameMode is MissionMode) || 
                ((MissionMode)GameMain.GameSession.GameMode).Mission.AllowRespawn);

            if (AllowRespawn && missionAllowRespawn) respawnManager = new RespawnManager(this, selectedShuttle);

            //assign jobs and spawnpoints separately for each team
            for (int teamID = 1; teamID <= teamCount; teamID++)
            {
                //find the clients in this team
                List<Client> teamClients = teamCount == 1 ? new List<Client>(connectedClients) : connectedClients.FindAll(c => c.TeamID == teamID);
                if (AllowSpectating)
                {
                    teamClients.RemoveAll(c => c.SpectateOnly);
                }

                if (!teamClients.Any() && teamID > 1) continue;

                AssignJobs(teamClients, teamID == hostTeam);

                List<CharacterInfo> characterInfos = new List<CharacterInfo>();
                foreach (Client client in teamClients)
                {
                    client.NeedsMidRoundSync = false;

                    client.PendingPositionUpdates.Clear();
                    client.EntityEventLastSent.Clear();
                    client.LastSentEntityEventID = 0;
                    client.LastRecvEntityEventID = 0;
                    client.UnreceivedEntityEventCount = 0;

                    if (client.CharacterInfo == null)
                    {
                        client.CharacterInfo = new CharacterInfo(Character.HumanConfigFile, client.Name);
                    }
                    characterInfos.Add(client.CharacterInfo);
                    client.CharacterInfo.Job = new Job(client.AssignedJob);
                }

                //host's character
                if (characterInfo != null && hostTeam == teamID)
                {
                    characterInfo.Job = new Job(GameMain.NetLobbyScreen.JobPreferences[0]);
                    characterInfos.Add(characterInfo);
                }

                WayPoint[] assignedWayPoints = WayPoint.SelectCrewSpawnPoints(characterInfos, Submarine.MainSubs[teamID - 1]);
                for (int i = 0; i < teamClients.Count; i++)
                {
                    Character spawnedCharacter = Character.Create(teamClients[i].CharacterInfo, assignedWayPoints[i].WorldPosition, true, false);
                    spawnedCharacter.AnimController.Frozen = true;
                    spawnedCharacter.GiveJobItems(assignedWayPoints[i]);
                    spawnedCharacter.TeamID = (byte)teamID;

                    teamClients[i].Character = spawnedCharacter;
#if CLIENT
                    GameSession.inGameInfo.UpdateClientCharacter(teamClients[i], spawnedCharacter, false);
#endif

#if CLIENT
                    GameMain.GameSession.CrewManager.AddCharacter(spawnedCharacter);
#endif
                }

#if CLIENT
                if (characterInfo != null && hostTeam == teamID)
                {
                    if (GameMain.NilMod.PlayYourselfName.Length > 0)
                    {
                        if (GameMain.NilMod.PlayYourselfName != "")
                        {
                            characterInfo.Name = GameMain.NilMod.PlayYourselfName;
                        }
                    }

                    myCharacter = Character.Create(characterInfo, assignedWayPoints[assignedWayPoints.Length - 1].WorldPosition, false, false);
                    myCharacter.GiveJobItems(assignedWayPoints.Last());
                    myCharacter.TeamID = (byte)teamID;

                    Character.Controlled = myCharacter;

                    GameSession.inGameInfo.AddNoneClientCharacter(myCharacter, true);

                    GameMain.GameSession.CrewManager.AddCharacter(myCharacter);
                }
#endif
                if (teamID == 1)
                {
                    GameServer.Log("Spawning initial Crew: Coalition", ServerLog.MessageType.Spawns);

                    //Log the hosts character which is always team #1
                    if (Character.Controlled != null)
                    {
                        GameServer.Log("spawn: " + Character.Controlled.Name + " As " + Character.Controlled.Info.Job.Name + " As Host", ServerLog.MessageType.Spawns);
                    }
                }
                if (teamID == 2)
                {
                    GameServer.Log("Spawning initial Crew: Renegades", ServerLog.MessageType.Spawns);
                }

                //List the players for the given team
                foreach (Client client in teamClients)
                {
                    GameServer.Log("spawn: " + client.CharacterInfo.Name + " As " + client.CharacterInfo.Job.Name + " On " + client.Connection.RemoteEndPoint.Address, ServerLog.MessageType.Spawns);
                }

            }
            //Locks the wiring if its set to.
            if (!GameMain.NilMod.CanRewireMainSubs)
            {
                foreach (Item item in Item.ItemList)
                {
                    //lock all wires to prevent the players from messing up the electronics
                    var connectionPanel = item.GetComponent<ConnectionPanel>();
                    if (connectionPanel != null && (item.Submarine == Submarine.MainSubs[0] || ((Submarine.MainSubs.Count() > 1 && item.Submarine == Submarine.MainSubs[1]))))
                    {
                        foreach (Connection connection in connectionPanel.Connections)
                        {
                            Array.ForEach(connection.Wires, w => { if (w != null) w.Locked = true; });
                        }
                    }
                }
            }

            foreach (Submarine sub in Submarine.MainSubs)
            {
                if (sub == null) continue;

                WayPoint cargoSpawnPos = WayPoint.GetRandom(SpawnType.Cargo, null, sub);

                if (cargoSpawnPos?.CurrentHull == null)
                {
                    DebugConsole.ThrowError("Couldn't spawn additional cargo (no cargo spawnpoint inside any of the hulls)");
                    continue;
                }

                var cargoRoom = cargoSpawnPos.CurrentHull;
                Vector2 position = new Vector2(
                    cargoSpawnPos.Position.X,
                    cargoRoom.Rect.Y - cargoRoom.Rect.Height);

                foreach (string s in extraCargo.Keys)
                {
                    ItemPrefab itemPrefab = MapEntityPrefab.Find(s) as ItemPrefab;
                    if (itemPrefab == null) continue;

                    for (int i = 0; i < extraCargo[s]; i++)
                    {
                        Entity.Spawner.AddToSpawnQueue(itemPrefab,  position + new Vector2(Rand.Range(-20.0f, 20.0f), itemPrefab.Size.Y / 2), sub);
                    }
                }


            }

            TraitorManager = null;
            if (TraitorsEnabled == YesNoMaybe.Yes ||
                (TraitorsEnabled == YesNoMaybe.Maybe && Rand.Range(0.0f, 1.0f) < 0.5f))
            {
                TraitorManager = new TraitorManager(this);

                if (TraitorManager.TraitorCharacter!=null && TraitorManager.TargetCharacter != null)
                {
                    //Nilmod Traitor Stuff
                    GameMain.NilMod.Traitor = TraitorManager.TraitorCharacter.Name;
                    GameMain.NilMod.TraitorTarget = TraitorManager.TargetCharacter.Name;
                    Log(TraitorManager.TraitorCharacter.Name + " is the traitor and the target is " + TraitorManager.TargetCharacter.Name, ServerLog.MessageType.ServerMessage);
                }
                else
                {
                    GameMain.NilMod.Traitor = "";
                    GameMain.NilMod.TraitorTarget = "";
                }
            }

            SendStartMessage(roundStartSeed, Submarine.MainSub, GameMain.GameSession.GameMode.Preset, connectedClients);

            yield return CoroutineStatus.Running;
            
            GameMain.GameScreen.Cam.TargetPos = Vector2.Zero;
            GameMain.GameScreen.Select();

            AddChatMessage("Press TAB to chat. Use \"r;\" to talk through the radio.", ChatMessageType.Server);

            GameMain.NetLobbyScreen.StartButtonEnabled = true;

            gameStarted = true;
            initiatedStartGame = false;

            roundStartTime = DateTime.Now;

            //Custom Nilmod Roundstart Messages for other players
            if (GameMain.NilMod.EnableEventChatterSystem)
            {
                foreach (Client receivingclient in ConnectedClients)
                {
                    NilMod.NilModEventChatter.RoundStartClientMessages(receivingclient);
                }

                NilMod.NilModEventChatter.SendHostMessages();
            }

#if CLIENT
            GameSession.inGameInfo.UpdateGameInfoGUIList();
#endif

            GameServer.Log("Debug: Round start complete.", ServerLog.MessageType.ServerMessage);

            yield return CoroutineStatus.Success;
        }

        private void SendStartMessage(int seed, Submarine selectedSub, GameModePreset selectedMode, List<Client> clients)
        {
            foreach (Client client in clients)
            {
                SendStartMessage(seed, selectedSub, selectedMode, client);
            }       
        }

        private void SendStartMessage(int seed, Submarine selectedSub, GameModePreset selectedMode, Client client)
        {
            NetOutgoingMessage msg = server.CreateMessage();
            msg.Write((byte)ServerPacketHeader.STARTGAME);

            msg.Write(seed);

            msg.Write(GameMain.GameSession.Level.Seed);

            msg.Write((byte)GameMain.NetLobbyScreen.MissionTypeIndex);

            msg.Write(selectedSub.Name);
            msg.Write(selectedSub.MD5Hash.Hash);

            msg.Write(GameMain.NetLobbyScreen.SelectedShuttle.Name);
            msg.Write(GameMain.NetLobbyScreen.SelectedShuttle.MD5Hash.Hash);

            msg.Write(selectedMode.Name);

            MultiplayerCampaign campaign = GameMain.GameSession?.GameMode as MultiplayerCampaign;

            bool missionAllowRespawn = campaign == null &&
                (!(GameMain.GameSession.GameMode is MissionMode) ||
                ((MissionMode)GameMain.GameSession.GameMode).Mission.AllowRespawn);

            msg.Write(AllowRespawn && missionAllowRespawn);
            msg.Write(Submarine.MainSubs[1] != null); //loadSecondSub

            if (TraitorManager != null &&
                TraitorManager.TraitorCharacter != null &&
                TraitorManager.TargetCharacter != null &&
                TraitorManager.TraitorCharacter == client.Character)
            {
                msg.Write(true);
                msg.Write(TraitorManager.TargetCharacter.Name);
            }
            else
            {
                msg.Write(false);
            }

            //monster spawn settings
            List<string> monsterNames = monsterEnabled.Keys.ToList();
            foreach (string s in monsterNames)
            {
                msg.Write(monsterEnabled[s]);
            }
            msg.WritePadBits();

            server.SendMessage(msg, client.Connection, NetDeliveryMethod.ReliableUnordered);     
        }

        public void EndGame()
        {
            if (!gameStarted) return;

            string endMessage = "The round has ended." + '\n';

#if CLIENT
            ClearClickCommand();
            GameSession.inGameInfo.ResetGUIListData();
#endif

            if (TraitorManager != null)
            {
                endMessage += TraitorManager.GetEndMessage();
            }

            Mission mission = GameMain.GameSession.Mission;
            GameMain.GameSession.GameMode.End(endMessage);

            if (autoRestart)
            {
                AutoRestartTimer = AutoRestartInterval;
                //send a netlobby update to get the clients' autorestart timers up to date 
                GameMain.NetLobbyScreen.LastUpdateID++;
            }

            //NilMod Logging changes - Allow logs to save and end + clear potentially at round starts, not at round ends, and thus change saving to that.
            //Also makes all chat after a previous round go into the end of the last rounds log file.
            if (!GameMain.NilMod.ClearLogRoundStart)
            {
                if (SaveServerLogs) log.Save();
            }

            Character.Controlled = null;
            
            GameMain.GameScreen.Cam.TargetPos = Vector2.Zero;
#if CLIENT
            myCharacter = null;
            GameMain.LightManager.LosEnabled = false;
#endif

            entityEventManager.Clear();
            foreach (Client c in connectedClients)
            {
                c.EntityEventLastSent.Clear();
                c.PendingPositionUpdates.Clear();
            }

#if DEBUG
            messageCount.Clear();
#endif

            respawnManager = null;
            gameStarted = false;

            if (connectedClients.Count > 0)
            {
                NetOutgoingMessage msg = server.CreateMessage();
                msg.Write((byte)ServerPacketHeader.ENDGAME);
                msg.Write(endMessage);
                msg.Write(mission != null && mission.Completed);
                if (server.ConnectionsCount > 0)
                {
                    server.SendMessage(msg, server.Connections, NetDeliveryMethod.ReliableOrdered, 0);
                }

                foreach (Client client in connectedClients)
                {
                    client.Character = null;
                    client.InGame = false;
                }
            }

            CoroutineManager.StartCoroutine(EndCinematic(),"EndCinematic");
            BanList.load();
        }

        public IEnumerable<object> EndCinematic()
        {
            float endPreviewLength = 10.0f;
            
            var cinematic = new TransitionCinematic(Submarine.MainSub, GameMain.GameScreen.Cam, endPreviewLength);
            //float secondsLeft = endPreviewLength;

            do
            {
                //secondsLeft -= CoroutineManager.UnscaledDeltaTime;

                yield return CoroutineStatus.Running;
            } while (cinematic.Running);//(secondsLeft > 0.0f);

            Submarine.Unload();
            entityEventManager.Clear();

            GameMain.NetLobbyScreen.Select();

            yield return CoroutineStatus.Success;
        }

        public override void KickPlayer(string playerName, string reason, float Expiretime = 0f, float Rejointime = 0f)
        {
            playerName = playerName.ToLowerInvariant();

            Client client = connectedClients.Find(c =>
                c.Name.ToLowerInvariant() == playerName ||
                (c.Character != null && c.Character.Name.ToLowerInvariant() == playerName));

            KickClient(client, reason, Expiretime, Rejointime);
        }
                
        public void KickClient(NetConnection conn, string reason, float Expiretime = 0f, float Rejointime = 0f)
        {
            Client client = connectedClients.Find(c => c.Connection == conn);
            KickClient(client, reason, Expiretime, Rejointime);            
        }
        
        public void KickClient(Client client, string reason,float Expiretime = 0f, float Rejointime = 0f)
        {
            if (client == null) return;
            
            string msg = "You have been kicked from the server.";
            if (!string.IsNullOrWhiteSpace(reason)) msg += "\nReason: " + reason;
            DisconnectKickClient(client, client.Name + " has been kicked from the server.", msg, Expiretime, Rejointime);            
        }

        public override void BanPlayer(string playerName, string reason, bool range = false, TimeSpan? duration = null)
        {
            playerName = playerName.ToLowerInvariant();

            Client client = connectedClients.Find(c =>
                c.Name.ToLowerInvariant() == playerName ||
                (c.Character != null && c.Character.Name.ToLowerInvariant() == playerName));

            if (client == null)
            {
                DebugConsole.ThrowError("Client \"" + playerName + "\" not found.");
                return;
            }

            BanClient(client, reason, range, duration);
        }

        public void BanClient(NetConnection conn, string reason, bool range = false, TimeSpan? duration = null)
        {
            Client client = connectedClients.Find(c => c.Connection == conn);
            if (client == null)
            {
                conn.Disconnect("You have been banned from the server");
                if (!banList.IsBanned(conn.RemoteEndPoint.Address.ToString()))
                {
                    banList.BanPlayer("IP Banned", conn.RemoteEndPoint.Address.ToString(), reason, duration);
                }                
            }
            else
            {
                BanClient(client, reason, range);
            }
        }

        public void KickBannedClient(NetConnection conn, string reason)
        {
            Client client = connectedClients.Find(c => c.Connection == conn);
            if (client != null)
            {
                if (gameStarted && client.Character != null)
                {
                    client.Character.ClearInputs();
                    client.Character.Kill(CauseOfDeath.Disconnected, true);
                }
                client.Connection.Disconnect("You have been banned from the server\n" + reason);
                //conn.Disconnect("You have been banned from the server\n" + reason);
                SendChatMessage(client.Name + " has been banned from the server", ChatMessageType.Server);
            }
            else
            {
                conn.Disconnect("You have been banned from the server" + "\nReason: " + reason);
            }
        }

        public void KickVPNClient(NetConnection conn, string reason, string clname)
        {
            /*
            Client client = connectedClients.Find(c => c.Connection == conn);
            if (client != null)
            {
                if (gameStarted && client.Character != null)
                {
                    client.Character.ClearInputs();
                    client.Character.Kill(CauseOfDeath.Disconnected, true);
                }
                conn.Disconnect("You have been banned from the server\n" + reason);
                SendChatMessage(client.name + " has been VPN Blacklisted from the server", ChatMessageType.Server);
                
            }
            else
            {
                conn.Disconnect("You have been banned from the server" + "\nReason: " + reason);
                SendChatMessage(clname + " has been banned from the server", ChatMessageType.Server);
            }
            */

            Client client = connectedClients.Find(c => c.Connection == conn);
            if (client != null)
            {
                if (gameStarted && client.Character != null)
                {
                    client.Character.ClearInputs();
                    client.Character.Kill(CauseOfDeath.Disconnected, true);
                }
                GameServer.Log("VPN Blacklisted player: " + clname + " (" + client.Connection.RemoteEndPoint.Address.ToString() + ") attempted to join the server.", ServerLog.MessageType.Connection);
                DebugConsole.NewMessage("VPN Blacklisted player: " + clname + " (" + client.Connection.RemoteEndPoint.Address.ToString() + ") attempted to join the server.", Color.Red);
                if (GameMain.NilMod.BansInfoAddCustomString)
                {
                    client.Connection.Disconnect(reason + "\n\n" + GameMain.NilMod.BansInfoCustomtext);
                }
                else
                {
                    client.Connection.Disconnect(reason);
                }
                
                //conn.Disconnect("You have been banned from the server\n" + reason);
                SendChatMessage("VPN Blacklisted player: " + clname + " attempted to join the server.", ChatMessageType.Server);
#if CLIENT
                GameMain.NetLobbyScreen.RemovePlayer(client.Name);
#endif
                ConnectedClients.Remove(client);
            }
            else
            {
                conn.Disconnect("You have been banned from the server" + "\nReason: " + reason);
                DebugConsole.NewMessage("VPN Blacklisted player: " + clname + " (" + conn.RemoteEndPoint.Address.ToString() + ") attempted to join the server.", Color.Red);
                SendChatMessage("VPN Blacklisted player: " + clname + " attempted to join the server.", ChatMessageType.Server);
            }
        }

        public void BanClient(Client client, string reason, bool range = false, TimeSpan? duration = null)
        {
            if (client == null) return;
            
            string msg = "You have been banned from the server.";
            if (!string.IsNullOrWhiteSpace(reason)) msg += "\nReason: " + reason;
            DisconnectKickClient(client, client.Name + " has been banned from the server.", msg);
            string ip = client.Connection.RemoteEndPoint.Address.ToString();
            if (range) { ip = banList.ToRange(ip); }
            banList.BanPlayer(client.Name, ip, reason, duration);
        }

        public void DisconnectClient(NetConnection senderConnection, string msg = "", string targetmsg = "")
        {
            Client client = connectedClients.Find(x => x.Connection == senderConnection);
            if (client == null) return;

            DisconnectClient(client, msg, targetmsg);
        }

        public void DisconnectKickClient(Client client, string msg = "", string targetmsg = "", float expiretime = 0f, float rejointime = 0f)
        {
            if (client == null) return;

            if (expiretime > 0f || rejointime > 0f)
            {
                KickedClient kickedclient = null;

                if (GameMain.NilMod.KickedClients.Count > 0)
                {
                    kickedclient = GameMain.NilMod.KickedClients.Find(kc => kc.IPAddress == client.Connection.RemoteEndPoint.Address.ToString());
                }

                if (kickedclient != null)
                {
                    if (kickedclient.RejoinTimer < rejointime) kickedclient.RejoinTimer = rejointime;
                    if (kickedclient.ExpireTimer < expiretime) kickedclient.ExpireTimer = expiretime;
                }
                else
                {
                    kickedclient = new KickedClient();
                    kickedclient.clientname = client.Name;
                    kickedclient.IPAddress = client.Connection.RemoteEndPoint.Address.ToString();
                    kickedclient.RejoinTimer = rejointime;
                    kickedclient.ExpireTimer = expiretime;
                    kickedclient.KickReason = targetmsg;
                    GameMain.NilMod.KickedClients.Add(kickedclient);
                }
            }

            if (gameStarted && client.Character != null)
            {
                client.Character.ClearInputs();
                client.Character.Kill(CauseOfDeath.Disconnected, true);
            }

            client.Character = null;
            client.InGame = false;

            if (string.IsNullOrWhiteSpace(msg)) msg = client.Name + " has left the server";
            if (string.IsNullOrWhiteSpace(targetmsg)) targetmsg = "You have left the server";

            Log(msg, ServerLog.MessageType.ServerMessage);

            client.Connection.Disconnect(targetmsg);
            connectedClients.Remove(client);

#if CLIENT
            GameSession.inGameInfo.RemoveClient(client);
            GameMain.NetLobbyScreen.RemovePlayer(client.Name);
            Voting.UpdateVoteTexts(connectedClients, VoteType.Sub);
            Voting.UpdateVoteTexts(connectedClients, VoteType.Mode);
#endif

            UpdateVoteStatus();

            SendChatMessage(msg, ChatMessageType.Server);

            UpdateCrewFrame();

            refreshMasterTimer = DateTime.Now;
        }

        public void DisconnectClient(Client client, string msg = "", string targetmsg = "")
        {
            if (client == null) return;

            if(gameStarted && client.Character != null && GameMain.NilMod.AllowReconnect)
            {
                client.Character.ClearInputs();
                DisconnectedCharacter disconnectedchar = new DisconnectedCharacter();
                disconnectedchar.clientname = client.Name;
                disconnectedchar.IPAddress = client.Connection.RemoteEndPoint.Address.ToString();
                disconnectedchar.DisconnectStun = client.Character.Stun;
                disconnectedchar.character = client.Character;
                disconnectedchar.TimeUntilKill = GameMain.NilMod.ReconnectTimeAllowed;
                disconnectedchar.ClientSetCooldown = 0.5f;
                GameMain.NilMod.DisconnectedCharacters.Add(disconnectedchar);
            }
            else if(gameStarted && client.Character != null)
            {
                client.Character.ClearInputs();
                client.Character.Kill(CauseOfDeath.Disconnected, true);
            }

            client.Character = null;
            client.InGame = false;

            if (string.IsNullOrWhiteSpace(msg)) msg = client.Name + " has left the server";
            if (string.IsNullOrWhiteSpace(targetmsg)) targetmsg = "You have left the server";

            Log(msg, ServerLog.MessageType.ServerMessage);

            client.Connection.Disconnect(targetmsg);
            connectedClients.Remove(client);

#if CLIENT
            GameMain.NetLobbyScreen.RemovePlayer(client.Name);
            Voting.UpdateVoteTexts(connectedClients, VoteType.Sub);
            Voting.UpdateVoteTexts(connectedClients, VoteType.Mode);
            GameSession.inGameInfo.RemoveClient(client);
#endif

            UpdateVoteStatus();

            SendChatMessage(msg, ChatMessageType.Server);

            UpdateCrewFrame();

            refreshMasterTimer = DateTime.Now;
        }

        private void UpdateCrewFrame()
        {
            foreach (Client c in connectedClients)
            {
                if (c.Character == null || !c.InGame) continue;
            }
        }

        public void SendChatMessage(ChatMessage msg, Client recipient)
        {
            msg.NetStateID = recipient.ChatMsgQueue.Count > 0 ?
                (ushort)(recipient.ChatMsgQueue.Last().NetStateID + 1) :
                (ushort)(recipient.LastRecvChatMsgID + 1);

            recipient.ChatMsgQueue.Add(msg);
            recipient.LastChatMsgQueueID = msg.NetStateID;
        }

        /// <summary>
        /// Add the message to the chatbox and pass it to all clients who can receive it
        /// </summary>
        public void SendChatMessage(string message, ChatMessageType? type = null, Client senderClient = null)
        {
            Boolean issendinghelpmessage = false;
            Character senderCharacter = null;
            string senderName = "";

            Client targetClient = null;
            
            if (type==null)
            {
                string tempStr;
                string command = ChatMessage.GetChatMessageCommand(message, out tempStr);
                switch (command.ToLowerInvariant())
                {
                    case "r":
                    case "radio":
                        type = ChatMessageType.Radio;
                        break;
                    case "d":
                    case "dead":
                        type = ChatMessageType.Dead;
                        break;
                    //NilMod Help Commands
                    case "h":
                    case "help":
                        issendinghelpmessage = true;
                        type = ChatMessageType.Private;
                        NilMod.NilModHelpCommands.ReadHelpRequest(tempStr, senderClient);
                        //DebugConsole.NewMessage("Received 'Help' Request of help command: " + tempStr + " from " + (senderClient != null ? senderClient.name : "Host (You)"), Color.White);
                        command = "Impossible Name Length Over 20 Characters";
                        break;
                    default:
                        if (command != "")
                        {
                            if (command == name.ToLowerInvariant())
                            {
                                //a private message to the host
                            }
                            else
                            {
                                targetClient = connectedClients.Find(c =>
                                    command == c.Name.ToLowerInvariant() ||
                                    (c.Character != null && command == c.Character.Name.ToLowerInvariant()) || Homoglyphs.Compare(command.ToLowerInvariant(),c.Name.ToLowerInvariant()));

                                if (targetClient == null)
                                {
                                    if (senderClient != null && !issendinghelpmessage)
                                    {
                                        var chatMsg = ChatMessage.Create(
                                            "", "Player \"" + command + "\" not found!",
                                            ChatMessageType.Error, null);

                                        chatMsg.NetStateID = senderClient.ChatMsgQueue.Count > 0 ?
                                            (ushort)(senderClient.ChatMsgQueue.Last().NetStateID + 1) :
                                            (ushort)(senderClient.LastRecvChatMsgID + 1);

                                        senderClient.ChatMsgQueue.Add(chatMsg);
                                        senderClient.LastChatMsgQueueID = chatMsg.NetStateID;
                                    }
                                    else
                                    {
                                        if(!issendinghelpmessage) AddChatMessage("Player \"" + command + "\" not found!", ChatMessageType.Error);

                                    }

                                    return;
                                }
                            }
                            
                            type = ChatMessageType.Private;
                        }
                        else
                        {
                            type = ChatMessageType.Default;
                        }
                        break;
                }

                message = tempStr;
            }

            if (gameStarted)
            {
                //msg sent by the server
                if (senderClient == null)
                {
                    senderCharacter = myCharacter;
                    senderName = myCharacter == null ? name : myCharacter.Name;
                }                
                else //msg sent by a client
                {
                    senderCharacter = senderClient.Character;
                    senderName = senderCharacter == null ? senderClient.Name : senderCharacter.Name;

                    //sender doesn't have an alive character -> only ChatMessageType.Dead allowed
                    if (senderCharacter == null || senderCharacter.IsDead)
                    {
                        type = ChatMessageType.Dead;
                    }
                    else if (type == ChatMessageType.Private)
                    {
                        //sender has an alive character, sending private messages not allowed
                        return;
                    }

                }
            }
            else
            {
                //msg sent by the server
                if (senderClient == null)
                {
                    senderName = name;
                }                
                else //msg sent by a client          
                {
                    //game not started -> clients can only send normal and private chatmessages
                    if (type != ChatMessageType.Private) type = ChatMessageType.Default;
                    senderName = senderClient.Name;
                }
            }

            //check if the client is allowed to send the message
            WifiComponent senderRadio = null;
            switch (type)
            {
                case ChatMessageType.Radio:
                    if (senderCharacter == null) return;

                    //return if senderCharacter doesn't have a working radio
                    var radio = senderCharacter.Inventory.Items.FirstOrDefault(i => i != null && i.GetComponent<WifiComponent>() != null);
                    if (radio == null) return;

                    senderRadio = radio.GetComponent<WifiComponent>();
                    if (!senderRadio.CanTransmit()) return;
                    break;
                case ChatMessageType.Dead:
                    //character still alive -> not allowed
                    if (senderClient != null && senderCharacter != null && !senderCharacter.IsDead)
                    {
                        return;
                    }
                    break;
            }
            
            if (type == ChatMessageType.Server)
            {
                senderName = null;
                senderCharacter = null;
            }

            //check which clients can receive the message and apply distance effects
            foreach (Client client in ConnectedClients)
            {
                string modifiedMessage = message;

                switch (type)
                {
                    case ChatMessageType.Default:
                    case ChatMessageType.Radio:
                        if (senderCharacter != null && 
                            client.Character != null && !client.Character.IsDead)
                        {
                            modifiedMessage = ApplyChatMsgDistanceEffects(message, (ChatMessageType)type, senderCharacter, client.Character);

                            //too far to hear the msg -> don't send
                            if (string.IsNullOrWhiteSpace(modifiedMessage)) continue;
                        }
                        break;
                    case ChatMessageType.Dead:
                        //character still alive -> don't send
                        if (client.Character != null && !client.Character.IsDead) continue;
                        break;
                    case ChatMessageType.Private:
                        //private msg sent to someone else than this client -> don't send
                        if ((client != targetClient && client != senderClient) | issendinghelpmessage) continue;
                        break;
                }
                
                var chatMsg = ChatMessage.Create(
                    senderName,
                    modifiedMessage, 
                    (ChatMessageType)type,
                    senderCharacter);

                SendChatMessage(chatMsg, client);
            }

            string myReceivedMessage = message;
            if (gameStarted && myCharacter != null && senderCharacter != null)
            {
                myReceivedMessage = ApplyChatMsgDistanceEffects(message, (ChatMessageType)type, senderCharacter, myCharacter);
            }

            if (!string.IsNullOrWhiteSpace(myReceivedMessage) && 
                (targetClient == null || senderClient == null))
            {
                AddChatMessage(myReceivedMessage, (ChatMessageType)type, senderName, senderCharacter); 
            }       
        }

        private string ApplyChatMsgDistanceEffects(string message, ChatMessageType type, Character sender, Character receiver)
        {
            if (sender == null) return "";

            switch (type)
            {
                case ChatMessageType.Default:
                    if (!receiver.IsDead)
                    {
                        return ChatMessage.ApplyDistanceEffect(receiver, sender, message, ChatMessage.SpeakRange);
                    }
                    break;
                case ChatMessageType.Radio:
                    if (!receiver.IsDead)
                    {
                        var receiverItem = receiver.Inventory.Items.FirstOrDefault(i => i?.GetComponent<WifiComponent>() != null);
                        //client doesn't have a radio -> don't send
                        if (receiverItem == null) return "";

                        var senderItem = sender.Inventory.Items.FirstOrDefault(i => i?.GetComponent<WifiComponent>() != null);
                        if (senderItem == null) return "";

                        var receiverRadio   = receiverItem.GetComponent<WifiComponent>();
                        var senderRadio     = senderItem.GetComponent<WifiComponent>();

                        if (!receiverRadio.CanReceive(senderRadio)) return "";

                        return ChatMessage.ApplyDistanceEffect(receiverItem, senderItem, message, senderRadio.Range);
                    }
                    break;
            }

            return message;
        }

        private void FileTransferChanged(FileSender.FileTransferOut transfer)
        {
            Client recipient = connectedClients.Find(c => c.Connection == transfer.Connection);
#if CLIENT
            UpdateFileTransferIndicator(recipient);
#endif
        }

        public void SendCancelTransferMsg(FileSender.FileTransferOut transfer)
        {
            NetOutgoingMessage msg = server.CreateMessage();
            msg.Write((byte)ServerPacketHeader.FILE_TRANSFER);
            msg.Write((byte)FileTransferMessageType.Cancel);
            msg.Write((byte)transfer.SequenceChannel);
            server.SendMessage(msg, transfer.Connection, NetDeliveryMethod.ReliableOrdered, transfer.SequenceChannel);
        }

        public void UpdateVoteStatus()
        {
            if (server.Connections.Count == 0|| connectedClients.Count == 0) return;

            GameMain.NetworkMember.EndVoteCount = GameMain.Server.ConnectedClients.Count(c => c.Character != null && c.GetVote<bool>(VoteType.EndRound));
            GameMain.NetworkMember.EndVoteMax = GameMain.Server.ConnectedClients.Count(c => c.Character != null);

            Client.UpdateKickVotes(connectedClients);

            var clientsToKick = connectedClients.FindAll(c => c.KickVoteCount >= connectedClients.Count * KickVoteRequiredRatio);
            foreach (Client c in clientsToKick)
            {
                SendChatMessage(c.Name + " has been kicked from the server.", ChatMessageType.Server, null);
                KickClient(c, "Kicked by vote", GameMain.NilMod.VoteKickStateNameTimer, GameMain.NilMod.VoteKickDenyRejoinTimer);
            }

            GameMain.NetLobbyScreen.LastUpdateID++;
            
            SendVoteStatus(connectedClients);

            if (Voting.AllowEndVoting && EndVoteMax > 0 &&
                ((float)EndVoteCount / (float)EndVoteMax) >= EndVoteRequiredRatio)
            {
                Log("Ending round by votes (" + EndVoteCount + "/" + (EndVoteMax - EndVoteCount) + ")", ServerLog.MessageType.ServerMessage);

                //Custom Nilmod End Vote Messages for other players whom are spectating the round or playing.
                foreach (Client client in ConnectedClients)
                {
                    if (client.InGame)
                    {
                        if (NilMod.NilModEventChatter.NilVoteEnd.Count() > 0 && NilMod.NilModEventChatter.ChatVoteEnd)
                        {
                            foreach (string message in NilMod.NilModEventChatter.NilVoteEnd)
                            {
                                NilMod.NilModEventChatter.SendServerMessage(message, client);
                            }
                        }
                    }
                }

                EndGame();
            }
        }

        public void SendVoteStatus(List<Client> recipients)
        {
            NetOutgoingMessage msg = server.CreateMessage();
            msg.Write((byte)ServerPacketHeader.UPDATE_LOBBY);
            msg.Write((byte)ServerNetObject.VOTE);
            Voting.ServerWrite(msg);
            msg.Write((byte)ServerNetObject.END_OF_MESSAGE);

            server.SendMessage(msg, recipients.Select(c => c.Connection).ToList(), NetDeliveryMethod.ReliableUnordered, 0);
        }

        public void UpdateClientPermissions(Client client)
        {           
            clientPermissions.RemoveAll(cp => cp.IP == client.Connection.RemoteEndPoint.Address.ToString());

            if (client.Permissions != ClientPermissions.None)
            {
                clientPermissions.Add(new SavedClientPermission(
                    client.Name, 
                    client.Connection.RemoteEndPoint.Address.ToString(), 
                    client.Permissions));
            }

            var msg = server.CreateMessage();
            msg.Write((byte)ServerPacketHeader.PERMISSIONS);
            msg.Write((byte)client.Permissions);
            server.SendMessage(msg, client.Connection, NetDeliveryMethod.ReliableUnordered);

            SaveClientPermissions();
        }

        public void SetClientCharacter(Client client, Character newCharacter) //Line 1983
        {
            if (client == null) return;

            //the client's previous character is no longer a remote player
            if (client.Character != null)
            {
                client.Character.IsRemotePlayer = false;
            }

            if (newCharacter == null)
            {
                if (client.Character != null) //removing control of the current character
                {
                    CreateEntityEvent(client.Character, new object[] { NetEntityEvent.Type.Control, null });
                    client.Character = null;
                }

            }
            else //taking control of a new character
            {
                newCharacter.ResetNetState();
                if (client.Character != null)
                {
                    newCharacter.LastNetworkUpdateID = client.Character.LastNetworkUpdateID;
                }
                newCharacter.IsRemotePlayer = true;
                try
                {
                    newCharacter.Enabled = true;
                    client.Character = newCharacter;
                    CreateEntityEvent(newCharacter, new object[] { NetEntityEvent.Type.Control, client });
                    GameMain.Server.CreateEntityEvent(newCharacter, new object[] { NetEntityEvent.Type.Status });
                }
                catch (NullReferenceException e)
                {
                    DebugConsole.NewMessage("Critical error occured in GAMESERVER.SETCLIENTCHARACTER - Failiure to enable character to due error: " + e.Message, Color.Red);
                    DebugConsole.NewMessage("Character: " + newCharacter.Name + " Has been removed to prevent server crash (Hopefully!)", Color.Red);
                    GameMain.Server.ServerLog.WriteLine("Critical error occured in GAMESERVER.SETCLIENTCHARACTER - Failiure to enable character to due error: " + e.Message, ServerLog.MessageType.Error);
                    GameMain.Server.ServerLog.WriteLine("Character: " + newCharacter.Name + " Has been removed to prevent server crash (Hopefully!)", ServerLog.MessageType.Error);
                    newCharacter.Enabled = false;
                    Entity.Spawner.AddToRemoveQueue(newCharacter);
                }
                
            }
        }

        private void UpdateCharacterInfo(NetIncomingMessage message, Client sender)
        {
            sender.SpectateOnly = message.ReadBoolean() && AllowSpectating;
            if (sender.SpectateOnly)
            {
                return;
            }

            Gender gender = Gender.Male;
            int headSpriteId = 0;
            try
            {
                gender = message.ReadBoolean() ? Gender.Male : Gender.Female;
                headSpriteId = message.ReadByte();
            }
            catch (Exception e)
            {
                gender = Gender.Male;
                headSpriteId = 0;

                DebugConsole.Log("Received invalid characterinfo from \"" + sender.Name + "\"! { " + e.Message + " }");
            }

            List<JobPrefab> jobPreferences = new List<JobPrefab>();
            int count = message.ReadByte();
            for (int i = 0; i < Math.Min(count, 3); i++)
            {
                string jobName = message.ReadString();

                JobPrefab jobPrefab = JobPrefab.List.Find(jp => jp.Name == jobName);
                if (jobPrefab != null) jobPreferences.Add(jobPrefab);
            }

            sender.CharacterInfo = new CharacterInfo(Character.HumanConfigFile, sender.Name, gender);
            sender.CharacterInfo.HeadSpriteId = headSpriteId;
            sender.JobPreferences = jobPreferences;
        }
        
        public void AssignJobs(List<Client> unassigned, bool assignHost)
        {
            unassigned = GameMain.NilMod.RandomizeClientOrder(unassigned);



            Dictionary<JobPrefab, int> assignedClientCount = new Dictionary<JobPrefab, int>();
            foreach (JobPrefab jp in JobPrefab.List)
            {
                assignedClientCount.Add(jp, 0);
            }

            int teamID = 0;
            if (unassigned.Count > 0) teamID = unassigned[0].TeamID;

            if (assignHost)
            {
                if (characterInfo != null)
                {
                    assignedClientCount[GameMain.NetLobbyScreen.JobPreferences[0]] = 1;
                }
                else if (myCharacter?.Info?.Job != null && !myCharacter.IsDead)
                {
                    assignedClientCount[myCharacter.Info.Job.Prefab] = 1;
                }
            }
            else if (myCharacter?.Info?.Job != null && !myCharacter.IsDead && myCharacter.TeamID == teamID)
            {
                assignedClientCount[myCharacter.Info.Job.Prefab]++;
            }

            //count the clients who already have characters with an assigned job
            foreach (Client c in connectedClients)
            {
                if (c.TeamID != teamID || unassigned.Contains(c)) continue;
                if (c.Character?.Info?.Job != null && !c.Character.IsDead)
                {
                    assignedClientCount[c.Character.Info.Job.Prefab]++;
                }
            }

            //if any of the players has chosen a job that is Always Allowed, give them that job
            for (int i = unassigned.Count - 1; i >= 0; i--)
            {
                if (!unassigned[i].JobPreferences[0].AllowAlways) continue;
                unassigned[i].AssignedJob = unassigned[i].JobPreferences[0];
                unassigned.RemoveAt(i);
            }

            //go through the jobs whose MinNumber>0 (i.e. at least one crew member has to have the job)
            bool unassignedJobsFound = true;
            while (unassignedJobsFound && unassigned.Count > 0)
            {
                unassignedJobsFound = false;

                foreach (JobPrefab jobPrefab in JobPrefab.List)
                {
                    if (unassigned.Count == 0) break;
                    if (jobPrefab.MinNumber < 1 || assignedClientCount[jobPrefab] >= jobPrefab.MinNumber) continue;

                    //find the client that wants the job the most, or force it to random client if none of them want it
                    Client assignedClient = FindClientWithJobPreference(unassigned, jobPrefab, true);

                    assignedClient.AssignedJob = jobPrefab;
                    assignedClientCount[jobPrefab]++;
                    unassigned.Remove(assignedClient);

                    //the job still needs more crew members, set unassignedJobsFound to true to keep the while loop running
                    if (assignedClientCount[jobPrefab] < jobPrefab.MinNumber) unassignedJobsFound = true;
                }
            }

            //NilMod reqnumber if There is a required player count get all clients who do not need round sync
            int playercount = connectedClients.FindAll(c => c.NeedsMidRoundSync != true).Count;

            //Add to the player count if the host is also playing as his own character
            if (assignHost && teamID == 1)
            {
                playercount += 1;
            }

            //find a suitable job for the rest of the players
            foreach (Client c in unassigned)
            {
                foreach (JobPrefab preferredJob in c.JobPreferences)
                {


                    //the maximum number of players that can have this job hasn't been reached yet
                    //And add in the required players check too
                    //For each player who gets the job, deduct from the requirement check
                    //So there can only be an instance of this job for each 1 at and above requirement.
                    // -> assign it to the client
                    if (assignedClientCount[preferredJob] < preferredJob.MaxNumber && playercount - assignedClientCount[preferredJob] >= preferredJob.ReqNumber)
                    {
                        c.AssignedJob = preferredJob;
                        assignedClientCount[preferredJob]++;
                        break;
                    }
                    //none of the jobs the client prefers are available anymore
                    else if (preferredJob == c.JobPreferences.Last())
                    {
                        //find all jobs that are still available
                        //var remainingJobs = JobPrefab.List.FindAll(jp => (assignedClientCount[preferredJob] <= jp.MaxNumber) && (playercount - assignedClientCount[preferredJob] >= jp.ReqNumber));

                        //find all jobs that are still available - TEST FIX
                        var remainingJobs = JobPrefab.List.FindAll(jp => (assignedClientCount[jp] < jp.MaxNumber) && (playercount - assignedClientCount[jp] >= jp.ReqNumber));


                        //all jobs taken, give a random job
                        if (remainingJobs.Count == 0)
                        {
                            DebugConsole.ThrowError("Failed to assign a suitable job for \"" + c.Name + "\" (all jobs already have the maximum numbers of players). Assigning a random job...");
                            c.AssignedJob = JobPrefab.List[Rand.Range(0, JobPrefab.List.Count)];
                            assignedClientCount[c.AssignedJob]++;
                        }
                        else //some jobs still left, choose one of them by random
                        {
                            c.AssignedJob = remainingJobs[Rand.Range(0, remainingJobs.Count)];
                            assignedClientCount[c.AssignedJob]++;
                        }
                    }
                }
            }
        }

        private Client FindClientWithJobPreference(List<Client> clients, JobPrefab job, bool forceAssign = false)
        {
            int bestPreference = 0;
            Client preferredClient = null;
            foreach (Client c in clients)
            {
                int index = c.JobPreferences.IndexOf(job);
                if (index == -1) index = 1000;

                if (preferredClient == null || index < bestPreference)
                {
                    bestPreference = index;
                    preferredClient = c;
                }
            }

            //none of the clients wants the job, assign it to random client
            if (forceAssign && preferredClient == null)
            {
                preferredClient = clients[Rand.Int(clients.Count)];
            }

            return preferredClient;
        }

        public static void Log(string line, ServerLog.MessageType messageType)
        {
            if (GameMain.Server == null || !GameMain.Server.SaveServerLogs) return;

            GameMain.Server.log.WriteLine(line, messageType);
        }

        public override void Disconnect()
        {
            banList.Save();
            SaveSettings();

            if (registeredToMaster && restClient != null)
            {
                var request = new RestRequest("masterserver2.php", Method.GET);
                request.AddParameter("action", "removeserver");
                request.AddParameter("serverport", Port);
                
                restClient.Execute(request);
                restClient = null;
            }

            if (SaveServerLogs)
            {
                Log("Shutting down the server...", ServerLog.MessageType.ServerMessage);
                log.Save();
            }
                        
            server.Shutdown("The server has been shut down");
        }

        //NilMod
        public void GrantPower(int submarine)
        {
            foreach (Item item in Item.ItemList)
            {
                if (item.Submarine == Submarine.MainSubs[submarine])
                {
                    var powerContainer = item.GetComponent<PowerContainer>();
                    if (powerContainer != null)
                    {
                        powerContainer.Charge = powerContainer.Capacity;
                        item.CreateServerEvent(powerContainer);
                    }
                }
            }
        }

        //NilMod
        public void MoveSub(int sub, Vector2 Position)
        {
            //Submarine.MainSubs[sub].PhysicsBody.FarseerBody.IgnoreCollisionWith(Level.Loaded.ShaftBody);

            Steering movedsubSteering = null;

            //Deactivate all autopilot related tasks
            foreach (Item item in Item.ItemList)
            {
                //Ensure any item checked to be steering is only the submarine were teleporting
                if (item.Submarine != Submarine.MainSubs[sub]) continue;

                //Find, temp field and then set the steering if not null - This may not work well on subs with 2 bridges
                var steering = item.GetComponent<Steering>();
                if (steering != null)
                {
                    movedsubSteering = steering;
                    movedsubSteering.AutoPilot = false;
                    movedsubSteering.MaintainPos = false;
                }
            }

            //Teleport the submarine and prevent any collission or other issues, remove speed, etc
            Submarine.MainSubs[sub].SetPosition(Position);
            Submarine.MainSubs[sub].Velocity = Vector2.Zero;
            //Submarine.MainSubs[sub].PhysicsBody.FarseerBody.RestoreCollisionWith(Level.Loaded.ShaftBody);

            //activate Maintain position on all controllers.
            foreach (Item item in Item.ItemList)
            {
                //Ensure any item checked to be steering is only the submarine were teleporting
                if (item.Submarine != Submarine.MainSubs[sub]) continue;

                //Find, temp field and then set the steering if not null - This may not work well on subs with 2 bridges
                var steering = item.GetComponent<Steering>();
                if (steering != null)
                {
                    //Apparently autopilot should be turned on after maintain to enable it correctly.
                    movedsubSteering = steering;
                    movedsubSteering.MaintainPos = true;
                    movedsubSteering.AutoPilot = true;
                }
            }
        }

        public void RemoveCorpses(Boolean RemoveNetPlayers)
        {
            for (int i = Character.CharacterList.Count() - 1; i >= 0;i--)
            {
                if(Character.CharacterList[i].IsDead)
                {
                    if (RemoveNetPlayers)
                    {
                        if (GameMain.NilMod.convertinghusklist.Find(ch => ch.character == Character.CharacterList[i]) != null) continue;
                        GameMain.NilMod.HideCharacter(Character.CharacterList[i]);
                    }
                    else if(!Character.CharacterList[i].IsRemotePlayer)
                    {
                        if (GameMain.NilMod.convertinghusklist.Find(ch => ch.character == Character.CharacterList[i]) != null) continue;
                        GameMain.NilMod.HideCharacter(Character.CharacterList[i]);
                    }
                }
            }
        }

        //NilMod Networking
        private void ClientWriteIngamenew(Client c)
        {
            GameMain.NilMod.characterstoupdate = new List<Character>();
            GameMain.NilMod.subtoupdate = new List<Submarine>();
            GameMain.NilMod.itemtoupdate = new List<Item>();
            GameMain.NilMod.PacketNumber = 0;

            if (!c.NeedsMidRoundSync)
            {
                foreach (Character character in Character.CharacterList)
                {
                    if (!character.Enabled) continue;

                    if (c.Character != null &&
                        (Vector2.DistanceSquared(character.WorldPosition, c.Character.WorldPosition) >=
                        NetConfig.CharacterIgnoreDistanceSqr) && (!character.IsRemotePlayer && !c.Character.IsDead))
                    {
                        continue;
                    }

                    GameMain.NilMod.characterstoupdate.Add(character);
                }

                foreach (Submarine sub in Submarine.Loaded)
                {
                    //if docked to a sub with a smaller ID, don't send an update
                    //  (= update is only sent for the docked sub that has the smallest ID, doesn't matter if it's the main sub or a shuttle)
                    if (sub.DockedTo.Any(s => s.ID < sub.ID)) continue;

                    GameMain.NilMod.subtoupdate.Add(sub);
                }

                foreach (Item item in Item.ItemList)
                {
                    if (!item.NeedsPositionUpdate) continue;

                    GameMain.NilMod.itemtoupdate.Add(item);
                }
            }

            //Always send one packet
            SendClientPacket(c);

            //As long as we have items left for those clients SPAM MOAR PACKETS >: O ...or if no items actually send the usual first packet.
            while ((GameMain.NilMod.characterstoupdate.Count > 0 | GameMain.NilMod.subtoupdate.Count > 0 | GameMain.NilMod.itemtoupdate.Count > 0))
            {
                SendClientPacket(c);
            }

            //DebugConsole.NewMessage("Sent packets: " + GameMain.NilMod.PacketNumber, Color.White);
        }

        //NilMod
        private void SendClientPacket(Client c)
        {
            NetOutgoingMessage outmsg = server.CreateMessage();
            outmsg.Write((byte)ServerPacketHeader.UPDATE_INGAME);

            //outmsg.Write((float)NetTime.Now + (GameMain.NilMod.PacketNumber * 0.0001));
            outmsg.Write((float)NetTime.Now);

            if (GameMain.NilMod.PacketNumber == 0)
            {
                outmsg.Write((byte)ServerNetObject.SYNC_IDS);
                outmsg.Write(c.LastSentChatMsgID); //send this to client so they know which chat messages weren't received by the server
                outmsg.Write(c.LastSentEntityEventID);

                entityEventManager.Write(c, outmsg);

                WriteChatMessages(outmsg, c);
            }
            GameMain.NilMod.PacketNumber++;

            //don't send position updates to characters who are still midround syncing
            //characters or items spawned mid-round don't necessarily exist at the client's end yet
            if (!c.NeedsMidRoundSync)
            {
                for (int i = GameMain.NilMod.characterstoupdate.Count - 1; i >= 0; i--)
                {
                    if (outmsg.LengthBytes >= NetPeerConfiguration.kDefaultMTU - 10) continue;
                    outmsg.Write((byte)ServerNetObject.ENTITY_POSITION);
                    GameMain.NilMod.characterstoupdate[i].ServerWrite(outmsg, c);
                    outmsg.WritePadBits();

                    GameMain.NilMod.characterstoupdate.RemoveAt(i);
                }

                for (int i = GameMain.NilMod.subtoupdate.Count - 1; i >= 0; i--)
                {
                    if (outmsg.LengthBytes >= NetPeerConfiguration.kDefaultMTU - 10) continue;
                    outmsg.Write((byte)ServerNetObject.ENTITY_POSITION);
                    GameMain.NilMod.subtoupdate[i].ServerWrite(outmsg, c);
                    outmsg.WritePadBits();

                    GameMain.NilMod.subtoupdate.RemoveAt(i);
                }

                for (int i = GameMain.NilMod.itemtoupdate.Count - 1; i >= 0; i--)
                {
                    if (outmsg.LengthBytes >= NetPeerConfiguration.kDefaultMTU - 10) continue;
                    outmsg.Write((byte)ServerNetObject.ENTITY_POSITION);
                    GameMain.NilMod.itemtoupdate[i].ServerWritePosition(outmsg, c);
                    outmsg.WritePadBits();

                    GameMain.NilMod.itemtoupdate.RemoveAt(i);
                }
            }

            outmsg.Write((byte)ServerNetObject.END_OF_MESSAGE);

            //DebugConsole.NewMessage("Sending packet: " + GameMain.NilMod.PacketNumber + " with MTU Size: " + outmsg.LengthBytes, Color.White);

            server.SendMessage(outmsg, c.Connection, NetDeliveryMethod.Unreliable);
        }


#if CLIENT
        //NilMod GUI Menu Click Commands
        private void ClickCommandUpdate(float DeltaTime)
        {
            GameMain.NilMod.ClickCooldown -= DeltaTime;
            if (GameMain.NilMod.ClickCooldown <= 0f)
            {
                switch (GameMain.NilMod.ClickCommandType)
                {
                    case "spawncreature":
                        ClickCommandFrame.Visible = true;
                        ClickCommandDescription.Text = "SPAWNCREATURE - Spawning: " + GameMain.NilMod.ClickArgs[0].ToLowerInvariant() + " countleft: " + GameMain.NilMod.ClickArgs[2] + " - Left click to spawn creatures, Right click to cancel.";
                        if (PlayerInput.LeftButtonClicked())
                        {
                            if (Convert.ToInt16(GameMain.NilMod.ClickArgs[2]) > 0)
                            {
                                GameMain.NilMod.ClickArgs[2] = Convert.ToString(Convert.ToInt16(GameMain.NilMod.ClickArgs[2]) - 1);
                                GameMain.NilMod.ClickCooldown = NilMod.ClickCooldownPeriod;

                                GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType, new object[] { GameMain.NilMod.ClickArgs[0],
                                    GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition).X.ToString(),
                                    GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition).Y.ToString(),
                                    GameMain.NilMod.ClickArgs[2]});
                            }
                            if (Convert.ToInt16(GameMain.NilMod.ClickArgs[2]) == 0)
                            {
                                ClearClickCommand();
                            }
                        }
                        break;
                    case "heal":
                        ClickCommandFrame.Visible = true;
                        ClickCommandDescription.Text = "HEAL - Left Click close to a creatures center to heal it, Hold shift while clicking to repeat, Hold ctrl when clicking to heal self, right click to cancel.";
                        if (PlayerInput.LeftButtonClicked())
                        {
                            Character closestDistChar = null;
                            float closestDist = GameMain.NilMod.ClickFindSelectionDistance;
                            foreach (Character c in Character.CharacterList)
                            {
                                if (!c.IsDead)
                                {
                                    float dist = Vector2.Distance(c.WorldPosition, GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition));

                                    if (dist < closestDist)
                                    {
                                        closestDist = dist;
                                        closestDistChar = c;
                                    }
                                }
                            }
                            if (PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftControl) && Character.Controlled != null && !PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift))
                            {
                                GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType, new object[] { Character.Controlled });

                                //Character.Controlled.Heal();
                                ClearClickCommand();
                            }
                            else if (PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftControl) && Character.Controlled != null && PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift))
                            {
                                GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType, new object[] { Character.Controlled });

                                //Character.Controlled.Heal();
                            }
                            else if (closestDistChar != null)
                            {
                                GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType, new object[] { closestDistChar });

                                //closestDistChar.Heal();
                                if (!PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift))
                                {
                                    ClearClickCommand();
                                }
                                else
                                {
                                    GameMain.NilMod.ClickCooldown = NilMod.ClickCooldownPeriod;
                                }
                            }
                        }
                        break;
                    case "revive":
                        ClickCommandFrame.Visible = true;
                        ClickCommandDescription.Text = "REVIVE - Left Click close to a creatures center to revive it, Hold shift while clicking to repeat, Hold ctrl when clicking the button to revive self, if detached from body ctrl click corpse to revive+control, right click to cancel. - As a note for now IF REVIVING A PLAYER you will wish to open the console (F3) and type setclientcharacter CapitalizedClientName ; clientcharacter to give them the body back.";
                        if (PlayerInput.LeftButtonClicked())
                        {
                            Character closestDistChar = null;
                            float closestDist = GameMain.NilMod.ClickFindSelectionDistance;
                            foreach (Character c in Character.CharacterList)
                            {
                                if (c.IsDead)
                                {
                                    float dist = Vector2.Distance(c.WorldPosition, GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition));

                                    if (dist < closestDist)
                                    {
                                        closestDist = dist;
                                        closestDistChar = c;
                                    }
                                }
                            }
                            if (closestDistChar != null)
                            {
                                if (!PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift))
                                {
                                    if (GameMain.Server.ConnectedClients.Find(c => c.Name == closestDistChar.Name) != null)
                                    {
                                        if (GameMain.Server != null)
                                        {
                                            Client MatchedClient = null;
                                            foreach (Client c in GameMain.Server.ConnectedClients)
                                            {
                                                //Don't even consider reviving a client if it is not ingame yet.
                                                if (!c.InGame || c.NeedsMidRoundSync) continue;

                                                //Check if this client just happens to be the same character.
                                                if(c.Character == closestDistChar)
                                                {
                                                    //It matched.
                                                    MatchedClient = c;
                                                }
                                                //Check if the client has a character
                                                else if (c.Character != null)
                                                {
                                                    //Check if this is the same named client, and if so, skip if they have a living character.
                                                    if (c.Name != closestDistChar.Name || c.Name == closestDistChar.Name && !c.Character.IsDead) continue;
                                                    //This name matches that of their client name.
                                                    MatchedClient = c;
                                                }
                                                //This client has no character, simply check the name
                                                else
                                                {
                                                    if (c.Name != closestDistChar.Name) continue;

                                                    MatchedClient = c;
                                                }

                                                if (MatchedClient != null)
                                                {
                                                    //They do not have a living character but they are the correct client - allow it.
                                                    GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType, new object[] { closestDistChar });
                                                    //closestDistChar.Revive(true);

                                                    //clients stop controlling the character when it dies, force control back
                                                    //GameMain.Server.SetClientCharacter(c, closestDistChar);
                                                    GameMain.GameScreen.RunIngameCommand("setclientcharacter", new object[] { c, closestDistChar });
                                                }
                                                break;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        closestDistChar.Revive(true);
                                    }
                                    
                                    ClearClickCommand();
                                }
                                else if (PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftControl) && !closestDistChar.IsRemotePlayer)
                                {
                                    Character.Controlled = closestDistChar;
                                    GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType, new object[] { Character.Controlled });
                                    GameMain.GameScreen.RunIngameCommand("heal", new object[] { Character.Controlled });
                                    //Character.Controlled.Revive(true);
                                    //Character.Controlled.Heal();
                                    ClearClickCommand();
                                }
                                else
                                {
                                    if (GameMain.Server.ConnectedClients.Find(c => c.Name == closestDistChar.Name) != null)
                                    {
                                        if (GameMain.Server != null)
                                        {
                                            Client MatchedClient = null;
                                            foreach (Client c in GameMain.Server.ConnectedClients)
                                            {
                                                //Don't even consider reviving a client if it is not ingame yet.
                                                if (!c.InGame || c.NeedsMidRoundSync) continue;

                                                //Check if this client just happens to be the same character.
                                                if (c.Character == closestDistChar)
                                                {
                                                    //It matched.
                                                    MatchedClient = c;
                                                }
                                                //Check if the client has a character
                                                else if (c.Character != null)
                                                {
                                                    //Check if this is the same named client, and if so, skip if they have a living character.
                                                    if (c.Name != closestDistChar.Name || c.Name == closestDistChar.Name && !c.Character.IsDead) continue;
                                                    //This name matches that of their client name.
                                                    MatchedClient = c;
                                                }
                                                //This client has no character, simply check the name
                                                else
                                                {
                                                    if (c.Name != closestDistChar.Name) continue;

                                                    MatchedClient = c;
                                                }

                                                if (MatchedClient != null)
                                                {
                                                    //They do not have a living character but they are the correct client - allow it.
                                                    GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType, new object[] { closestDistChar });
                                                    //closestDistChar.Revive(true);

                                                    //clients stop controlling the character when it dies, force control back
                                                    //GameMain.Server.SetClientCharacter(c, closestDistChar);
                                                    GameMain.GameScreen.RunIngameCommand("setclientcharacter", new object[] { c, closestDistChar });
                                                }
                                                break;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType, new object[] { closestDistChar });
                                        //closestDistChar.Revive(true);
                                    }
                                    GameMain.NilMod.ClickCooldown = NilMod.ClickCooldownPeriod;
                                }
                            }
                        }
                        break;
                    case "kill":
                        ClickCommandFrame.Visible = true;
                        ClickCommandDescription.Text = "KILL CREATURE - Left Click close to a creatures center to instantaniously kill it, Hold shift while clicking to repeat, right click to cancel.";
                        if (PlayerInput.LeftButtonClicked())
                        {
                            Character closestDistChar = null;
                            float closestDist = GameMain.NilMod.ClickFindSelectionDistance;
                            foreach (Character c in Character.CharacterList)
                            {
                                if (!c.IsDead)
                                {
                                    float dist = Vector2.Distance(c.WorldPosition, GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition));

                                    if (dist < closestDist)
                                    {
                                        closestDist = dist;
                                        closestDistChar = c;
                                    }
                                }
                            }
                            if (closestDistChar != null)
                            {
                                closestDistChar.Kill(CauseOfDeath.Disconnected, true);
                                if (!PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift))
                                {
                                    ClearClickCommand();
                                }
                                else
                                {
                                    GameMain.NilMod.ClickCooldown = NilMod.ClickCooldownPeriod;
                                }
                            }
                        }
                        break;
                    case "removecorpse":
                        ClickCommandFrame.Visible = true;
                        ClickCommandDescription.Text = "REMOVECORPSE - Left Click close to a creatures corpse to delete it, Hold shift while clicking to repeat, right click to cancel.";
                        if (PlayerInput.LeftButtonClicked())
                        {
                            Character closestDistChar = null;
                            float closestDist = GameMain.NilMod.ClickFindSelectionDistance;
                            foreach (Character c in Character.CharacterList)
                            {
                                if (GameMain.NilMod.convertinghusklist.Find(ch => ch.character == c) != null) continue;

                                if (c.IsDead)
                                {
                                    float dist = Vector2.Distance(c.WorldPosition, GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition));

                                    if (dist < closestDist)
                                    {
                                        closestDist = dist;
                                        closestDistChar = c;
                                    }
                                }
                            }
                            if (closestDistChar != null)
                            {
                                //GameMain.NilMod.HideCharacter(closestDistChar);

                                GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType, new object[] { closestDistChar });
                                if (!PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift))
                                {
                                    ClearClickCommand();
                                }
                                else
                                {
                                    GameMain.NilMod.ClickCooldown = NilMod.ClickCooldownPeriod;
                                }
                            }
                        }
                        break;
                    case "teleportsub":
                        ClickCommandFrame.Visible = true;
                        ClickCommandDescription.Text = "TELEPORTSUB - Team " + GameMain.NilMod.ClickArgs[0] + "'s submarine - Teleports the chosen teams submarine, left click to teleport. Right click to cancel.";
                        if (PlayerInput.LeftButtonClicked())
                        {
                            int subtotp = -1;
                            if (Convert.ToInt16(GameMain.NilMod.ClickArgs[0]) <= Submarine.MainSubs.Count() - 1)
                            {
                                subtotp = Convert.ToInt16(GameMain.NilMod.ClickArgs[0]);
                            }
                            else
                            {
                                DebugConsole.NewMessage("MainSub ID Range is from 0 to " + (Submarine.MainSubs.Count() - 1), Color.Red);
                            }

                            //Not Null? Lets try it! XD
                            if (GameMain.Server != null)
                            {
                                if (subtotp >= 0)
                                {
                                    if (Submarine.MainSubs[subtotp] != null)
                                    {
                                        var cam = GameMain.GameScreen.Cam;
                                        //GameMain.Server.MoveSub(subtotp, cam.ScreenToWorld(PlayerInput.MousePosition));
                                        GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType,
                                            new object[] { subtotp.ToString(),
                                                cam.ScreenToWorld(PlayerInput.MousePosition).X.ToString(),
                                                cam.ScreenToWorld(PlayerInput.MousePosition).Y.ToString() });
                                    }
                                    else
                                    {
                                        DebugConsole.NewMessage("Cannot teleport submarine - Submarine ID: " + subtotp + " Is not loaded in the game (If not multiple submarines use 0 or leave blank)", Color.Red);
                                    }
                                }
                            }
                            else
                            {
                                DebugConsole.NewMessage("Cannot teleport submarine - The Server is not running.", Color.Red);
                            }
                            if (!PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift))
                            {
                                ClearClickCommand();
                            }
                            else
                            {
                                GameMain.NilMod.ClickCooldown = NilMod.ClickCooldownPeriod;
                            }
                        }
                        break;
                    case "relocate":
                        ClickCommandFrame.Visible = true;
                        if (GameMain.NilMod.RelocateTarget != null)
                        {
                            ClickCommandDescription.Text = "RELOCATE - " + GameMain.NilMod.RelocateTarget + " - Left Click to select target to teleport, Left click again to teleport target to new destination, hold shift to repeat (Does not keep last target), Ctrl+Left Click to relocate self, Ctrl+Shift works, Right click to cancel.";
                        }
                        else
                        {
                            ClickCommandDescription.Text = "RELOCATE - None Selected - Left Click to select target to teleport, Left click again to teleport target to new destination, hold shift to repeat (Does not keep last target), Ctrl+Left Click to relocate self, Ctrl+Shift works, Right click to cancel.";
                        }
                        if (PlayerInput.LeftButtonClicked())
                        {
                            if (PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftControl))
                            {
                                if (Character.Controlled != null)
                                {
                                    GameMain.NilMod.RelocateTarget = null;

                                    //Character.Controlled.AnimController.CurrentHull = null;
                                    //Character.Controlled.Submarine = null;
                                    //Character.Controlled.AnimController.SetPosition(FarseerPhysics.ConvertUnits.ToSimUnits(GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition)));
                                    //Character.Controlled.AnimController.FindHull(GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition), true);

                                    GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType, new object[] { Character.Controlled, GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition).X, GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition).Y });

                                    if (!PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift))
                                    {
                                        ClearClickCommand();
                                    }
                                    else
                                    {
                                        GameMain.NilMod.ClickCooldown = NilMod.ClickCooldownPeriod;
                                    }
                                }
                            }
                            else if (GameMain.NilMod.RelocateTarget == null)
                            {
                                Character closestDistChar = null;
                                float closestDist = GameMain.NilMod.ClickFindSelectionDistance;
                                foreach (Character c in Character.CharacterList)
                                {
                                    float dist = Vector2.Distance(c.WorldPosition, GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition));

                                    if (dist < closestDist)
                                    {
                                        closestDist = dist;
                                        closestDistChar = c;
                                    }
                                }
                                GameMain.NilMod.RelocateTarget = closestDistChar;
                            }
                            else
                            {
                                //GameMain.NilMod.RelocateTarget.AnimController.CurrentHull = null;
                                //GameMain.NilMod.RelocateTarget.Submarine = null;
                                //GameMain.NilMod.RelocateTarget.AnimController.SetPosition(FarseerPhysics.ConvertUnits.ToSimUnits(GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition)));
                                //GameMain.NilMod.RelocateTarget.AnimController.FindHull(GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition), true);

                                GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType,
                                    new object[] { GameMain.NilMod.RelocateTarget,
                                    GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition).X,
                                    GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition).Y });

                                GameMain.NilMod.RelocateTarget = null;

                                if (!PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift))
                                {
                                    ClearClickCommand();
                                }
                                else
                                {
                                    GameMain.NilMod.ClickCooldown = NilMod.ClickCooldownPeriod;
                                }
                                
                            }

                        }
                        break;
                        /*
                    case "handcuff":
                        ClickCommandFrame.Visible = true;
                        ClickCommandDescription.Text = "HANDCUFF - Left click to spawn and add handcuffs to the players hands dropping their tools - Right click to drop handcuffs if present in hands - shift to repeat - ctrl click to delete handcuffs from hands - right click to cancel.";
                        break;
                        */
                    case "freeze":
                        ClickCommandFrame.Visible = true;
                        ClickCommandDescription.Text = "FREEZE - Left click a player to freeze their movements - Left click again to unfreeze - hold only shift to repeat - hold ctrl shift and left click to freeze all - hold ctrl and left click to unfreeze everyone - Right click to cancel - Players may still talk if concious.";
                        if (PlayerInput.LeftButtonClicked())
                        {
                            Character closestDistChar = null;
                            float closestDist = GameMain.NilMod.ClickFindSelectionDistance;
                            foreach (Character c in Character.CharacterList)
                            {
                                if (!c.IsDead)
                                {
                                    float dist = Vector2.Distance(c.WorldPosition, GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition));

                                    if (dist < closestDist)
                                    {
                                        closestDist = dist;
                                        closestDistChar = c;
                                    }
                                }
                            }
                            //Standard Left click
                            if (closestDistChar != null && !(PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftControl) && PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift)))
                            {
                                GameMain.GameScreen.RunIngameCommand(GameMain.NilMod.ClickCommandType, new object[] { closestDistChar });

                                if (!PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift))
                                {
                                    ClearClickCommand();
                                }
                                else
                                {
                                    GameMain.NilMod.ClickCooldown = NilMod.ClickCooldownPeriod;
                                }
                            }
                            //hold ctrl and left click to unfreeze everyone
                            else if ((PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftControl) && !PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift)))
                            {
                                for (int i = GameMain.NilMod.FrozenCharacters.Count() - 1; i >= 0; i--)
                                {
                                    GameMain.NilMod.FrozenCharacters.RemoveAt(i);
                                }
                            }
                            //hold ctrl shift and left click to freeze all
                            else if ((PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftControl) && PlayerInput.KeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift)))
                            {
                                foreach (Character character in Character.CharacterList)
                                {
                                    if (GameMain.NilMod.FrozenCharacters.Find(c => c == character) == null)
                                    {
                                        if(character.IsRemotePlayer)
                                        {
                                            if (ConnectedClients.Find(c => c.Character == closestDistChar) != null)
                                            {
                                                var chatMsg = ChatMessage.Create(
                                                "Server Message",
                                                ("You have been frozen by the server\n\nYou may still talk if able, but no longer perform any actions or movements."),
                                                (ChatMessageType)ChatMessageType.MessageBox,
                                                null);

                                                GameMain.Server.SendChatMessage(chatMsg, ConnectedClients.Find(c => c.Character == closestDistChar));
                                            }
                                        }
                                        GameMain.NilMod.FrozenCharacters.Add(character);
                                    }
                                }
                            }
                        }
                        break;
                    case "":
                    default:
                        break;
                }
            }
            //Nullify the active command if rightclicking
            if (PlayerInput.RightButtonClicked())
            {
                ClearClickCommand();
            }
        }

        public void ClearClickCommand()
        {
            GameMain.NilMod.ClickCommandType = "";
            GameMain.NilMod.ClickArgs = null;
            GameMain.NilMod.ActiveClickCommand = false;
            GameMain.NilMod.ClickCooldown = 0.5f;
            GameMain.NilMod.RelocateTarget = null;
            ClickCommandFrame.Visible = false;
            ClickCommandDescription.Text = "";
        }
#endif
    }
}
