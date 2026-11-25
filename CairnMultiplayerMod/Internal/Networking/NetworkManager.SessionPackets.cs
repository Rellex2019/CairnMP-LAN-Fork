using System;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;

namespace CairnMultiplayerMod.Internal.Networking;

internal sealed partial class NetworkManager
{
    private void HandleHandshakeAck(BinaryReader reader)
    {
        var packet = new ServerHandshakeAck();
        packet.Deserialize(reader);
        LocalPlayerId = packet.AssignedPlayerId;
        ServerName = packet.ServerName;
        IsHandshakeComplete = true;
        ModLog.Info($"[CairnMP] Handshake OK. id={packet.AssignedPlayerId} server='{packet.ServerName}'");
        OnHandshakeAck?.Invoke();
    }

    private void HandleDirectSessionInfo(BinaryReader reader)
    {
        var packet = new ServerDirectSessionInfo();
        packet.Deserialize(reader);
        LocalPlayerId = packet.AssignedPlayerId;
        if (_session is DirectTransportSession direct)
        {
            direct.LocalPeerId = (ulong)packet.AssignedPlayerId;
            direct.SetLocalSessionInfo(packet);
        }
        ServerName = packet.ServerName;
        var mode = MultiplayerModes.Parse(packet.Mode);
        var world = Enum.IsDefined(typeof(ClimbWorld), packet.World)
            ? (ClimbWorld)packet.World
            : ClimbWorld.Kami;
        ModLog.Info($"[Direct] Session '{packet.ServerName}' assigned player id {packet.AssignedPlayerId} " +
                    $"({mode.Name}, {ClimbWorlds.Name(world)})");
        OnDirectSessionInfo?.Invoke(packet);
    }

    private void HandleHandshakeReject(BinaryReader reader)
    {
        var packet = new ServerHandshakeReject();
        packet.Deserialize(reader);
        LastError = packet.Reason;
        ModLog.Error($"[CairnMP] Handshake rejected: {packet.Reason}");
        OnHandshakeRejected?.Invoke(packet.Reason);
    }

    private void HandlePlayerJoined(BinaryReader reader)
    {
        var packet = new ServerPlayerJoined();
        packet.Deserialize(reader);
        if (packet.PlayerId == LocalPlayerId) return;
        _remotePlayers[packet.PlayerId] = new RemotePlayer
        {
            Id = packet.PlayerId,
            Name = packet.PlayerName,
        };
        ModLog.Info($"[CairnMP] Player joined: [{packet.PlayerId}] {packet.PlayerName}");
        OnPlayerJoined?.Invoke(packet.PlayerId, packet.PlayerName);
    }

    private void HandlePlayerLeft(BinaryReader reader)
    {
        var packet = new ServerPlayerLeft();
        packet.Deserialize(reader);
        if (_remotePlayers.Remove(packet.PlayerId))
            ModLog.Info($"[CairnMP] Player left: {packet.PlayerId}");
        OnPlayerLeft?.Invoke(packet.PlayerId);
    }

    private void HandleStartGame(BinaryReader reader)
    {
        var packet = new ServerStartGame();
        packet.Deserialize(reader);
        var startMode = MultiplayerModes.Parse(packet.Mode);
        var startWorld = Enum.IsDefined(typeof(ClimbWorld), packet.World)
            ? (ClimbWorld)packet.World
            : ClimbWorld.Kami;
        ModLog.Debug($"[CairnMP] StartGame: mode={startMode.Name} world={ClimbWorlds.Name(startWorld)} " +
                     $"difficulty={(GameDifficulty)packet.Difficulty} skipTut={packet.SkipTutorials} skipPra={packet.SkipPractice} assist={packet.AssistEnabled}");
        OnStartGameReceived?.Invoke(packet);
    }

    private void HandleCurrentRun(BinaryReader reader)
    {
        HandleStartGame(reader);
    }
}
