using System;
using Common;
using HarmonyLib;
using ProjectMage.character;
using ProjectMage.gamestate;
using ProjectMage.map.entities;
using ProjectMage.player;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // Per-player grapple visibility.
    // Vanilla uses a single shared Grapple.visible / Grapple.visFrame for all players.
    // In splitscreen when P2 rolls near a stunned enemy, the shared visFrame becomes >0 and BOTH players see the indicator.
    // We track per-player "can grapple" flags (set in GetGrapple) and animate our own per-player visFrame arrays in Grapples.Update.
    // DrawAllGrapples then draws the indicator only for the player whose visFrame > 0.
    //
    // We also reset the shared Grapple.visible to false so vanilla Update doesn't drive the shared visFrame (which we ignore during draw).
    //
    // For fixed grapple points (Grapples.grapple[], charIdx < 0) we also animate per-player visFrame arrays indexed by slot in grapple[], because vanilla Grapple.Update hard-resets visFrame to 0 for any point outside ScrollManager.scroll (P1's camera), making the ring indicator disappear for P2 when P1 moves away.
    // The growth predicate is CanGrapplePoint, which replicates Grapples.GetGrapple's fixed-point branch (800x600, min 10 above, facing, layer, ability).
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Grapples), "GetGrapple")]
    // ReSharper disable InconsistentNaming
    private static void Grapples_GetGrapple_Postfix(Grapples __instance, Character character, ref int charIdx,
        bool __result)
    {
        // ReSharper restore InconsistentNaming
        if (!SplitActive) return;
        if (!__result) return;
        if (charIdx < 0) return; // not a char grapple

        var playerIdx = character.playerIdx;
        switch (playerIdx)
        {
            case 0:
                P1CanGrappleChar[charIdx] = true;
                break;
            case 1:
                P2CanGrappleChar[charIdx] = true;
                break;
        }

        // Reset shared visible so vanilla Update doesn't animate shared visFrame
        for (var i = 0; i < __instance.charGrappleCount; i++)
            if (__instance.charGrapple[i].charIdx == charIdx)
            {
                __instance.charGrapple[i].visible = false;
                break;
            }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Grapples), "Update")]
    // ReSharper disable once InconsistentNaming
    private static void Grapples_Update_Postfix(Grapples __instance, float frameTime)
    {
        if (!SplitActive) return;

        // Fixed grapple points: per-player visFrame animated by a direct CanGrapplePoint check against each player.
        // Indexed by slot in grapple[] (stable within a map session).
        var p1Char = GetCharacter(MainPlayer());
        var p2Char = HasP2 ? GetCharacter(CoopPlayer()) : null;
        for (var i = 0; i < __instance.grappleCount; i++)
        {
            var g = __instance.grapple[i];
            var p1Can = p1Char != null && CanGrapplePoint(p1Char, g.point, g.col);
            var p2Can = p2Char != null && CanGrapplePoint(p2Char, g.point, g.col);

            if (p1Can)
            {
                P1GrapplePtVisFrame[i] += frameTime * 10f;
                if (P1GrapplePtVisFrame[i] > 1f) P1GrapplePtVisFrame[i] = 1f;
            }
            else
            {
                P1GrapplePtVisFrame[i] -= frameTime * 10f;
                if (P1GrapplePtVisFrame[i] < 0f) P1GrapplePtVisFrame[i] = 0f;
            }

            if (p2Can)
            {
                P2GrapplePtVisFrame[i] += frameTime * 10f;
                if (P2GrapplePtVisFrame[i] > 1f) P2GrapplePtVisFrame[i] = 1f;
            }
            else
            {
                P2GrapplePtVisFrame[i] -= frameTime * 10f;
                if (P2GrapplePtVisFrame[i] < 0f) P2GrapplePtVisFrame[i] = 0f;
            }
        }

        // Char grapples: per-player visFrame animated by GetGrapple flags.
        for (var i = 0; i < __instance.charGrappleCount; i++)
        {
            var charIdx = __instance.charGrapple[i].charIdx;
            if (charIdx < 0 || charIdx >= CharMgr.character.Length) continue;
            var c = CharMgr.character[charIdx];
            if (c is not { exists: true }) continue;
            if (c.update.grappleStunFrame <= 0f) continue;

            // P1 visFrame
            if (P1CanGrappleChar[charIdx])
            {
                P1GrappleVisFrame[charIdx] += frameTime * 10f;
                if (P1GrappleVisFrame[charIdx] > 1f) P1GrappleVisFrame[charIdx] = 1f;
            }
            else
            {
                P1GrappleVisFrame[charIdx] -= frameTime * 10f;
                if (P1GrappleVisFrame[charIdx] < 0f) P1GrappleVisFrame[charIdx] = 0f;
            }

            // P2 visFrame
            if (P2CanGrappleChar[charIdx])
            {
                P2GrappleVisFrame[charIdx] += frameTime * 10f;
                if (P2GrappleVisFrame[charIdx] > 1f) P2GrappleVisFrame[charIdx] = 1f;
            }
            else
            {
                P2GrappleVisFrame[charIdx] -= frameTime * 10f;
                if (P2GrappleVisFrame[charIdx] < 0f) P2GrappleVisFrame[charIdx] = 0f;
            }
        }

        // Clear per-player flags after updating visFrame so they persist from one frame's GetGrapple call to the next frame's Update.
        Array.Clear(P1CanGrappleChar, 0, P1CanGrappleChar.Length);
        Array.Clear(P2CanGrappleChar, 0, P2CanGrappleChar.Length);
    }

    // Replicates the fixed-point branch of Grapples.GetGrapple's eligibility check for a single grapple point against a single player.
    // Returns true when the player is in range, facing the point, on the same layer, and the grapple ability is unlocked.
    // Used to drive per-player visFrame growth for the ring indicator so it appears for each player independently of the shared ScrollManager.scroll that vanilla Grapple.Update culls against.
    //
    // Range constants match Grapples.GetGrapple's fixed-point branch:
    //   num4 = 800f (horizontal), num5 = 600f (fixed grapple),
    //   num6 = 10f  (min above player), num7 = 50f (facing tolerance).
    // col 22 (luna) requires GameSessionMgr.lunaFrame > 0.
    private static bool CanGrapplePoint(Character player, Vector2 point, int col)
    {
        if (player == null || player.dyingFrame > 0f) return false;
        // Hide the ring indicator while the player is actively grappling.
        // Vanilla stops calling GetGrapple when grappleFrame > 0 (see CharUpdateState.UpdateAirborneCharacter), so the shared visible flag stops being set and the visFrame decays to 0.
        if (player.update.grappleFrame > 0f) return false;
        if (!PlayerEquipment.IsAbilityUnlocked(0)) return false;
        if (point.Y >= player.loc.Y) return false; // must be above player
        if (point.Y <= player.loc.Y - 600f) return false; // within 600 above
        if (point.X <= player.loc.X - 800f) return false; // within 800 horizontal
        if (point.X >= player.loc.X + 800f) return false;
        if (point.Y >= player.loc.Y - 10f) return false; // at least 10 above

        var facingOk = player.face == 1
            ? point.X > player.loc.X - 50f
            : point.X < player.loc.X + 50f;
        if (!facingOk) return false;

        if (CharCols.GetLayer(point) != CharCols.GetLayer(player.loc)) return false;

        if (col == 22 && GameSessionMgr.gameSession.lunaFrame <= 0f) return false;

        if (GameSessionMgr.gameSession.mapMgr.arenas.active <= -1) return true;

        var rect = GameSessionMgr.gameSession.mapMgr.arenas
            .arena[GameSessionMgr.gameSession.mapMgr.arenas.active].rect;

        return rect.Width <= 0 || rect.Contains(point);
    }
}