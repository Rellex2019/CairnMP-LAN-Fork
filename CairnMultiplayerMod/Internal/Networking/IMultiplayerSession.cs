using System.Collections.Generic;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Networking;

internal interface IMultiplayerSession
{
    bool IsInLobby { get; }
    bool IsHost { get; }
    string CurrentRoomCode { get; }
    string CurrentLobbyName { get; }
    string LocalPersonaName { get; }
    int MaxMembers { get; }
    IReadOnlyList<LobbyMember> Members { get; }
    MultiplayerModeRules CurrentModeRules { get; }
    ClimbWorld CurrentWorld { get; }
    void PublishWorldAvailability(bool available);
}
