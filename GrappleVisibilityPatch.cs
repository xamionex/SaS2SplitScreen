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
    // Vanilla uses a single shared Grapple.visible / Grapple.visFrame for all
    // players. In splitscreen when P2 rolls near a stunned enemy, the shared
    // visFrame becomes >0 and BOTH players see the indicator. We track
    // per-player "can grapple" flags (set in GetGrapple) and animate our own
    // per-player visFrame arrays in Grapples.Update. DrawAllGrapples then
    // draws the indicator only for the player whose visFrame > 0.
    //
    // We also reset the shared Grapple.visible to false so vanilla Update
    // doesn't drive the shared visFrame (which we ignore during draw).
    //
    // For fixed grapple points (Grapples.grapple[], charIdx < 0) we also
    // animate per-player visFrame arrays indexed by slot in grapple[],
    // because vanilla Grapple.Update hard-resets visFrame to 0 for any
    // point outside ScrollManager.scroll (P1's camera), making the ring
    // indicator disappear for P2 when P1 moves away. The growth predicate
    // is CanGrapplePoint, which replicates Grapples.GetGrapple's
    // fixed-point branch (800x600, min 10 above, facing, layer, ability).
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

        // Fixed grapple points: per-player visFrame animated by a direct
        // CanGrapplePoint check against each player. Indexed by slot in
        // grapple[] (stable within a map session).
        Character p1Char = GetCharacter(MainPlayer());
        Character p2Char = _hasP2 ? GetCharacter(CoopPlayer()) : null;
        for (int i = 0; i < __instance.grappleCount; i++)
        {
            Grapple g = __instance.grapple[i];
            bool p1Can = p1Char != null && CanGrapplePoint(p1Char, g.point, g.col);
            bool p2Can = p2Char != null && CanGrapplePoint(p2Char, g.point, g.col);

            if (p1Can)
            {
                _p1GrapplePtVisFrame[i] += frameTime * 10f;
                if (_p1GrapplePtVisFrame[i] > 1f) _p1GrapplePtVisFrame[i] = 1f;
            }
            else
            {
                _p1GrapplePtVisFrame[i] -= frameTime * 10f;
                if (_p1GrapplePtVisFrame[i] < 0f) _p1GrapplePtVisFrame[i] = 0f;
            }

            if (p2Can)
            {
                _p2GrapplePtVisFrame[i] += frameTime * 10f;
                if (_p2GrapplePtVisFrame[i] > 1f) _p2GrapplePtVisFrame[i] = 1f;
            }
            else
            {
                _p2GrapplePtVisFrame[i] -= frameTime * 10f;
                if (_p2GrapplePtVisFrame[i] < 0f) _p2GrapplePtVisFrame[i] = 0f;
            }
        }

        // Char grapples: per-player visFrame animated by GetGrapple flags.
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

    // Replicates the fixed-point branch of Grapples.GetGrapple's eligibility
    // check for a single grapple point against a single player. Returns true
    // when the player is in range, facing the point, on the same layer, and
    // the grapple ability is unlocked. Used to drive per-player visFrame
    // growth for the ring indicator so it appears for each player
    // independently of the shared ScrollManager.scroll that vanilla
    // Grapple.Update culls against.
    //
    // Range constants match Grapples.GetGrapple's fixed-point branch:
    //   num4 = 800f (horizontal), num5 = 600f (fixed grapple),
    //   num6 = 10f  (min above player), num7 = 50f (facing tolerance).
    // col 22 (luna) requires GameSessionMgr.lunaFrame > 0.
    private static bool CanGrapplePoint(Character player, Vector2 point, int col)
    {
        if (player == null || player.dyingFrame > 0f) return false;
        // Hide the ring indicator while the player is actively grappling.
        // Vanilla stops calling GetGrapple when grappleFrame > 0 (see
        // CharUpdateState.UpdateAirborneCharacter), so the shared
        // visible flag stops being set and the visFrame decays to 0.
        if (player.update.grappleFrame > 0f) return false;
        if (!PlayerEquipment.IsAbilityUnlocked(0)) return false;
        if (point.Y >= player.loc.Y) return false;            // must be above player
        if (point.Y <= player.loc.Y - 600f) return false;      // within 600 above
        if (point.X <= player.loc.X - 800f) return false;      // within 800 horizontal
        if (point.X >= player.loc.X + 800f) return false;
        if (point.Y >= player.loc.Y - 10f) return false;       // at least 10 above

        bool facingOk = player.face == 1
            ? point.X > player.loc.X - 50f
            : point.X < player.loc.X + 50f;
        if (!facingOk) return false;

        if (CharCols.GetLayer(point) != CharCols.GetLayer(player.loc)) return false;

        if (col == 22 && GameSessionMgr.gameSession.lunaFrame <= 0f) return false;

        if (GameSessionMgr.gameSession.mapMgr.arenas.active > -1)
        {
            var rect = GameSessionMgr.gameSession.mapMgr.arenas
                .arena[GameSessionMgr.gameSession.mapMgr.arenas.active].rect;
            if (rect.Width > 0 && !rect.Contains(point)) return false;
        }

        return true;
    }
}
