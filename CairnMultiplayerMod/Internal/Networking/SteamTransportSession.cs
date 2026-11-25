using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSteamworks;

namespace CairnMultiplayerMod.Internal.Networking;

internal sealed class SteamTransportSession : INetworkTransportSession
{
    private const int GameplayChannel = 0;

    private readonly SteamLobbyManager _lobby;
    private readonly ConcurrentQueue<(ulong peerId, byte[] payload)> _incoming = new();

    internal SteamTransportSession(SteamLobbyManager lobby)
    {
        _lobby = lobby ?? throw new ArgumentNullException(nameof(lobby));
        try { LocalPeerId = SteamUser.GetSteamID().m_SteamID; }
        catch (Exception exception) { ModLog.SuppressedException("steam-transport.local-id", exception); }
    }

    public bool IsActive => _lobby.IsInLobby;
    public bool IsHost => _lobby.IsHost;
    public ulong LocalPeerId { get; set; }
    public ulong HostPeerId => _lobby.HostSteamId;
    public string DisconnectReason => "";
    public bool IsInLobby => _lobby.IsInLobby;
    public string CurrentRoomCode => _lobby.CurrentRoomCode;
    public string CurrentLobbyName => _lobby.CurrentLobbyName;
    public string LocalPersonaName => _lobby.LocalPersonaName;
    public int MaxMembers => _lobby.MaxMembers;
    public IReadOnlyList<LobbyMember> Members => _lobby.Members;
    public MultiplayerModeRules CurrentModeRules => _lobby.CurrentModeRules;
    public ClimbWorld CurrentWorld => _lobby.CurrentWorld;

    public void PublishWorldAvailability(bool available) => _lobby.PublishWorldAvailability(available);

    public void Send(ulong peerId, byte[] payload, bool reliable = true)
    {
        if (peerId == 0 || payload == null || payload.Length == 0) return;
        try
        {
            var data = new Il2CppStructArray<byte>(payload.Length);
            for (var i = 0; i < payload.Length; i++) data[i] = payload[i];
            var sendType = reliable || payload.Length > 1100
                ? EP2PSend.k_EP2PSendReliable
                : EP2PSend.k_EP2PSendUnreliableNoDelay;
            SteamNetworking.SendP2PPacket(new CSteamID(peerId), data, (uint)payload.Length,
                sendType, GameplayChannel);
        }
        catch (Exception exception) { ModLog.SuppressedException("steam-transport.send", exception); }
    }

    public void Broadcast(byte[] payload, bool reliable = true)
    {
        if (payload == null || payload.Length == 0) return;
        foreach (var member in Members)
        {
            if (member.SteamId == 0 || member.SteamId == LocalPeerId) continue;
            Send(member.SteamId, payload, reliable);
        }
    }

    public void Pump()
    {
        try
        {
            uint packetSize;
            var guard = 0;
            while (SteamNetworking.IsP2PPacketAvailable(out packetSize, GameplayChannel) && guard++ < 256)
            {
                if (packetSize == 0) break;
                var buffer = new Il2CppStructArray<byte>((long)packetSize);
                uint bytesRead;
                CSteamID remoteId;
                if (!SteamNetworking.ReadP2PPacket(buffer, packetSize, out bytesRead, out remoteId, GameplayChannel))
                    break;

                var payload = new byte[bytesRead];
                for (var i = 0; i < bytesRead; i++) payload[i] = buffer[i];
                _incoming.Enqueue((remoteId.m_SteamID, payload));
            }
        }
        catch (Exception exception) { ModLog.SuppressedException("steam-transport.pump", exception); }
    }

    public bool TryRead(out ulong peerId, out byte[] payload)
    {
        if (_incoming.TryDequeue(out var packet))
        {
            peerId = packet.peerId;
            payload = packet.payload;
            return true;
        }

        peerId = 0;
        payload = null;
        return false;
    }

    public bool ConsumeMembersChanged() => false;

    public bool IsPeerAllowed(ulong peerId)
    {
        if (peerId == 0) return false;
        if (peerId == LocalPeerId) return true;
        if (!_lobby.IsInLobby) return false;

        try
        {
            var count = SteamMatchmaking.GetNumLobbyMembers(_lobby.CurrentLobbyId);
            for (var i = 0; i < count; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(_lobby.CurrentLobbyId, i).m_SteamID == peerId)
                    return true;
        }
        catch (Exception exception) { ModLog.SuppressedException("steam-transport.lookup-member", exception); }

        foreach (var member in Members)
            if (member.SteamId == peerId) return true;

        return false;
    }

    public bool BroadcastStart(ServerStartGame start)
    {
        if (!_lobby.BroadcastStart(start)) return false;

        var frame = PacketCodec.Frame(PacketId.ServerStartGame, start);
        var payload = new byte[frame.Length - 2];
        Buffer.BlockCopy(frame, 2, payload, 0, payload.Length);
        Broadcast(payload, reliable: true);
        return true;
    }

    public void Dispose()
    {
        while (_incoming.TryDequeue(out _)) { }
    }
}
