using System;
using Common;
using HarmonyLib;
using ProjectMage.player;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // Update P2's CamMgr so layer/indoors reflect P2's location.
    // Player.Update only calls camMgr.Update when this.ID == 0 (P1 only).
    // Without this, P2's camMgr has stale default values (curLayer=0,
    // indoors=0 = outdoor), so cave rendering never works for P2.
    //
    // We save/restore ScrollManager state because P2's camMgr.Update overwrites
    // the shared scroll/zoom/cannedDepth. If we leave it at P2's camera,
    // particle spawning (MageParticles.UpdateBaseParticles) and other
    // screen-dependent effects fail for entities near P1.
    private static Vector2 _preP2Scroll;
    private static float _preP2Zoom;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), "Update")]
    private static void Player_Update_Prefix(Player __instance, float frameTime, float realTime)
    {
        if (__instance.ID == 1 && IsLocalCoop() && SplitActive)
        {
            _preP2Scroll = ScrollManager.scroll;
            _preP2Zoom = ScrollManager.zoom;
            __instance.camMgr.Update(frameTime, realTime);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), "Update")]
    private static void Player_Update_Postfix(Player __instance)
    {
        if (__instance.ID == 1 && IsLocalCoop() && SplitActive)
        {
            ScrollManager.scroll = _preP2Scroll;
            ScrollManager.zoom = _preP2Zoom;
            ScrollManager.UpdateCannedValues();
        }
    }
}
