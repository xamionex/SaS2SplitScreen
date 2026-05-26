using System;
using System.Reflection;
using Common;
using HarmonyLib;
using ProjectMage.character;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // MoveCoopPlayers - independent doors.
    private static readonly FieldInfo CharMovementCField = AccessTools.Field(typeof(CharMovement), "c");
    private static bool _coopLocSaved;
    private static Vector2 _savedCoopLoc;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CharMovement), "MoveCoopPlayers", typeof(int), typeof(Vector2))]
    private static void MoveCoopPlayers_Prefix(CharMovement __instance, ref Vector2 pLoc)
    {
        // Neuter the vanilla screen-edge snap barrier in splitscreen:
        // set pLoc (the "other player" position passed by the caller) to
        // this character's own position, making the barrier conditions
        // (this.c.loc.X > pLoc.X) and (this.c.loc.X < pLoc.X) both false.
        if (SplitActive && __instance != null)
        {
            Character c = CharMovementCField?.GetValue(__instance) as Character;
            if (c != null)
                pLoc = c.loc;
        }

        _coopLocSaved = false;

        if (!SplitActive && GlobalSettings.IndependentDoors?.Value != true) return;

        var coopPlayer = CoopPlayer();
        if (coopPlayer != null && coopPlayer.charIdx >= 0
                               && coopPlayer.charIdx < CharMgr.character.Length)
        {
            _savedCoopLoc = CharMgr.character[coopPlayer.charIdx].loc;
            _coopLocSaved = true;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CharMovement), "MoveCoopPlayers", typeof(int), typeof(Vector2))]
    private static void MoveCoopPlayers_Postfix(CharMovement __instance, int pLayer)
    {
        var thisChar = CharMovementCField?.GetValue(__instance) as Character;
        if (thisChar == null) return;

        var mainPlayer = MainPlayer();
        if (mainPlayer == null || mainPlayer.charIdx < 0
                               || mainPlayer.charIdx >= CharMgr.character.Length) return;

        var coopPlayer = CoopPlayer();
        if (coopPlayer == null || coopPlayer.charIdx < 0
                               || coopPlayer.charIdx >= CharMgr.character.Length) return;

        bool isP1 = ReferenceEquals(thisChar, CharMgr.character[mainPlayer.charIdx]);

        if (isP1)
        {
            int p1Layer = CharCols.GetLayer(thisChar.loc + new Vector2(0f, -10f));
            bool changedLayer = p1Layer != pLayer;

            if (changedLayer && GlobalSettings.IndependentDoors?.Value == true && _coopLocSaved)
                CharMgr.character[coopPlayer.charIdx].loc = _savedCoopLoc;
        }
    }
}
