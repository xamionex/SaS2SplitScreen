using System;
using System.Reflection;
using Common;
using HarmonyLib;
using ProjectMage.gamestate;

namespace SaS2SplitScreen;

/// <summary>
///     Splitscreen audio fixes.
///     In normal play both Sound.GetPan and Sound.GetVol use ScrollManager.midReal as the listener center (the midpoint
///     camera).
///     When players separate far enough in splitscreen, sounds near P1 are panned/attenuated as if nobody is listening -
///     this is audible as sound dropouts and incorrect stereo placement.
///     These patches replace midReal with the nearest player's world position
///     so that:
///     * GetPan  - stereo pan is relative to whichever player is closer.
///     * GetVol  - a sound within either player's camera range is full volume;
///     sounds outside both views attenuate from the nearest player.
///     Both patches are no-ops when splitscreen is inactive.
/// </summary>

// -- GetPan --------------------------------------------------------------------
[HarmonyPatch]
internal static class SoundGetPanSplitPatch
{
    [HarmonyTargetMethod]
    private static MethodBase Target()
    {
        return AccessTools.Method(
            AccessTools.TypeByName("ProjectMage.sfx.Sound"), "GetPan",
            [typeof(Vector2)]);
    }

    /// Replaces the midpoint-camera pan with a nearest-player pan.
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void Postfix(Vector2 loc, ref float __result)
    {
        if (GameState.state != 1) return;
        if (!SplitscreenPatch.HasP2) return;

        var halfWorld = ScrollManager.maxReal.X - ScrollManager.midReal.X;
        if (halfWorld <= 0f) return;

        // Choose the player whose camera centre is closest to the sound source
        var dist1 = Math.Abs(loc.X - SplitscreenPatch.P1Loc.X);
        var dist2 = Math.Abs(loc.X - SplitscreenPatch.P2Loc.X);
        var nearestCentreX = dist1 <= dist2
            ? SplitscreenPatch.P1Loc.X
            : SplitscreenPatch.P2Loc.X;

        __result = Math.Max(-1f, Math.Min(1f,
            (loc.X - nearestCentreX) / halfWorld));
    }
}

// -- GetVol --------------------------------------------------------------------
[HarmonyPatch]
internal static class SoundGetVolSplitPatch
{
    [HarmonyTargetMethod]
    private static MethodBase Target()
    {
        return AccessTools.Method(
            AccessTools.TypeByName("ProjectMage.sfx.Sound"), "GetVol",
            [typeof(Vector2)]);
    }

    /// Returns full volume when the sound is in either player's camera range;
    /// attenuates from the nearest player otherwise (same 1/1000 slope as vanilla).
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void Postfix(Vector2 loc, ref float __result)
    {
        if (GameState.state != 1) return;
        if (!SplitscreenPatch.HasP2) return;
        if (__result >= 1f) return; // already within midpoint view, leave it

        // Half-extents of one player's full camera view in world units
        var halfX = ScrollManager.maxReal.X - ScrollManager.midReal.X;
        var halfY = ScrollManager.maxReal.Y - ScrollManager.midReal.Y;

        // Full volume if inside either player's camera
        var inP1 = Math.Abs(loc.X - SplitscreenPatch.P1Loc.X) <= halfX
                   && Math.Abs(loc.Y - SplitscreenPatch.P1Loc.Y) <= halfY;
        var inP2 = Math.Abs(loc.X - SplitscreenPatch.P2Loc.X) <= halfX
                   && Math.Abs(loc.Y - SplitscreenPatch.P2Loc.Y) <= halfY;

        if (inP1 || inP2)
        {
            __result = 1f;
            return;
        }

        // Outside both cameras: attenuate from the nearest camera edge
        // (same 1/1000 world-unit falloff the vanilla code uses)
        const float falloff = 1000f;

        var p1DistX = Math.Max(0f, Math.Abs(loc.X - SplitscreenPatch.P1Loc.X) - halfX);
        var p1DistY = Math.Max(0f, Math.Abs(loc.Y - SplitscreenPatch.P1Loc.Y) - halfY);
        var p2DistX = Math.Max(0f, Math.Abs(loc.X - SplitscreenPatch.P2Loc.X) - halfX);
        var p2DistY = Math.Max(0f, Math.Abs(loc.Y - SplitscreenPatch.P2Loc.Y) - halfY);

        var vol1 = 1f - (p1DistX + p1DistY) / falloff;
        var vol2 = 1f - (p2DistX + p2DistY) / falloff;
        __result = Math.Max(0f, Math.Max(vol1, vol2));
    }
}