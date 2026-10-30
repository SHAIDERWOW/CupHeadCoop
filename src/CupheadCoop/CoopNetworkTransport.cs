using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Steamworks;

namespace CupheadCoop
{
    public class CoopNetworkTransport
    {
        private const int SteamChannelUnreliable = 0;
        private const int SteamChannelReliable = 1;
        private const byte UdpFlagReliable = 0x80;
        private const byte UdpFlagUnreliable = 0x00;

        public CoopRole Role { get; private set; }
        public bool IsConnected { get; private set; }
        public bool UsingSteamP2P { get; private set; }
        public CSteamID CurrentLobbyId { get; private set; }
        public CSteamID RemoteSteamId { get; private set; }
        public IPEndPoint RemoteUdpEndPoint { get; private set; }
        public float LastRttMs { get; private set; }
        public string StatusMessage { get; private set; }

        public event Action<byte[]> OnPacketReceived;
        public event Action OnPeerConnected;
        public event Action OnPeerDisconnected;

        private UdpClient _udpClient;
        private uint _nextReliableSeq = 1;
        private readonly HashSet<uint> _receivedReliableSeqs = new HashSet<uint>();
        private readonly Dictionary<uint, PendingReliablePacket> _pendingReliables = new Dictionary<uint, PendingReliablePacket>();

        // Steamworks callbacks (kept alive as fields so GC never collects them)
        private CallResult<LobbyCreated_t> _lobbyCreatedCallResult;
        private Callback<LobbyEnter_t> _lobbyEnterCallback;
        private Callback<LobbyChatUpdate_t> _lobbyChatUpdateCallback;
        private Callback<GameLobbyJoinRequested_t> _gameLobbyJoinRequestedCallback;
        private Callback<P2PSessionRequest_t> _p2pSessionRequestCallback;
        private Callback<P2PSessionConnectFail_t> _p2pConnectFailCallback;
        private bool _steamCallbacksRegistered;
        private static System.Reflection.FieldInfo _steamInstanceField;
        private static System.Reflection.FieldInfo _steamInitField;

        public static bool IsSteamInitialized()
        {
            try
            {
                if (_steamInstanceField == null || _steamInitField == null)
                {
                    Type smType = typeof(OnlineManager).Assembly.GetType("SteamManager");
                    if (smType != null)
                    {
                        _steamInstanceField = smType.GetField("s_instance",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                        _steamInitField = smType.GetField("m_bInitialized",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    }
                }
                if (_steamInstanceField != null && _steamInitField != null)
                {
                    object instance = _steamInstanceField.GetValue(null);
                    if (instance != null)
                    {
                        return (bool)_steamInitField.GetValue(instance);
                    }
                }
            }
            catch { }
            return false;
        }

        private class PendingReliablePacket
        {
            public uint Sequence;
            public byte[] FramedBytes;
            public DateTime LastSentUtc;
            public int Attempts;
        }

        public CoopNetworkTransport()
        {
            Role = CoopRole.None;
            StatusMessage = "Idle (Not Connected)";
        }

        public void EnsureSteamCallbacks()
        {
            if (_steamCallbacksRegistered || !IsSteamInitialized())
                return;

            try
            {
                _lobbyCreatedCallResult = CallResult<LobbyCreated_t>.Create(OnSteamLobbyCreated);
                _lobbyEnterCallback = Callback<LobbyEnter_t>.Create(OnSteamLobbyEntered);
                _lobbyChatUpdateCallback = Callback<LobbyChatUpdate_t>.Create(OnSteamLobbyChatUpdate);
                _gameLobbyJoinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(OnSteamGameLobbyJoinRequested);
                _p2pSessionRequestCallback = Callback<P2PSessionRequest_t>.Create(OnSteamP2PSessionRequest);
                _p2pConnectFailCallback = Callback<P2PSessionConnectFail_t>.Create(OnSteamP2PConnectFail);
                SteamNetworking.AllowP2PPacketRelay(true);
                _steamCallbacksRegistered = true;
                CupheadCoopBootstrapper.Log("Steamworks P2P & Lobby callbacks registered.");
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("EnsureSteamCallbacks error: " + ex);
            }
        }

        public void HostSteamLobby()
        {
            EnsureSteamCallbacks();
            if (!IsSteamInitialized())
            {
                StatusMessage = "Steam not initialized! Use Direct UDP or launch via Steam.";
                return;
            }

            Disconnect();
            Role = CoopRole.Host;
            UsingSteamP2P = true;
            StatusMessage = "Creating Steam Friend Lobby...";
            SteamAPICall_t handle = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2);
            _lobbyCreatedCallResult.Set(handle, OnSteamLobbyCreated);
        }

        public void InviteSteamFriend()
        {
            if (!IsSteamInitialized() || !CurrentLobbyId.IsValid())
                return;

            SteamFriends.ActivateGameOverlayInviteDialog(CurrentLobbyId);
        }

        public void JoinSteamLobby(ulong lobbyIdULong)
        {
            EnsureSteamCallbacks();
            if (!IsSteamInitialized())
                return;

            Disconnect();
            Role = CoopRole.Client;
            UsingSteamP2P = true;
            CSteamID lobbyId = new CSteamID(lobbyIdULong);
            StatusMessage = "Joining Steam Lobby " + lobbyIdULong + "...";
            SteamMatchmaking.JoinLobby(lobbyId);
        }

        public void HostUdp(int port)
        {
            Disconnect();
            try
            {
                _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                _udpClient.Client.Blocking = false;
                Role = CoopRole.Host;
                UsingSteamP2P = false;
                IsConnected = false;
                StatusMessage = "Hosting UDP on port " + port + " (waiting for client)...";
                CupheadCoopBootstrapper.Log(StatusMessage);
            }
            catch (Exception ex)
            {
                StatusMessage = "UDP Host error: " + ex.Message;
                CupheadCoopBootstrapper.Log("HostUdp error: " + ex);
            }
        }

        public void JoinUdp(string hostIp, int port)
        {
            Disconnect();
            try
            {
                _udpClient = new UdpClient(0);
                _udpClient.Client.Blocking = false;
                RemoteUdpEndPoint = new IPEndPoint(IPAddress.Parse(hostIp.Trim()), port);
                Role = CoopRole.Client;
                UsingSteamP2P = false;
                IsConnected = true;
                StatusMessage = "Connected (UDP Client -> " + RemoteUdpEndPoint + ")";
                CupheadCoopBootstrapper.Log(StatusMessage);
                if (OnPeerConnected != null)
                    OnPeerConnected();
            }
            catch (Exception ex)
            {
                StatusMessage = "UDP Join error: " + ex.Message;
                CupheadCoopBootstrapper.Log("JoinUdp error: " + ex);
            }
        }

        public void Disconnect()
        {
            bool wasConnected = IsConnected;
            IsConnected = false;
            Role = CoopRole.None;

            if (UsingSteamP2P && IsSteamInitialized())
            {
                try
                {
                    if (RemoteSteamId.IsValid())
                        SteamNetworking.CloseP2PSessionWithUser(RemoteSteamId);
                    if (CurrentLobbyId.IsValid())
                        SteamMatchmaking.LeaveLobby(CurrentLobbyId);
                }
                catch { }
            }

            CurrentLobbyId = CSteamID.Nil;
            RemoteSteamId = CSteamID.Nil;
            RemoteUdpEndPoint = null;

            if (_udpClient != null)
            {
                try { _udpClient.Close(); } catch { }
                _udpClient = null;
            }

            _pendingReliables.Clear();
            _receivedReliableSeqs.Clear();
            StatusMessage = "Disconnected";

            if (wasConnected && OnPeerDisconnected != null)
                OnPeerDisconnected();
        }

        public void Send(byte[] payload, bool reliable)
        {
            if (payload == null || payload.Length == 0 || Role == CoopRole.None)
                return;

            if (UsingSteamP2P)
            {
                if (!IsSteamInitialized() || !RemoteSteamId.IsValid())
                    return;

                EP2PSend sendType = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
                int channel = reliable ? SteamChannelReliable : SteamChannelUnreliable;
                SteamNetworking.SendP2PPacket(RemoteSteamId, payload, (uint)payload.Length, sendType, channel);
                return;
            }

            if (_udpClient == null || RemoteUdpEndPoint == null)
                return;

            try
            {
                if (reliable)
                {
                    uint seq = _nextReliableSeq++;
                    byte[] framed = new byte[payload.Length + 5];
                    framed[0] = UdpFlagReliable;
                    byte[] seqBytes = BitConverter.GetBytes(seq);
                    Buffer.BlockCopy(seqBytes, 0, framed, 1, 4);
                    Buffer.BlockCopy(payload, 0, framed, 5, payload.Length);

                    _pendingReliables[seq] = new PendingReliablePacket
                    {
                        Sequence = seq,
                        FramedBytes = framed,
                        LastSentUtc = DateTime.UtcNow,
                        Attempts = 1
                    };
                    _udpClient.Send(framed, framed.Length, RemoteUdpEndPoint);
                }
                else
                {
                    byte[] framed = new byte[payload.Length + 1];
                    framed[0] = UdpFlagUnreliable;
                    Buffer.BlockCopy(payload, 0, framed, 1, payload.Length);
                    _udpClient.Send(framed, framed.Length, RemoteUdpEndPoint);
                }
            }
            catch (SocketException)
            {
                // Non-blocking UDP socket buffer transient condition
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("UDP Send error: " + ex.Message);
            }
        }

        public void Poll()
        {
            EnsureSteamCallbacks();

            if (UsingSteamP2P && IsSteamInitialized())
            {
                PollSteamChannel(SteamChannelUnreliable);
                PollSteamChannel(SteamChannelReliable);
            }
            else if (_udpClient != null)
            {
                PollUdp();
                RetransmitPendingReliables();
            }
        }

        private void PollSteamChannel(int channel)
        {
            uint msgSize;
            while (SteamNetworking.IsP2PPacketAvailable(out msgSize, channel))
            {
                byte[] buffer = new byte[msgSize];
                uint bytesRead;
                CSteamID senderId;
                if (SteamNetworking.ReadP2PPacket(buffer, msgSize, out bytesRead, out senderId, channel))
                {
                    if (!RemoteSteamId.IsValid() && senderId != SteamUser.GetSteamID())
                    {
                        RemoteSteamId = senderId;
                        IsConnected = true;
                        StatusMessage = "Connected via Steam P2P (" + SteamFriends.GetFriendPersonaName(senderId) + ")";
                        if (OnPeerConnected != null)
                            OnPeerConnected();
                    }
                    HandleRawPayload(buffer);
                }
            }
        }

        private void PollUdp()
        {
            try
            {
                while (_udpClient != null && _udpClient.Available > 0)
                {
                    IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                    byte[] datagram = _udpClient.Receive(ref sender);
                    if (datagram == null || datagram.Length < 2)
                        continue;

                    if (Role == CoopRole.Host && (RemoteUdpEndPoint == null || !RemoteUdpEndPoint.Equals(sender)))
                    {
                        RemoteUdpEndPoint = sender;
                        if (!IsConnected)
                        {
                            IsConnected = true;
                            StatusMessage = "Connected (UDP Client " + sender + ")";
                            CupheadCoopBootstrapper.Log(StatusMessage);
                            if (OnPeerConnected != null)
                                OnPeerConnected();
                        }
                    }

                    byte header = datagram[0];
                    if (header == (byte)PacketType.UdpAck && datagram.Length >= 5)
                    {
                        uint ackSeq = BitConverter.ToUInt32(datagram, 1);
                        _pendingReliables.Remove(ackSeq);
                        continue;
                    }

                    if ((header & UdpFlagReliable) != 0)
                    {
                        if (datagram.Length < 6)
                            continue;
                        uint seq = BitConverter.ToUInt32(datagram, 1);
                        SendUdpAck(seq);

                        if (!_receivedReliableSeqs.Add(seq))
                            continue; // Duplicate reliable packet; ignore

                        byte[] payload = new byte[datagram.Length - 5];
                        Buffer.BlockCopy(datagram, 5, payload, 0, payload.Length);
                        HandleRawPayload(payload);
                    }
                    else
                    {
                        byte[] payload = new byte[datagram.Length - 1];
                        Buffer.BlockCopy(datagram, 1, payload, 0, payload.Length);
                        HandleRawPayload(payload);
                    }
                }
            }
            catch (SocketException)
            {
                // Ignore non-blocking socket EWOULDBLOCK / WSAECONNRESET
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("PollUdp error: " + ex.Message);
            }
        }

        private void SendUdpAck(uint seq)
        {
            if (_udpClient == null || RemoteUdpEndPoint == null)
                return;
            byte[] ack = new byte[5];
            ack[0] = (byte)PacketType.UdpAck;
            Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, ack, 1, 4);
            try { _udpClient.Send(ack, ack.Length, RemoteUdpEndPoint); } catch { }
        }

        private void RetransmitPendingReliables()
        {
            if (_pendingReliables.Count == 0 || _udpClient == null || RemoteUdpEndPoint == null)
                return;

            DateTime now = DateTime.UtcNow;
            List<uint> expired = null;

            foreach (KeyValuePair<uint, PendingReliablePacket> kv in _pendingReliables)
            {
                PendingReliablePacket pkt = kv.Value;
                if ((now - pkt.LastSentUtc).TotalMilliseconds >= 120.0)
                {
                    if (pkt.Attempts >= 25)
                    {
                        if (expired == null) expired = new List<uint>();
                        expired.Add(kv.Key);
                        continue;
                    }
                    pkt.Attempts++;
                    pkt.LastSentUtc = now;
                    try { _udpClient.Send(pkt.FramedBytes, pkt.FramedBytes.Length, RemoteUdpEndPoint); } catch { }
                }
            }

            if (expired != null)
            {
                for (int i = 0; i < expired.Count; i++)
                    _pendingReliables.Remove(expired[i]);
            }
        }

        public void InjectTestPayload(byte[] payload)
        {
            if (!IsConnected)
            {
                IsConnected = true;
                if (OnPeerConnected != null)
                    OnPeerConnected();
            }
            HandleRawPayload(payload);
        }

        private void HandleRawPayload(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return;

            PacketType pType = (PacketType)payload[0];
            if (pType == PacketType.Ping && payload.Length >= 9)
            {
                long sentTicks = BitConverter.ToInt64(payload, 1);
                Send(CoopProtocol.BuildPingPong(PacketType.Pong, sentTicks), false);
                return;
            }
            if (pType == PacketType.Pong && payload.Length >= 9)
            {
                long sentTicks = BitConverter.ToInt64(payload, 1);
                long elapsedTicks = DateTime.UtcNow.Ticks - sentTicks;
                LastRttMs = (float)(elapsedTicks / (double)TimeSpan.TicksPerMillisecond);
                return;
            }

            if (OnPacketReceived != null)
                OnPacketReceived(payload);
        }

        private void OnSteamLobbyCreated(LobbyCreated_t param, bool bIOFailure)
        {
            if (bIOFailure || param.m_eResult != EResult.k_EResultOK)
            {
                StatusMessage = "Failed to create Steam Lobby: " + param.m_eResult;
                return;
            }

            CurrentLobbyId = new CSteamID(param.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(CurrentLobbyId, "cuphead_coop_ver", CoopProtocol.ProtocolVersion.ToString());
            StatusMessage = "Hosting Steam Lobby (" + CurrentLobbyId.m_SteamID + "). Invite your friend!";
            CupheadCoopBootstrapper.Log(StatusMessage);
        }

        private void OnSteamGameLobbyJoinRequested(GameLobbyJoinRequested_t param)
        {
            CupheadCoopBootstrapper.Log("Steam Overlay Join requested for lobby " + param.m_steamIDLobby.m_SteamID);
            JoinSteamLobby(param.m_steamIDLobby.m_SteamID);
        }

        private void OnSteamLobbyEntered(LobbyEnter_t param)
        {
            CurrentLobbyId = new CSteamID(param.m_ulSteamIDLobby);
            if (Role == CoopRole.Client)
            {
                CSteamID owner = SteamMatchmaking.GetLobbyOwner(CurrentLobbyId);
                if (owner.IsValid() && owner != SteamUser.GetSteamID())
                {
                    RemoteSteamId = owner;
                    IsConnected = true;
                    StatusMessage = "Connected to Host (" + SteamFriends.GetFriendPersonaName(owner) + ")";
                    CupheadCoopBootstrapper.Log(StatusMessage);
                    if (OnPeerConnected != null)
                        OnPeerConnected();
                }
            }
        }

        private void OnSteamLobbyChatUpdate(LobbyChatUpdate_t param)
        {
            CSteamID changedUser = new CSteamID(param.m_ulSteamIDUserChanged);
            if (changedUser == SteamUser.GetSteamID())
                return;

            uint change = param.m_rgfChatMemberStateChange;
            if ((change & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                RemoteSteamId = changedUser;
                SteamNetworking.AcceptP2PSessionWithUser(changedUser);
                IsConnected = true;
                StatusMessage = "Friend joined (" + SteamFriends.GetFriendPersonaName(changedUser) + ")";
                CupheadCoopBootstrapper.Log(StatusMessage);
                if (OnPeerConnected != null)
                    OnPeerConnected();
            }
            else if ((change & (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected)) != 0)
            {
                if (changedUser == RemoteSteamId)
                {
                    IsConnected = false;
                    RemoteSteamId = CSteamID.Nil;
                    StatusMessage = "Friend disconnected from lobby.";
                    if (OnPeerDisconnected != null)
                        OnPeerDisconnected();
                }
            }
        }

        private void OnSteamP2PSessionRequest(P2PSessionRequest_t param)
        {
            SteamNetworking.AcceptP2PSessionWithUser(param.m_steamIDRemote);
            if (!RemoteSteamId.IsValid() && Role != CoopRole.None)
            {
                RemoteSteamId = param.m_steamIDRemote;
                IsConnected = true;
                StatusMessage = "Connected via Steam P2P (" + SteamFriends.GetFriendPersonaName(RemoteSteamId) + ")";
                if (OnPeerConnected != null)
                    OnPeerConnected();
            }
        }

        private void OnSteamP2PConnectFail(P2PSessionConnectFail_t param)
        {
            StatusMessage = "Steam P2P Error: " + (EP2PSessionError)param.m_eP2PSessionError;
            CupheadCoopBootstrapper.Log(StatusMessage);
        }
    }
}
