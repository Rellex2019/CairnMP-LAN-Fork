using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Scopes direct-rope physics. The Lifeline is deliberately left alone: Cairn only counts
/// quickdraws on its securing rope as protection, and whatever hears "secured" then asks for
/// the last piton. A rope between two harnesses has none to give.
/// </summary>
internal static class RopeTeamFallPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.RopeTeam");
    private static bool _installed;
    private static bool _belayHooksLogged;
    private static bool _redirectingFall;
    private static MethodInfo _toFallingSecure;
    internal static bool IsInstalled => _installed;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            var fixedUpdate = AccessTools.Method(typeof(LogicalRope), nameof(LogicalRope.FixedUpdate));
            Harmony.Patch(fixedUpdate,
                prefix: new HarmonyMethod(typeof(RopeTeamFallPatch), nameof(BeforePhysics)),
                postfix: new HarmonyMethod(typeof(RopeTeamFallPatch), nameof(AfterPhysics)));
            var lateUpdate = AccessTools.Method(typeof(LogicalRopeRenderer), nameof(LogicalRopeRenderer.LateUpdate));
            Harmony.Patch(lateUpdate, postfix: new HarmonyMethod(typeof(RopeTeamFallPatch), nameof(AfterRender)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[RopeTeam] Direct-rope hooks unavailable: " + ex.Message);
            return;
        }

        _toFallingSecure = AccessTools.Method(typeof(ClimbingV2PawnController), "ToFallingModuleSecure",
            new[] { typeof(FallReason), typeof(Il2CppSystem.Nullable<Vector3>), typeof(AnimationClip) });
        PatchOptional("unsecure fall",
            AccessTools.Method(typeof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.ToFallingModuleUnsecure)),
            nameof(BeforeToFallingModuleUnsecure));
        PatchOptional("securing rope",
            AccessTools.Method(typeof(ClimbingV2PawnControllerModuleSecuredFalling), "get_SecuringRope", Type.EmptyTypes),
            nameof(BeforeGetSecuringRope));
        PatchOptional("next piton",
            AccessTools.Method(typeof(ClimbingV2PawnControllerModuleSecuredFalling), "CheckNextPiton", Type.EmptyTypes),
            nameof(BeforeCheckNextPiton));
        PatchOptional("should snap",
            AccessTools.Method(typeof(ClimbingV2PawnControllerModuleSecuredFalling), "ShouldSnap", new[] { typeof(Piton) }),
            nameof(BeforeShouldSnap));
        PatchOptional("harness attach",
            AccessTools.Method(typeof(ClimbingV2PawnControllerModuleSecuredFalling), "get_HarnessAttachPos", Type.EmptyTypes),
            nameof(BeforeGetHarnessAttachPos));
    }

    private static void PatchOptional(string label, MethodInfo method, string prefix)
    {
        if (method == null)
        {
            ModLog.Warning("[RopeTeam] Optional hook unavailable: " + label);
            return;
        }
        try { Harmony.Patch(method, prefix: new HarmonyMethod(typeof(RopeTeamFallPatch), prefix)); }
        catch (Exception ex) { ModLog.Warning($"[RopeTeam] Optional hook failed ({label}): {ex.Message}"); }
    }

    public static void Uninstall()
    {
        RopeInterop.ReleaseAllAnchors();
        Harmony.UnpatchSelf();
        _installed = false;
    }

    private static bool BeforePhysics(LogicalRope __instance) => RopeInterop.BeforeRopePhysics(__instance);

    private static void AfterPhysics(LogicalRope __instance) => RopeInterop.AfterRopePhysics(__instance);

    private static void AfterRender(LogicalRopeRenderer __instance) => RopeInterop.AfterRopeRendered(__instance);

    private static bool BeforeToFallingModuleUnsecure(
        ClimbingV2PawnController __instance,
        FallReason reason,
        Il2CppSystem.Nullable<Vector3> retroactiveFallStartPos,
        AnimationClip animationClipToInfluenceFallPuppet)
    {
        if (_redirectingFall || _toFallingSecure == null || !RopeInterop.IsNativeBelayEngaged) return true;
        if (!RopeInterop.TryGetDirectRopeHarnesses(out var local, out _)) return true;
        if (__instance.harness == null || __instance.harness.Pointer != local.Pointer) return true;

        _redirectingFall = true;
        try
        {
            RopeInterop.TightenRopeForFall(__instance.harness.Pointer);
            _toFallingSecure.Invoke(__instance,
                new object[] { reason, retroactiveFallStartPos, animationClipToInfluenceFallPuppet });
            return false;
        }
        catch (Exception ex)
        {
            ModLog.Warning("[RopeTeam] Could not enter a secured fall: " + ex.Message);
            return true;
        }
        finally { _redirectingFall = false; }
    }

    private static bool BeforeGetSecuringRope(
        ClimbingV2PawnControllerModuleSecuredFalling __instance,
        ref LogicalRope __result)
    {
        if (!RopeInterop.TryGetDirectBelay(out var rope, out var local, out _)) return true;
        var controller = __instance.pawnController;
        if (controller == null || controller.harness == null || controller.harness.Pointer != local.Pointer)
            return true;
        if (!_belayHooksLogged)
        {
            _belayHooksLogged = true;
            ModLog.Info("[RopeTeam] Native secured-fall hooks active on direct rope");
        }
        __result = rope;
        return false;
    }

    private static bool BeforeCheckNextPiton(ClimbingV2PawnControllerModuleSecuredFalling __instance)
    {
        if (!RopeInterop.TryGetDirectRopeHarnesses(out var local, out _)) return true;
        var controller = __instance.pawnController;
        if (controller == null || controller.harness == null || controller.harness.Pointer != local.Pointer)
            return true;

        __instance.willNextPitonFail = false;
        __instance.partCountToConsider = 0;
        local.SetNextPitonWillSnap(false);
        return false;
    }

        private static bool BeforeShouldSnap(
            ClimbingV2PawnControllerModuleSecuredFalling __instance,
            Piton piton,
            ref bool __result)
        {
            if (!RopeInterop.TryGetDirectRopeHarnesses(out var local, out _)) return true;
            var controller = __instance.pawnController;
            if (controller == null || controller.harness == null || controller.harness.Pointer != local.Pointer)
                return true;

            __result = false;
            return false;
        }

        private static bool BeforeGetHarnessAttachPos(
            ClimbingV2PawnControllerModuleSecuredFalling __instance,
        ref Vector3 __result)
    {
        if (!RopeInterop.TryGetDirectRopeHarnesses(out var local, out var partner)) return true;
        var controller = __instance.pawnController;
        if (controller == null || controller.harness == null || controller.harness.Pointer != local.Pointer)
            return true;

        var pos = partner.GetAttachPosition();
        if (!float.IsFinite(pos.x) || !float.IsFinite(pos.y) || !float.IsFinite(pos.z)) return true;
        __result = pos;
        return false;
    }
}
