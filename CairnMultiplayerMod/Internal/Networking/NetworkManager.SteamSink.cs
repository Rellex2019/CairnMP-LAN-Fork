using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Networking.Authoritative;

namespace CairnMultiplayerMod.Internal.Networking;

/// <summary>
/// Transport adapter for authoritative services. This is the only translation between stable
/// player ids, transport peer ids and transport reliability.
/// </summary>
internal sealed partial class NetworkManager
{
    private IAuthoritativeSink _transportSink;

    public IAuthoritativeSink SteamSink => _transportSink ??= new TransportAuthoritativeSink(this);

    private PitonAuthority _pitonAuthority;

    private PitonAuthority Pitons => _pitonAuthority ??= new PitonAuthority(
        SteamSink,
        pkt => OnPitonPlaced?.Invoke(pkt),
        pkt => OnPitonRemoved?.Invoke(pkt),
        clock: null,
        onLocalPitonRecovered: localId => OnOwnPitonRecovered?.Invoke(localId));

    private sealed class TransportAuthoritativeSink : IAuthoritativeSink
    {
        private readonly NetworkManager _t;

        public TransportAuthoritativeSink(NetworkManager transport) => _t = transport;

        public IReadOnlyCollection<int> ConnectedPlayerIds => _t._steamIdsByPlayerId.Keys;

        public void SendTo(int playerId, PacketId id, IPacket packet, NetReliability reliability)
        {
            if (!_t._steamIdsByPlayerId.TryGetValue(playerId, out var peerId)) return;
            if (peerId == 0 || peerId == _t.LocalPeerId) return;
            _t.SendPayload(peerId, BuildPayload(id, packet),
                reliability == NetReliability.ReliableOrdered);
        }

        public void Broadcast(PacketId id, IPacket packet, int exceptPlayerId, NetReliability reliability)
        {
            ulong exceptPeerId = 0;
            if (exceptPlayerId != 0)
                _t._steamIdsByPlayerId.TryGetValue(exceptPlayerId, out exceptPeerId);
            _t.BroadcastServerPacket(id, packet, exceptPeerId,
                reliability == NetReliability.ReliableOrdered);
        }
    }
}
