using System;
using HarmonyLib;
using ProjectMage.character;
using ProjectMage.player;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // Merge activeChars from both players' views after their PlayerPrompts prefixes have run.
    // Without this, CharMgr.Update only sees P2's nearby characters and drops P1-only entities from script processing.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerMgr), "Update")]
    [HarmonyPriority(Priority.High)]
    private static void PlayerMgr_Update_ActiveChars_Postfix()
    {
        if (!SplitActive) return;
        if (!HasP2) return;

        var before = CharMgr.activeChars.total;

        for (var i = 0; i < _p1CharsCount; i++)
        {
            var idx = P1CharsBackup[i];
            if (!CharMgr.activeChars.Contains(idx))
                CharMgr.activeChars.Set(idx);
        }

        for (var i = 0; i < _p2CharsCount; i++)
        {
            var idx = P2CharsBackup[i];
            if (!CharMgr.activeChars.Contains(idx))
                CharMgr.activeChars.Set(idx);
        }

        var after = CharMgr.activeChars.total;
        if (after == before) return;
        
        var now = Environment.TickCount;
        if (now - _lastActiveCharsLogTick <= 500) return;
        _lastActiveCharsLogTick = now;
        Log($"[Splitscreen] Merged {after - before} chars into activeChars (total={after}, p1Count={_p1CharsCount}, p2Count={_p2CharsCount})");
    }
}