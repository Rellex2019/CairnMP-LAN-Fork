using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Networking.Authoritative;
using Il2CppInterop.Runtime;
using Il2CppSteamworks;

namespace CairnMultiplayerMod.Internal.Networking;

internal sealed partial class NetworkManager
{
    private const int SteamGameplayChannel = 0;
    private const double SteamReliablePresenceIntervalSeconds = 1.0;

    private INetworkTransportSession _session;
    private SteamLobbyManager _steamLobby;
    private bool _transportActive;
    private double _lastReliablePresenceSentAt;
    private double _lastSteamMemberRefreshAt;
    private double _lastSteamSendFailLogAt;
    private double _lastSteamPacketDropLogAt;
    private int _droppedSteamPackets;
    private readonly Dictionary<ulong, int> _steamPlayerIds = new();
    private readonly Dictionary<int, ulong> _steamIdsByPlayerId = new();
    private readonly Dictionary<ulong, string> _steamPlayerNames = new();
    private readonly Dictionary<ulong, double> _steamReliableForwardTimes = new();
    private readonly HashSet<ulong> _steamMembersPresent = new();
    private readonly PeerBudget _ropeCreationBudget = new(4, 1);
    private readonly List<int> _steamPlayersToRemove = new();
    // Piton state is owned by a transport-independent authority.
    private readonly HashSet<ulong> _steamLoggedFirstClientPlayerFrame = new();
    private readonly HashSet<ulong> _steamLoggedFirstClientClimbotFrame = new();
    private Callback<P2PSessionRequest_t> _steamP2PSessionRequestCallback;
    private Callback<P2PSessionConnectFail_t> _steamP2PSessionConnectFailCallback;

    public bool IsTransportActive => _transportActive && _session != null && _session.IsActive;

    private ulong LocalPeerId => _session?.LocalPeerId ?? 0;
    private ulong HostPeerId => _session?.HostPeerId ?? 0;

    public void StartSteamTransport(SteamLobbyManager lobby)
    {
        if (lobby == null || !lobby.IsInLobby) return;

        try { SteamNetworking.AllowP2PPacketRelay(true); }
        catch (Exception ex) { ModLog.Warning($"[SteamP2P] Allow relay failed: {ex.Message}"); }

        StartTransport(new SteamTransportSession(lobby), lobby.CurrentLobbyId.m_SteamID.ToString());
        _steamLobby = lobby;
        EnsureSteamP2PCallbacks();
    }

    public void StartDirectTransport(INetworkTransportSession session)
    {
        if (session == null || !session.IsActive) return;
        StartTransport(session, "");
    }

    private void StartTransport(INetworkTransportSession session, string lobbyId)
    {
        Reset();
        _session = session;
        _transportActive = true;
        _lastReliablePresenceSentAt = 0;
        CurrentLobbyId = lobbyId;
        CurrentRoomCode = session.CurrentRoomCode;
        ServerName = string.IsNullOrEmpty(session.CurrentLobbyName)
            ? (session.IsHost ? "Direct lobby" : "Steam lobby")
            : session.CurrentLobbyName;
        LocalPlayerId = StablePeerPlayerId(session.LocalPeerId);
        IsHandshakeComplete = session.IsHost;

        RefreshSessionMembers();
        InitializeExtensionApi();
        if (session.IsHost)
            OnHandshakeAck?.Invoke();
        ModLog.Debug($"[CairnMP] Transport active ({session.GetType().Name}). local={session.LocalPeerId} host={session.HostPeerId} playerId={LocalPlayerId}");
    }

    public void RefreshSteamLobbyMembers(SteamLobbyManager lobby)
    {
        if (lobby == null || !lobby.IsInLobby) return;
        if (_session is SteamTransportSession) RefreshSessionMembers();
    }

    private void RefreshSessionMembers()
    {
        var session = _session;
        if (!IsTransportActive || session == null) return;

        _steamMembersPresent.Clear();
        foreach (var member in session.Members)
        {
            if (member.SteamId == 0) continue;
            _steamMembersPresent.Add(member.SteamId);
            _steamPlayerNames[member.SteamId] = string.IsNullOrEmpty(member.Name) ? $"Player{member.SteamId}" : member.Name;
            var playerId = member.PlayerId != 0 ? member.PlayerId : StablePeerPlayerId(member.SteamId);
            _steamPlayerIds[member.SteamId] = playerId;
            _steamIdsByPlayerId[playerId] = member.SteamId;

            if (playerId == LocalPlayerId)
            {
                _remotePlayers.Remove(playerId);
                continue;
            }
            if (member.SteamId == LocalPeerId) continue;

            if (!_remotePlayers.ContainsKey(playerId))
            {
                var rp = new RemotePlayer
                {
                    Id = playerId,
                    Name = _steamPlayerNames[member.SteamId],
                    State = PlayerState.Connecting,
                };
                _remotePlayers[playerId] = rp;
                OnPlayerJoined?.Invoke(playerId, rp.Name);
            }
            else
            {
                _remotePlayers[playerId].Name = _steamPlayerNames[member.SteamId];
            }
        }

        _steamPlayersToRemove.Clear();
        foreach (var kv in _steamIdsByPlayerId)
        {
            if (kv.Value == LocalPeerId) continue;
            if (!_steamMembersPresent.Contains(kv.Value)) _steamPlayersToRemove.Add(kv.Key);
        }

        foreach (var playerId in _steamPlayersToRemove)
        {
            _ropeCreationBudget.Remove(playerId);
            NotifyExtensionPeerLeft(playerId);
            if (_steamIdsByPlayerId.TryGetValue(playerId, out var peerId))
            {
                _steamPlayerIds.Remove(peerId);
                _steamPlayerNames.Remove(peerId);
                _manifestHandledPeers.Remove(peerId);
            }
            _steamIdsByPlayerId.Remove(playerId);
            if (session.IsHost) _pitonAuthority?.RemovePlayer(playerId);
            if (_remotePlayers.Remove(playerId))
                OnPlayerLeft?.Invoke(playerId);
        }
    }

    private void PumpTransport()
    {
        if (!_transportActive || _session == null) return;

        if (_session is SteamTransportSession && (_steamLobby == null || !_steamLobby.IsInLobby))
        {
            var reason = "Steam lobby closed";
            Reset();
            LastError = reason;
            OnDisconnected?.Invoke(reason);
            return;
        }

        if (!_session.IsActive)
        {
            var reason = string.IsNullOrEmpty(_session.DisconnectReason) ? "Connection closed" : _session.DisconnectReason;
            Reset();
            LastError = reason;
            OnDisconnected?.Invoke(reason);
            ModLog.Warning($"[CairnMP] Connection lost: {reason}");
            return;
        }

        if (SessionMembersChanged() || ShouldRefreshSteamMembers())
            RefreshSessionMembers();

        _session.Pump();
        var guard = 0;
        while (_session.TryRead(out var peerId, out var payload) && guard++ < 512)
        {
            if (payload == null || payload.Length == 0) continue;
            try { ProcessIncomingPacket(peerId, payload); }
            catch (Exception ex) { LogDroppedPacket(peerId, payload, ex); }
        }
    }

    private bool SessionMembersChanged() => _session?.ConsumeMembersChanged() ?? false;

    private void ProcessIncomingPacket(ulong peerId, byte[] payload)
    {
        if (!IsSessionPeer(peerId))
        {
            ModLog.Warning($"[CairnMP] Ignored packet from unknown peer {peerId}");
            return;
        }

        var id = (PacketId)payload[0];
        if (_session != null && _session.IsHost)
        {
            ProcessHostPacket(peerId, id, payload);
            return;
        }

        if (peerId != HostPeerId)
        {
            ModLog.Warning($"[CairnMP] Ignored non-host packet {id} from {peerId}");
            return;
        }

        ProcessPacket(payload);
    }

    private void ProcessHostPacket(ulong remoteSteamId, PacketId id, byte[] payload)
    {
        using var ms = new MemoryStream(payload, 1, payload.Length - 1, writable: false);
        using var r = new BinaryReader(ms);

        if (HandleExtensionHostPacket(remoteSteamId, id, r))
            return;
        if (!IsExtensionPeerAdmitted(remoteSteamId))
            return;

        switch (id)
        {
            case PacketId.ClientPlayerState:
                {
                    var pkt = new ClientPlayerState();
                    pkt.Deserialize(r);
                    if (!PacketValidation.IsValidPose(pkt.X, pkt.Y, pkt.Z, pkt.YawDeg)) break;

                    var playerId = EnsureRemotePlayer(remoteSteamId);
                    ApplyRemotePlayerState(playerId, pkt);
                    var reliablePresence = ShouldForwardReliablePresence(remoteSteamId);
                    BroadcastServerPacket(PacketId.ServerPlayerState, new ServerPlayerState
                    {
                        PlayerId = playerId,
                        X = pkt.X,
                        Y = pkt.Y,
                        Z = pkt.Z,
                        YawDeg = pkt.YawDeg,
                        SceneName = pkt.SceneName,
                        State = pkt.State,
                    }, exceptPeerId: remoteSteamId, reliable: reliablePresence);
                    break;
                }
            case PacketId.ClientBoneState:
                {
                    var pkt = new ClientBoneState();
                    pkt.Deserialize(r);
                    if (!PacketValidation.IsValidBoneState(pkt.BoneCount, pkt.Positions, pkt.Rotations)) break;

                    var playerId = EnsureRemotePlayer(remoteSteamId);
                    if (_remotePlayers.TryGetValue(playerId, out var rp))
                    {
                        rp.BoneCount = pkt.BoneCount;
                        rp.BonePositions = pkt.Positions;
                        rp.BoneRotations = pkt.Rotations;
                    }
                    BroadcastServerPacket(PacketId.ServerBoneState, new ServerBoneState
                    {
                        PlayerId = playerId,
                        BoneCount = pkt.BoneCount,
                        Positions = pkt.Positions,
                        Rotations = pkt.Rotations,
                    }, exceptPeerId: remoteSteamId, reliable: true);
                    break;
                }
            case PacketId.ClientPlayerFrame:
                {
                    var pkt = new ClientPlayerFrame();
                    pkt.Deserialize(r);
                    if (!PacketValidation.IsValidNetFrame(pkt.Frame, requirePosition: true, out var rejectReason))
                    {
                        LogRejectedNetFrame($"steam-client-player-{remoteSteamId}", $"client player steam={remoteSteamId}", rejectReason);
                        break;
                    }

                    var playerId = EnsureRemotePlayer(remoteSteamId);
                    var playerName = _steamPlayerNames.TryGetValue(remoteSteamId, out var knownName)
                        ? knownName
                        : $"Player{playerId}";
                    if (_steamLoggedFirstClientPlayerFrame.Add(remoteSteamId))
                        ModLog.Debug($"[NetSync] First ClientPlayerFrame received from steam={remoteSteamId} id={playerId} name='{playerName}' positions={FrameVectorCount(pkt.Frame.Positions)} eulers={FrameVectorCount(pkt.Frame.Eulers)} flags=0x{pkt.Frame.Flags:X2}");
                    ApplyRemotePlayerFrame(playerId, playerName, pkt.Frame);
                    BroadcastServerPacket(PacketId.ServerPlayerFrame, new ServerPlayerFrame
                    {
                        PlayerId = playerId,
                        PlayerName = playerName,
                        Frame = pkt.Frame,
                    }, exceptPeerId: remoteSteamId, reliable: false);
                    break;
                }
            case PacketId.ClientClimbotFrame:
                {
                    var pkt = new ClientClimbotFrame();
                    pkt.Deserialize(r);
                    if (!PacketValidation.IsValidNetFrame(pkt.Frame, requirePosition: true, out var rejectReason))
                    {
                        LogRejectedNetFrame($"steam-client-climbot-{remoteSteamId}", $"client climbot steam={remoteSteamId}", rejectReason);
                        break;
                    }

                    var playerId = EnsureRemotePlayer(remoteSteamId);
                    if (_steamLoggedFirstClientClimbotFrame.Add(remoteSteamId))
                        ModLog.Debug($"[NetSync] First ClientClimbotFrame received from steam={remoteSteamId} id={playerId} positions={FrameVectorCount(pkt.Frame.Positions)} eulers={FrameVectorCount(pkt.Frame.Eulers)} flags=0x{pkt.Frame.Flags:X2}");
                    ApplyRemoteClimbotFrame(playerId, pkt.Frame);
                    BroadcastServerPacket(PacketId.ServerClimbotFrame, new ServerClimbotFrame
                    {
                        PlayerId = playerId,
                        Frame = pkt.Frame,
                    }, exceptPeerId: remoteSteamId, reliable: false);
                    break;
                }
            case PacketId.ClientRopeClip:
                {
                    var pkt = new ClientRopeClip();
                    pkt.Deserialize(r);
                    var fromId = EnsureRemotePlayer(remoteSteamId);
                    if (!RopeRequests.IsAllowed(fromId, pkt.TargetPlayerId, pkt.Clip, _ropeLinks(), IsAdmittedPlayer)) break;
                    if (pkt.Clip && !_ropeCreationBudget.Take(fromId)) break;
                    var outPkt = new ServerRopeClip { FromPlayerId = fromId, TargetPlayerId = pkt.TargetPlayerId, Clip = pkt.Clip };
                    OnRopeClip?.Invoke(fromId, pkt.TargetPlayerId, pkt.Clip);
                    BroadcastServerPacket(PacketId.ServerRopeClip, outPkt, exceptPeerId: 0, reliable: true);
                    break;
                }
            case PacketId.ClientPitonPlaced:
            case PacketId.ClientPitonRemoved:
            case PacketId.ClientPitonRecovered:
                Pitons.OnClientPacket(EnsureRemotePlayer(remoteSteamId), id, r);
                break;
            case PacketId.ClientFeatureStream:
                {
                    var pkt = new ClientFeatureStream();
                    pkt.Deserialize(r);
                    var playerId = EnsureRemotePlayer(remoteSteamId);
                    var outPkt = new ServerFeatureStream
                    {
                        FromPlayerId = playerId,
                        Channel = pkt.Channel,
                        Payload = pkt.Payload,
                    };
                    OnFeatureStream?.Invoke(playerId, pkt.Channel, pkt.Payload);
                    BroadcastServerPacket(PacketId.ServerFeatureStream, outPkt,
                        exceptPeerId: remoteSteamId, reliable: pkt.Reliable);
                    break;
                }
            case PacketId.ClientDisconnect:
                {
                    var playerId = EnsureRemotePlayer(remoteSteamId);
                    _manifestHandledPeers.Add(remoteSteamId);
                    _pitonAuthority?.RemovePlayer(playerId);
                    NotifyExtensionPeerLeft(playerId);
                    if (_remotePlayers.Remove(playerId))
                        OnPlayerLeft?.Invoke(playerId);
                    BroadcastServerPacket(PacketId.ServerPlayerLeft,
                        new ServerPlayerLeft { PlayerId = playerId }, exceptPeerId: remoteSteamId, reliable: true);
                    break;
                }
        }
    }

    private void SendSteamPlayerState(float x, float y, float z, float yaw, string sceneName, PlayerState state)
    {
        if (!IsHandshakeComplete) return;

        var reliablePresence = ShouldSendReliablePresence();
        if (_session?.IsHost == true)
        {
            BroadcastServerPacket(PacketId.ServerPlayerState, new ServerPlayerState
            {
                PlayerId = LocalPlayerId,
                X = x,
                Y = y,
                Z = z,
                YawDeg = yaw,
                SceneName = sceneName ?? "",
                State = state,
            }, exceptPeerId: 0, reliable: reliablePresence);
            return;
        }

        SendPacketToHost(PacketId.ClientPlayerState, new ClientPlayerState
        {
            X = x,
            Y = y,
            Z = z,
            YawDeg = yaw,
            SceneName = sceneName ?? "",
            State = state,
        }, reliable: reliablePresence);
    }

    private void SendSteamPlayerFrame(NetFrameData frame)
    {
        if (!IsHandshakeComplete) return;

        if (_session?.IsHost == true)
        {
            BroadcastServerPacket(PacketId.ServerPlayerFrame, new ServerPlayerFrame
            {
                PlayerId = LocalPlayerId,
                PlayerName = _steamPlayerNames.TryGetValue(LocalPeerId, out var name) ? name : "Player",
                Frame = frame,
            }, exceptPeerId: 0, reliable: false);
            return;
        }

        SendPacketToHost(PacketId.ClientPlayerFrame, new ClientPlayerFrame { Frame = frame }, reliable: false);
    }

    private void SendSteamClimbotFrame(NetFrameData frame)
    {
        if (!IsHandshakeComplete) return;

        if (_session?.IsHost == true)
        {
            BroadcastServerPacket(PacketId.ServerClimbotFrame, new ServerClimbotFrame
            {
                PlayerId = LocalPlayerId,
                Frame = frame,
            }, exceptPeerId: 0, reliable: false);
            return;
        }

        SendPacketToHost(PacketId.ClientClimbotFrame, new ClientClimbotFrame { Frame = frame }, reliable: false);
    }

    private void SendSteamPitonPlaced(ClientPitonPlaced piton)
    {
        if (!IsHandshakeComplete) return;
        if (!PacketValidation.IsValidPitonPayload(
                piton.PosX, piton.PosY, piton.PosZ,
                piton.RotX, piton.RotY, piton.RotZ, piton.RotW,
                piton.Quality, piton.PitonHp, piton.ItemId)) return;

        if (_session?.IsHost == true)
        {
            Pitons.HandleHostPitonPlaced(LocalPlayerId, piton);
            return;
        }

        SendPacketToHost(PacketId.ClientPitonPlaced, piton, reliable: true);
    }

    private void SendSteamPitonRemoved(uint pitonId)
    {
        if (!IsHandshakeComplete) return;

        if (_session?.IsHost == true)
        {
            Pitons.HandleHostPitonRemoved(LocalPlayerId, pitonId);
            return;
        }

        SendPacketToHost(PacketId.ClientPitonRemoved, new ClientPitonRemoved { PitonId = pitonId }, reliable: true);
    }

    private void SendSteamPitonRecovered(uint pitonId)
    {
        if (!IsHandshakeComplete) return;

        if (_session?.IsHost == true)
        {
            Pitons.HandleHostPitonRecovered(LocalPlayerId, pitonId);
            return;
        }

        SendPacketToHost(PacketId.ClientPitonRecovered, new ClientPitonRecovered { PitonId = pitonId }, reliable: true);
    }

    private void SendSteamFeatureStream(ushort channel, byte[] payload, bool reliable)
    {
        if (!IsHandshakeComplete) return;

        if (_session?.IsHost == true)
        {
            BroadcastServerPacket(PacketId.ServerFeatureStream, new ServerFeatureStream
            {
                FromPlayerId = LocalPlayerId,
                Channel = channel,
                Payload = payload,
            }, exceptPeerId: 0, reliable);
            return;
        }

        SendPacketToHost(PacketId.ClientFeatureStream,
            new ClientFeatureStream { Channel = channel, Reliable = reliable, Payload = payload }, reliable);
    }

    private void SendSteamRopeClip(int targetPlayerId, bool clip)
    {
        if (!IsHandshakeComplete || targetPlayerId == LocalPlayerId) return;

        if (_session?.IsHost == true)
        {
            var pkt = new ServerRopeClip { FromPlayerId = LocalPlayerId, TargetPlayerId = targetPlayerId, Clip = clip };
            if (!RopeRequests.IsAllowed(LocalPlayerId, targetPlayerId, clip, _ropeLinks(), IsAdmittedPlayer)) return;
            if (clip && !_ropeCreationBudget.Take(LocalPlayerId)) return;
            OnRopeClip?.Invoke(LocalPlayerId, targetPlayerId, clip);
            BroadcastServerPacket(PacketId.ServerRopeClip, pkt, exceptPeerId: 0, reliable: true);
            return;
        }

        SendPacketToHost(PacketId.ClientRopeClip,
            new ClientRopeClip { TargetPlayerId = targetPlayerId, Clip = clip }, reliable: true);
    }

    private void SendPacketToHost(PacketId id, IPacket packet, bool reliable)
    {
        if (HostPeerId == 0 || HostPeerId == LocalPeerId) return;
        SendPayload(HostPeerId, BuildPayload(id, packet), reliable);
    }

    private bool IsAdmittedPlayer(int playerId)
        => _enabledExtensionsByPlayer.ContainsKey(playerId)
           && _steamIdsByPlayerId.TryGetValue(playerId, out var peerId)
           && _steamMembersPresent.Contains(peerId);

    public bool SendTeleportToPlayer(int targetPlayerId, float x, float y, float z, float yaw)
    {
        if (!IsTransportActive || _session?.IsHost != true) return false;
        if (!_steamIdsByPlayerId.TryGetValue(targetPlayerId, out var peerId) || peerId == LocalPeerId)
            return false;

        var pkt = new ServerTeleport { X = x, Y = y, Z = z, Yaw = yaw };
        SendPayload(peerId, BuildPayload(PacketId.ServerTeleport, pkt), reliable: true);
        ModLog.Debug($"[CairnMP] Sent ServerTeleport to player {targetPlayerId} (peer={peerId})");
        return true;
    }

    private void SendSnapshotTo(ulong peerId)
    {
        if (peerId == 0 || peerId == LocalPeerId)
            return;

        Pitons.SendSnapshotTo(EnsureRemotePlayer(peerId));

        foreach (var (a, b) in _ropeLinks())
            SendPayload(peerId, BuildPayload(PacketId.ServerRopeClip,
                new ServerRopeClip { FromPlayerId = a, TargetPlayerId = b, Clip = true }), reliable: true);
    }

    private void BroadcastServerPacket(PacketId id, IPacket packet, ulong exceptPeerId, bool reliable)
    {
        var session = _session;
        if (session == null) return;
        var payload = BuildPayload(id, packet);
        foreach (var member in session.Members)
        {
            if (member.SteamId == 0 || member.SteamId == LocalPeerId || member.SteamId == exceptPeerId) continue;
            if (session.IsHost && !IsExtensionPeerAdmitted(member.SteamId)) continue;
            SendPayload(member.SteamId, payload, reliable);
        }
    }

    private bool SendPayload(ulong target, byte[] payload, bool reliable)
    {
        var session = _session;
        if (!IsTransportActive || session == null || payload == null || payload.Length == 0) return false;

        try
        {
            session.Send(target, payload, reliable);
            return true;
        }
        catch (Exception ex)
        {
            if (ShouldLogSteamSendFailure())
                ModLog.Warning($"[CairnMP] Send exception target={target}: {ex.Message}");
            return false;
        }
    }

    private bool ShouldSendReliablePresence()
    {
        var now = NowSeconds();
        if (now - _lastReliablePresenceSentAt < SteamReliablePresenceIntervalSeconds)
            return false;

        _lastReliablePresenceSentAt = now;
        return true;
    }

    private bool ShouldRefreshSteamMembers()
    {
        var now = NowSeconds();
        if (now - _lastSteamMemberRefreshAt < 1.0)
            return false;

        _lastSteamMemberRefreshAt = now;
        return true;
    }

    private bool ShouldLogSteamSendFailure()
    {
        var now = NowSeconds();
        if (now - _lastSteamSendFailLogAt < 2.0)
            return false;

        _lastSteamSendFailLogAt = now;
        return true;
    }

    private void LogDroppedPacket(ulong peerId, byte[] payload, Exception exception)
    {
        _droppedSteamPackets++;
        var now = NowSeconds();
        if (now - _lastSteamPacketDropLogAt < 2.0)
            return;

        _lastSteamPacketDropLogAt = now;
        var id = payload.Length > 0 ? ((PacketId)payload[0]).ToString() : "(empty)";
        ModLog.Warning($"[CairnMP] Dropped packet {id} from {peerId} " +
                       $"(size={payload.Length}, dropped so far={_droppedSteamPackets}): " +
                       $"{exception.GetType().Name}: {exception.Message}");
    }

    private bool ShouldForwardReliablePresence(ulong sourceSteamId)
    {
        var now = NowSeconds();
        if (_steamReliableForwardTimes.TryGetValue(sourceSteamId, out var last) &&
            now - last < SteamReliablePresenceIntervalSeconds)
        {
            return false;
        }

        _steamReliableForwardTimes[sourceSteamId] = now;
        return true;
    }

    private static double NowSeconds() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    private int EnsureRemotePlayer(ulong peerId)
    {
        var playerId = _steamPlayerIds.TryGetValue(peerId, out var knownId)
            ? knownId
            : StablePeerPlayerId(peerId);
        if (!_steamPlayerIds.ContainsKey(peerId))
        {
            _steamPlayerIds[peerId] = playerId;
            _steamIdsByPlayerId[playerId] = peerId;
        }

        if (!_remotePlayers.ContainsKey(playerId))
        {
            var name = _steamPlayerNames.TryGetValue(peerId, out var knownName)
                ? knownName
                : $"Player{playerId}";
            _remotePlayers[playerId] = new RemotePlayer
            {
                Id = playerId,
                Name = name,
                State = PlayerState.Connecting,
            };
            OnPlayerJoined?.Invoke(playerId, name);
        }

        return playerId;
    }

    private void ApplyRemotePlayerState(int playerId, ClientPlayerState pkt)
    {
        if (!PacketValidation.IsValidPose(pkt.X, pkt.Y, pkt.Z, pkt.YawDeg)) return;
        if (!_remotePlayers.TryGetValue(playerId, out var p)) return;
        var wasInGame = p.State == PlayerState.InGame;
        p.X = pkt.X;
        p.Y = pkt.Y;
        p.Z = pkt.Z;
        p.YawDeg = pkt.YawDeg;
        p.SceneName = pkt.SceneName;
        p.State = pkt.State;
        p.LastUpdateTime = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

        if (!wasInGame && p.State == PlayerState.InGame && !p.PitonsReplayedOnEntry)
        {
            Pitons.SendSnapshotTo(playerId);
            p.PitonsReplayedOnEntry = true;
        }
        else if (p.State != PlayerState.InGame)
        {
            p.PitonsReplayedOnEntry = false;
        }
    }

    private static byte[] BuildPayload(PacketId id, IPacket packet)
    {
        var frame = PacketCodec.Frame(id, packet);
        var payload = new byte[Math.Max(0, frame.Length - 2)];
        Buffer.BlockCopy(frame, 2, payload, 0, payload.Length);
        return payload;
    }

    internal static int StablePeerPlayerId(ulong peerId)
    {
        var folded = (uint)(peerId ^ (peerId >> 32));
        var id = (int)(folded & 0x7FFFFFFF);
        return id == 0 ? 1 : id;
    }

    private void EnsureSteamP2PCallbacks()
    {
        try
        {
            _steamP2PSessionRequestCallback ??= Callback<P2PSessionRequest_t>.Create(
                DelegateSupport.ConvertDelegate<Callback<P2PSessionRequest_t>.DispatchDelegate>(
                    new Action<P2PSessionRequest_t>(OnSteamP2PSessionRequest)));
            _steamP2PSessionConnectFailCallback ??= Callback<P2PSessionConnectFail_t>.Create(
                DelegateSupport.ConvertDelegate<Callback<P2PSessionConnectFail_t>.DispatchDelegate>(
                    new Action<P2PSessionConnectFail_t>(OnSteamP2PSessionConnectFail)));
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[SteamP2P] Callback registration failed: {ex.Message}");
        }
    }

    private void OnSteamP2PSessionRequest(P2PSessionRequest_t evt)
    {
        var remoteId = evt.m_steamIDRemote;
        if (!_transportActive || _session is not SteamTransportSession || remoteId.m_SteamID == 0) return;
        if (!IsSessionPeer(remoteId.m_SteamID))
        {
            ModLog.Warning($"[SteamP2P] Rejected session request from non-lobby peer {remoteId.m_SteamID}");
            return;
        }

        try
        {
            SteamNetworking.AcceptP2PSessionWithUser(remoteId);
            EnsureRemotePlayer(remoteId.m_SteamID);
            ModLog.Debug($"[SteamP2P] Accepted session request from {remoteId.m_SteamID}");
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[SteamP2P] Accept session failed for {remoteId.m_SteamID}: {ex.Message}");
        }
    }

    private void OnSteamP2PSessionConnectFail(P2PSessionConnectFail_t evt)
    {
        var remoteId = evt.m_steamIDRemote;
        ModLog.Warning($"[SteamP2P] Session failed remote={remoteId.m_SteamID} error={evt.m_eP2PSessionError}");
    }

    private bool IsSessionPeer(ulong peerId)
    {
        if (peerId == 0 || _session == null) return false;
        if (_session.IsPeerAllowed(peerId)) return true;
        if (_session is not SteamTransportSession || _steamLobby == null || !_steamLobby.IsInLobby) return false;

        // Reads Steam directly to cover the short delay before OnMembersChanged.
        try
        {
            var count = SteamMatchmaking.GetNumLobbyMembers(_steamLobby.CurrentLobbyId);
            for (int i = 0; i < count; i++)
            {
                var member = SteamMatchmaking.GetLobbyMemberByIndex(_steamLobby.CurrentLobbyId, i);
                if (member.m_SteamID == peerId) return true;
            }
        }
        catch (Exception exception) { ModLog.SuppressedException("steam-p2p.close-lobby-members", exception); }

        foreach (var member in _steamLobby.Members)
            if (member.SteamId == peerId) return true;

        return false;
    }

    private void StopTransport()
    {
        var previousSession = _session;
        if (previousSession is SteamTransportSession)
        {
            foreach (var steamId in _steamPlayerIds.Keys)
            {
                if (steamId == 0 || steamId == LocalPeerId) continue;
                try { SteamNetworking.CloseP2PSessionWithUser(new CSteamID(steamId)); }
                catch (Exception exception) { ModLog.SuppressedException("steam-p2p.close-session", exception); }
            }
        }

        try { previousSession?.Dispose(); }
        catch (Exception exception) { ModLog.SuppressedException("transport.dispose", exception); }
        ResetTransportState();
    }

    private void DisposeSteamP2PCallbacks()
    {
        try { _steamP2PSessionRequestCallback?.Dispose(); }
        catch (Exception exception) { ModLog.SuppressedException("steam-p2p.dispose-session-request-callback", exception); }
        try { _steamP2PSessionConnectFailCallback?.Dispose(); }
        catch (Exception exception) { ModLog.SuppressedException("steam-p2p.dispose-connect-fail-callback", exception); }
        _steamP2PSessionRequestCallback = null;
        _steamP2PSessionConnectFailCallback = null;
    }

    private void ResetTransportState()
    {
        _ropeCreationBudget.Clear();
        ResetExtensionApi();
        _transportActive = false;
        _session = null;
        _steamLobby = null;
        _steamPlayerIds.Clear();
        _steamIdsByPlayerId.Clear();
        _steamPlayerNames.Clear();
        _steamReliableForwardTimes.Clear();
        _steamMembersPresent.Clear();
        _steamPlayersToRemove.Clear();
        _pitonAuthority?.Reset();
        _pitonAuthority = null;
        _steamLoggedFirstClientPlayerFrame.Clear();
        _steamLoggedFirstClientClimbotFrame.Clear();
        _lastReliablePresenceSentAt = 0;
        _lastSteamMemberRefreshAt = 0;
        _lastSteamSendFailLogAt = 0;
        DisposeSteamP2PCallbacks();
    }
}
