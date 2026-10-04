using HarmonyLib;
using ProjectMage.map.arena;

namespace SaS2SplitScreen;

// ============================================================================
// Suppress the vanilla fullscreen area banner in splitscreen.
//
// The F9 debug-trigger test proved the layer transition itself does NOT leak: forcing the transition while both players stand still flashes only P1's half.
// But WALKING into/out of a cave makes the black circle appear on BOTH halves.
// The difference is crossing an area boundary, which fires the vanilla MapRegions banner: a giant black oval + area name drawn FULLSCREEN, once per frame, AFTER both halves are composited (PlayerMgr.Draw -> mapMgr.regions.Draw).
// The split composite cannot separate it - both halves always see it.
//
// While splitscreen is active we suppress the vanilla fullscreen draw.
// The banner is position-driven and cosmetic; losing it in splitscreen is acceptable (each half keeps showing its own player's world).
// ============================================================================

internal static partial class SplitscreenPatch
{
    private static bool _regionSuppressLogged;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MapRegions), "Draw")]
    private static bool MapRegions_Draw_Prefix()
    {
        if (!SplitActive) return true;

        if (_regionSuppressLogged) return false;
        _regionSuppressLogged = true;
        Log("[Splitscreen] Region banner: vanilla fullscreen draw suppressed (it would cover both halves).");

        return false;
    }
}