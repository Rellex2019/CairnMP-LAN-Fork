using System;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Networking;
using CairnMultiplayerMod.Internal.UI;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PanelRegressionTests
{
    [Fact]
    public void NativeCloseUpdatesWrapperAndToggleReopens()
    {
        var primary = new Panel();
        var wrapper = new ResilientMultiplayerPanel(primary, () => new Panel());
        wrapper.Show();
        primary.Close();
        Assert.False(wrapper.IsVisible);
        wrapper.Toggle();
        Assert.True(wrapper.IsVisible);
        Assert.True(primary.IsVisible);
    }

    [Fact]
    public void FallbackDestroysAndUnsubscribesPrimaryWhilePreservingState()
    {
        var primary = new Panel();
        var fallback = new Panel();
        var wrapper = new ResilientMultiplayerPanel(primary, () => fallback);
        wrapper.Show();
        wrapper.SetCurrentLobbyName("Expedition");
        wrapper.SetCanReturnToGame(true);
        var session = new Session();
        wrapper.SetSession(session);
        primary.FailTick = true;
        wrapper.Tick(1);
        Assert.True(primary.Destroyed);
        Assert.True(fallback.IsVisible);
        Assert.Equal("Expedition", fallback.LobbyName);
        Assert.True(fallback.CanReturnToGame);
        Assert.Same(session, fallback.Session);
        primary.Close();
        Assert.True(wrapper.IsVisible);
        fallback.Close();
        Assert.False(wrapper.IsVisible);
    }

    private sealed class Panel : IMultiplayerPanel
    {
        public event Action<HostConfig> OnHostRequested { add {} remove {} }
        public event Action<string> OnJoinByCodeRequested { add {} remove {} }
        public event Action OnBrowseRequested { add {} remove {} }
        public event Action<ulong> OnJoinByLobbyIdRequested { add {} remove {} }
        public event Action OnDisconnectRequested { add {} remove {} }
        public event Action OnStartRequested { add {} remove {} }
        public event Action OnReturnToGameRequested { add {} remove {} }
        public event Action<DirectConnectConfig> OnJoinDirectRequested { add {} remove {} }
        public event Action OnPanelClosed;
        public bool IsVisible { get; private set; }
        internal bool FailTick, Destroyed, CanReturnToGame;
        internal string LobbyName;
        internal IMultiplayerSession Session;
        public void Show() => IsVisible = true;
        public void Hide() => IsVisible = false;
        public void Toggle() => IsVisible = !IsVisible;
        public void SetStatus(string status, bool connected) {}
        public void SetConnecting(string status) {}
        public void SetBrowserLobbies(IReadOnlyList<LobbyEntry> lobbies) {}
        public void SetCurrentLobbyName(string name) => LobbyName = name;
        public void SetCanReturnToGame(bool canReturn) => CanReturnToGame = canReturn;
        public void SetSession(IMultiplayerSession session) => Session = session;
        public void Tick(float dt) { if (FailTick) throw new InvalidOperationException("UI unavailable"); }
        public void OnGUI() {}
        public void DestroyResources() { Destroyed = true; IsVisible = false; }
        internal void Close() { IsVisible = false; OnPanelClosed?.Invoke(); }
    }

    private sealed class Session : IMultiplayerSession
    {
        public bool IsInLobby => true;
        public bool IsHost => true;
        public string CurrentRoomCode => "14000";
        public string CurrentLobbyName => "Test";
        public string LocalPersonaName => "Tester";
        public int MaxMembers => 2;
        public IReadOnlyList<LobbyMember> Members => Array.Empty<LobbyMember>();
        public MultiplayerModeRules CurrentModeRules => MultiplayerModes.RopeTeam;
        public ClimbWorld CurrentWorld => ClimbWorld.Kami;
        public void PublishWorldAvailability(bool available) {}
    }
}
