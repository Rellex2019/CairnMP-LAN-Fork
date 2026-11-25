using System;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Networking;

internal interface INetworkTransportSession : IMultiplayerSession, IDisposable
{
    bool IsActive { get; }
    ulong LocalPeerId { get; set; }
    ulong HostPeerId { get; }
    string DisconnectReason { get; }
    void Send(ulong peerId, byte[] payload, bool reliable = true);
    void Broadcast(byte[] payload, bool reliable = true);
    void Pump();
    bool TryRead(out ulong peerId, out byte[] payload);
    bool ConsumeMembersChanged();
    bool IsPeerAllowed(ulong peerId);
    bool BroadcastStart(ServerStartGame start);
}
