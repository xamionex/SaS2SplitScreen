using System;
using Bestiary.monsters;
using Common;
using HarmonyLib;
using ProjectMage.character;
using ProjectMage.player;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // Interaction-prompt barrier fix.
    // PlayerPrompts.Update evaluates visibility using ScrollManager.scroll (camera midpoint).
    // When P1 is far from P2 in splitscreen, entities near P1 appear off-screen from the midpoint.
    // Prompts vanish, and interactions fail.
    // These prefixes set scroll to the owning player's camera position before any prompt logic runs.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerPrompts), "Update", typeof(Character))]
    private static void PlayerPrompts_Update_Prefix(Character c)
    {
        if (c == null || !SplitActive) return;
        SetScroll(c.loc + BaseCamOffset);
        SaveAllRects();
        CharMgr.UpdateLists();

        if (!HasP2) return;
        var p1Char = GetCharacter(MainPlayer());
        if (p1Char == null) return;
        var dst = c.ID == p1Char.ID ? P1CharsBackup : P2CharsBackup;
        ref var count = ref c.ID == p1Char.ID ? ref _p1CharsCount : ref _p2CharsCount;
        count = Math.Min(CharMgr.activeChars.total, 320);
        Array.Copy(CharMgr.activeChars.list, dst, count);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerPrompts), "Update", typeof(Character))]
    private static void PlayerPrompts_Update_Postfix_RestoreRects(Character c)
    {
        if (c == null || !SplitActive) return;
        RestoreAllRects();
    }

    // Fix drawVec.Y for harvestable characters whose rects.topVal.Y is -1, meaning they were not rendered before PlayerPrompts.Update in this split-screen frame.
    // Uses the last valid topVal cached by CharRects_Reset_Prefix, saved from the previous draw pass, falling back to loc.Y - boxHeight on first frame.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerPrompts), "Update", typeof(Character))]
    // ReSharper disable once InconsistentNaming
    private static void PlayerPrompts_Update_Postfix_FixY(PlayerPrompts __instance, Character c)
    {
        if (!SplitActive) return;
        if (!__instance.drawActive) return;
        if (__instance.drawVec.Y >= 0f) return;

        if (GetCharUseMethod == null) return;
        var charIdx = (int)GetCharUseMethod.Invoke(__instance, [c]);
        if (charIdx < 0) return;

        var target = CharMgr.character[charIdx];
        if (target is not { exists: true }) return;

        var def = MonsterCatalog.monsterDef[target.monsterIdx];
        if (def == null) return;

        if (CachedTopY.TryGetValue(target.ID, out var cachedY))
        {
            __instance.drawVec = new Vector2(__instance.drawVec.X, cachedY);
            return;
        }

        __instance.drawVec = new Vector2(__instance.drawVec.X,
            target.loc.Y - def.boxHeight);
    }

    // Save topVal.Y for harvestable characters before CharRects.Clear() wipes it at the start of each draw pass.
    // In splitscreen, P2's draw pass clears topVal for P1-only characters, losing the correct value.
    // This prefix preserves it so PlayerPrompts_Update_Postfix_FixY can read it later.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CharRects), "Clear")]
    // ReSharper disable once InconsistentNaming
    private static void CharRects_Reset_Prefix(CharRects __instance)
    {
        if (!SplitActive) return;
        if (CharRectsCharacterField == null) return;
        var ch = (Character)CharRectsCharacterField.GetValue(__instance);
        if (ch == null || ch.monsterIdx < 0) return;
        var def = MonsterCatalog.monsterDef[ch.monsterIdx];
        if (def == null) return;
        if (__instance.topVal.Y >= 0f) CachedTopY[ch.ID] = __instance.topVal.Y;
    }
}