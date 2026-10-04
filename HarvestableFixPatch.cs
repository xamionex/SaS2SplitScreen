using Bestiary.monsters;
using HarmonyLib;
using ProjectMage.character;
using ProjectMage.player;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // When UseCharacter handles a MonsterHarvest (type 5) that has no flags (flag 1 / 2 = mage/side clue), the vanilla code falls through to the default "activate" animation instead of animName+"_a" (= "material_a").
    // The harvest-script command (97) that calls CharHarvest.DoHarvest lives only in the "material_a" animation, so the reward never spawns.
    // Force material_a for all flagless harvestable(s) so DoHarvest fires.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerPrompts), "UseCharacter", typeof(Player), typeof(Character))]
    private static void UseCharacter_Prefix(Player player)
    {
        if (player == null || !SplitActive) return;
        var c = GetCharacter(player);
        if (c == null) return;
        SetScroll(c.loc + BaseCamOffset);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerPrompts), "UseCharacter", typeof(Player), typeof(Character))]
    private static void UseCharacter_Postfix(Player player, Character other)
    {
        if (other == null || other.monsterIdx < 0 || !SplitActive) return;
        var mDef = MonsterCatalog.monsterDef[other.monsterIdx];
        if (mDef is not { type: 5 }) return;
        if (mDef.flags != null && (mDef.flags.Contains(1) || mDef.flags.Contains(2))) return;
        CharAnimSetAnim(other.anim, other.anim.animName + "_a", false, true);
    }

    private static void CharAnimSetAnim(object anim, string newAnim, bool overRide, bool resetFields)
    {
        if (CharAnimSetAnimMethod == null) return;
        CharAnimSetAnimMethod.Invoke(anim, [newAnim, overRide, resetFields]);
    }
}