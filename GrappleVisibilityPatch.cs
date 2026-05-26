using System;
using HarmonyLib;
using ProjectMage.character;
using ProjectMage.map.entities;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // Per-player grapple visibility.
    // Vanilla uses a single shared Grapple.visible / Grapple.visFrame for all
    // players. In splitscreen when P2 rolls near a stunned enemy, the shared
    // visFrame becomes >0 and BOTH players see the indicator. We track
    // per-player "can grapple" flags (set in GetGrapple) and animate our own
    // per-player visFrame arrays in Grapples.Update. DrawAllGrapples then
    // draws the indicator only for the player whose visFrame > 0.
    //
    // We also reset the shared Grapple.visible to false so vanilla Update
    // doesn't drive the shared visFrame (which we ignore during draw).
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Grapples), "GetGrapple")]
    private static void Grapples_GetGrapple_Postfix(Grapples __instance, Character character, ref int charIdx, bool __result)
    {
        if (!SplitActive) return;
        if (!__result) return;
        if (charIdx < 0) return; // not a char grapple

        int playerIdx = character.playerIdx;
        if (playerIdx == 0) _p1CanGrappleChar[charIdx] = true;
        else if (playerIdx == 1) _p2CanGrappleChar[charIdx] = true;

        // Reset shared visible so vanilla Update doesn't animate shared visFrame
        for (int i = 0; i < __instance.charGrappleCount; i++)
        {
            if (__instance.charGrapple[i].charIdx == charIdx)
            {
                __instance.charGrapple[i].visible = false;
                break;
            }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Grapples), "Update")]
    private static void Grapples_Update_Postfix(Grapples __instance, float frameTime)
    {
        if (!SplitActive) return;
        for (int i = 0; i < __instance.charGrappleCount; i++)
        {
            int charIdx = __instance.charGrapple[i].charIdx;
            if (charIdx < 0 || charIdx >= CharMgr.character.Length) continue;
            Character c = CharMgr.character[charIdx];
            if (c == null || !c.exists) continue;
            if (c.update.grappleStunFrame <= 0f) continue;

            // P1 visFrame
            if (_p1CanGrappleChar[charIdx])
            {
                _p1GrappleVisFrame[charIdx] += frameTime * 10f;
                if (_p1GrappleVisFrame[charIdx] > 1f) _p1GrappleVisFrame[charIdx] = 1f;
            }
            else
            {
                _p1GrappleVisFrame[charIdx] -= frameTime * 10f;
                if (_p1GrappleVisFrame[charIdx] < 0f) _p1GrappleVisFrame[charIdx] = 0f;
            }

            // P2 visFrame
            if (_p2CanGrappleChar[charIdx])
            {
                _p2GrappleVisFrame[charIdx] += frameTime * 10f;
                if (_p2GrappleVisFrame[charIdx] > 1f) _p2GrappleVisFrame[charIdx] = 1f;
            }
            else
            {
                _p2GrappleVisFrame[charIdx] -= frameTime * 10f;
                if (_p2GrappleVisFrame[charIdx] < 0f) _p2GrappleVisFrame[charIdx] = 0f;
            }
        }

        // Clear per-player flags after updating visFrame so they persist
        // from one frame's GetGrapple call to the next frame's Update.
        Array.Clear(_p1CanGrappleChar, 0, _p1CanGrappleChar.Length);
        Array.Clear(_p2CanGrappleChar, 0, _p2CanGrappleChar.Length);
    }
}
