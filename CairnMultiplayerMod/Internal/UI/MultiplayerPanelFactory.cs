using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Networking;
using CairnMultiplayerMod.Internal.UI.Sketch;
using CairnMultiplayerMod.Internal.UI.Toolkit;

namespace CairnMultiplayerMod.Internal.UI;

internal static class MultiplayerPanelFactory
{
    /// <param name="whyUnavailable">Why this player's game cannot open a destination, or null
    /// when it can. Asked of the game by the caller: the panels only put the answer on screen.</param>
    public static IMultiplayerPanel Create(IMultiplayerSession session, Func<ClimbWorld, string> whyUnavailable)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        if (whyUnavailable == null) throw new ArgumentNullException(nameof(whyUnavailable));
        try
        {
            var panel = new SketchMultiplayerPanel(session, whyUnavailable);
            ModLog.Info("[CairnMP] Sketch (native-style) multiplayer panel enabled with UI Toolkit fallback.");
            return new ResilientMultiplayerPanel(panel, () => new UiToolkitMultiplayerPanel(session, whyUnavailable));
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[CairnMP] Sketch panel unavailable, using UI Toolkit: {ex.Message}");
            return new UiToolkitMultiplayerPanel(session, whyUnavailable);
        }
    }
}
