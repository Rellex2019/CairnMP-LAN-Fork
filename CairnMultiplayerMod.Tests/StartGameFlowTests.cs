using System;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Game;
using CairnMultiplayerMod.Internal.Game.MainMenu;
using CairnMultiplayerMod.Internal.Networking;
using CairnMultiplayerMod.Internal.UI;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class StartGameFlowTests
{
    [Fact]
    public void MultiplayerLaunchOpensSaveSelectionBeforeStartingANewGame()
    {
        Assert.Equal(MainMenuInterop.MainMenuStep.StoryModeManageSave,
            StartGameFlow.MultiplayerLaunchEntryStep);
    }

    [Fact]
    public void ASummitLaunchTakesTheSameRoadAsTheSummitButton()
    {
        // A player who has never read the onboarding pages reads them first; the game hands
        // them over to the world selection on its own.
        Assert.Equal(MainMenuInterop.MainMenuStep.SummitModeOnboarding, SummitWorldInterop.EntryStep(false));
        Assert.Equal(MainMenuInterop.MainMenuStep.SummitModeWorldSelect, SummitWorldInterop.EntryStep(true));
    }

    [Fact]
    public void SummitStepsMatchCairnsMenuSteps()
    {
        Assert.Equal(6, (int)MainMenuInterop.MainMenuStep.SummitModeWorldSelect);
        Assert.Equal(28, (int)MainMenuInterop.MainMenuStep.SummitModeOnboarding);
    }

    [Fact]
    public void BeginPreparesTheNativeMenuBeforeForcingItsStep()
    {
        var panel = new Panel();
        var restores = 0;
        var flow = new StartGameFlow(panel, () => restores++, new RuntimeState());
        var start = new ServerStartGame
        {
            Difficulty = 3,
            SkipTutorials = true,
            SkipPractice = true,
            AssistEnabled = false,
            Mode = (byte)MultiplayerMode.RopeTeam,
            ExtraConstraints = 0,
            World = (int)ClimbWorld.Kami,
        };

        flow.Begin(start);

        Assert.Equal(1, restores);
    }

    [Fact]
    public void LastLaunchIsKeptForReturning()
    {
        var panel = new Panel();
        var flow = new StartGameFlow(panel, () => { }, new RuntimeState());
        var start = new ServerStartGame
        {
            Difficulty = 3,
            SkipTutorials = true,
            SkipPractice = true,
            AssistEnabled = false,
            Mode = (byte)MultiplayerMode.RopeTeam,
            ExtraConstraints = 0,
            World = (int)ClimbWorld.Kami,
        };

        flow.Begin(start);

        Assert.True(flow.HasLastStartGame);
        Assert.Equal(start.Mode, flow.LastStartGame.Value.Mode);
        Assert.Equal(start.World, flow.LastStartGame.Value.World);
        Assert.True(panel.CanReturnToGame);
    }

    [Fact]
    public void AReplayedCurrentRunDoesNotRestartAPendingLaunch()
    {
        var panel = new Panel();
        var flow = new StartGameFlow(panel, () => { }, new RuntimeState());
        var start = new ServerStartGame
        {
            Difficulty = 3,
            SkipTutorials = true,
            SkipPractice = true,
            AssistEnabled = false,
            Mode = (byte)MultiplayerMode.RopeTeam,
            ExtraConstraints = 0,
            World = (int)ClimbWorld.Kami,
        };

        flow.Begin(start);
        panel.Statuses.Clear();
        flow.JoinCurrentRun(start);

        Assert.Empty(panel.Statuses);
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
        public event Action OnPanelClosed { add {} remove {} }
        public bool IsVisible { get; private set; }
        public bool CanReturnToGame { get; private set; }
        public List<string> Statuses { get; } = new();
        public void Show() => IsVisible = true;
        public void Hide() => IsVisible = false;
        public void Toggle() => IsVisible = !IsVisible;
        public void SetStatus(string status, bool connected) => Statuses.Add(status);
        public void SetConnecting(string status) { }
        public void SetBrowserLobbies(IReadOnlyList<LobbyEntry> lobbies) { }
        public void SetCurrentLobbyName(string name) { }
        public void SetCanReturnToGame(bool canReturn) => CanReturnToGame = canReturn;
        public void SetSession(IMultiplayerSession session) { }
        public void Tick(float dt) { }
        public void OnGUI() { }
        public void DestroyResources() { IsVisible = false; }
    }
}
