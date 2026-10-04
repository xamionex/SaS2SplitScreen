using System;
using HarmonyLib;
using ProjectMage.character;
using ProjectMage.gamestate;
using ProjectMage.player;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // Rebuild char grapples from merged activeChars.
    // CharMgr.UpdateLists() resets char grapples to the current scroll view.
    // In splitscreen it runs twice per frame (once per player prefix), so only P2's stun enemies survive into CharMgr.Update() / GetGrapple.
    // We rebuild charGrapple from the merged activeChars so BOTH players can grapple stun enemies near them.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerMgr), "Update")]
    [HarmonyPriority(Priority.Last)]
    private static void PlayerMgr_Update_GrappleRebuild_Postfix()
    {
        if (!SplitActive) return;

        _mergedActiveCharsCount = CharMgr.activeChars.total;
        Array.Copy(CharMgr.activeChars.list, MergedActiveChars, _mergedActiveCharsCount);

        var grapples = GameSessionMgr.gameSession.mapMgr.entityMgr.grapples;
        grapples.ResetCharGrapples();
        for (var i = 0; i < CharMgr.activeChars.total; i++)
        {
            var idx = CharMgr.activeChars.list[i];
            if (idx < 0 || idx >= CharMgr.character.Length) continue;
            var c2 = CharMgr.character[idx];
            if (c2 is not { exists: true }) continue;
            if (c2.update.grappleStunFrame <= 0f) continue;
            if (grapples.charGrappleCount >= 8) break;

            var g = grapples.charGrapple[grapples.charGrappleCount];
            g.charIdx = idx;
            g.point = GetCharGrappleHeadPos(c2);
            g.x = -1;
            g.y = -1;
            g.col = 20;
            g.frame = 0f;
            g.visible = false;
            g.visFrame = 0f;
            grapples.charGrappleCount++;
        }
    }
}