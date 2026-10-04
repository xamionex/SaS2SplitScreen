using System.Reflection;
using Common;
using HarmonyLib;
using ProjectMage.character;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // MoveCoopPlayers - independent doors.
    // Vanilla local coop drags the OTHER player onto the mover whenever their tile layers differ (CharMovement.MoveCoopPlayers).
    // That is the mechanism that made BOTH players cross the cave threshold in the minimal TransitionPatch-only test: P1 entered, vanilla teleported P2 onto P1, so P2's own camera transitioned and played its own transition.
    // In splitscreen each player must stay independent:
    //   - rewrite pLayer to the mover's current layer so the vanilla "layer != pLayer" tether branch never fires,
    //   - rewrite pLoc to the mover's own position so the screen-edge snap barrier never fires.
    private static readonly FieldInfo CharMovementCField = AccessTools.Field(typeof(CharMovement), "c");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CharMovement), "MoveCoopPlayers", typeof(int), typeof(Vector2))]
    // ReSharper disable once InconsistentNaming
    private static void MoveCoopPlayers_Prefix(CharMovement __instance, ref int pLayer, ref Vector2 pLoc)
    {
        if (!SplitActive || __instance == null) return;

        if (CharMovementCField?.GetValue(__instance) is not Character c) return;

        pLoc = c.loc;
        pLayer = CharCols.GetLayer(c.loc + new Vector2(0f, -10f));
    }
}