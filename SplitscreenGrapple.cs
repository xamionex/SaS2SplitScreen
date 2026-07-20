using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Common;
using HarmonyLib;
using Menumancer.hud;
using Menumancer.UIFormat;
using ProjectMage;
using ProjectMage.character;
using ProjectMage.config;
using ProjectMage.director;
using ProjectMage.gamestate;
using ProjectMage.gamestate.arenastate;
using ProjectMage.gamestate.outro;
using ProjectMage.map.entities;
using ProjectMage.map.pickups;
using ProjectMage.particles;
using ProjectMage.gamestate.mage;
using ProjectMage.player;
using ProjectMage.player.menu;
using ProjectMage.texturesheet;
using SalmonMaps.director.bloom;
using SalmonMaps.map;
using Bestiary.monsters;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // DRAW ALL GRAPPLES
    private static void DrawAllGrapples()
    {
        var p1 = MainPlayer();
        var p2 = CoopPlayer();
        if (p1 == null || p2 == null) return;

        // Rebuild char grapples from merged activeChars before drawing.
        var grapples = GameSessionMgr.gameSession?.mapMgr?.entityMgr?.grapples;
        if (grapples != null)
        {
            grapples.ResetCharGrapples();
            for (int i = 0; i < _mergedActiveCharsCount; i++)
            {
                int idx = _mergedActiveChars[i];
                if (idx < 0 || idx >= CharMgr.character.Length) continue;
                Character c = CharMgr.character[idx];
                if (c == null || !c.exists) continue;
                if (c.update.grappleStunFrame <= 0f) continue;
                if (grapples.charGrappleCount >= 8) break;

                var g = grapples.charGrapple[grapples.charGrappleCount];
                g.charIdx = idx;
                g.point = GetCharGrappleHeadPos(c);
                g.x = -1;
                g.y = -1;
                g.col = 20;
                g.frame = 0f;
                g.visible = false;
                g.visFrame = 0f;
                grapples.charGrappleCount++;
            }
        }

        int screenW = (int)ScrollManager.screenSize.X;
        int screenH = (int)ScrollManager.screenSize.Y;
        int halfW = screenW / 2;
        int cropX = halfW / 2;

        Vector2 savedScroll = GetScroll();

        SpriteTools.BeginAlpha();

        // Player 1 (left half)
        SetScroll(ScrollFor(_p1Loc));
        DrawGrapplesForPlayer(p1, new Rectangle(0, 0, halfW, screenH), cropX);

        // Player 2 (right half)
        SetScroll(ScrollFor(_p2Loc));
        DrawGrapplesForPlayer(p2, new Rectangle(halfW, 0, halfW, screenH), cropX);

        SetScroll(savedScroll);
        SpriteTools.End();
    }

    private static void DrawGrapplesForPlayer(Player player, Rectangle halfRect, int cropX)
    {
        var grapples = GameSessionMgr.gameSession?.mapMgr?.entityMgr?.grapples;
        if (grapples == null) return;

        var grappleTex = Textures.tex[ParticleManager.spritesTexIdx];

        float viewH = halfRect.Height;
        Vector2 gameSize = ScrollManager.screenSize;

        for (int pass = 0; pass < 2; pass++)
        {
            int count = pass == 0 ? grapples.grappleCount : grapples.charGrappleCount;
            Grapple[] arr = pass == 0 ? grapples.grapple : grapples.charGrapple;

            for (int i = 0; i < count; i++)
            {
                Grapple g = arr[i];

                // Use g.point directly (computed during rebuild with validated headVec or a box-based fallback).
                // No live headVec override here,
                // because that caused the indicator to drift or jump in splitscreen when headVec came from a different draw pass.
                //
                // Position mapping: the game draws into a full-size render
                // target (screenSize), then CompositeHalves crops the center
                // half of each target and blits it to each player's half of
                // the screen. The crop is: src = (cropX, 0, halfW, screenH),
                // dst = (halfRect.X, 0, halfW, screenH). So a point at
                // gameScreenPos in the render target ends up at
                // (gameScreenPos - (cropX, 0)) + (halfRect.X, 0) on screen.
                // The star particle goes through this crop; the ring must
                // use the same mapping or it will be offset from the star
                // (the old formula scaled by viewW/gameSize which compressed
                // toward center, placing the ring between the player and the
                // grapple point instead of at the point).
                Vector2 gameScreenPos = ScrollManager.GetScreenLoc(g.point, 0);
                Vector2 drawPosBase = new Vector2(
                    halfRect.X + gameScreenPos.X - cropX,
                    gameScreenPos.Y);

                // charIdx stun-ring (sprite 0, elliptical, 2 rings)
                if (g.charIdx >= 0 && g.charIdx < CharMgr.character.Length)
                {
                    float stunFrame = CharMgr.character[g.charIdx].update.grappleStunFrame;
                    if (stunFrame > 1f)
                    {
                        float num = Math.Min(stunFrame - 1f, 1f);
                        float num4 = viewH / gameSize.Y;
                        num4 *= 0.3f;

                        float num5 = stunFrame * 10f;
                        num5 -= (float)((int)num5);
                        if (num5 < 0.5f) num5 = 0f;
                        else if (num5 < 0.6f) num5 = (num5 - 0.5f) * 10f;
                        else if (num5 < 0.9f) num5 = 1f;
                        else num5 = (1f - num5) * 10f;

                        if (num5 > 0f)
                        {
                            for (int j = 0; j < 2; j++)
                            {
                                Vector2 stunDrawPos = drawPosBase;
                                grappleTex.Draw(
                                    stunDrawPos, 0,
                                    ScrollManager.cannedDepth[0] * new Vector2(2f, 0.5f) * num4,
                                    stunFrame + j * 1.5707964f,
                                    1f, 1f, 1f, num5 * num, 0.65005004f);
                            }
                        }
                    }
                }

                // visFrame indicator rings (sprite 94, 3 rings).
                // Use per-player visFrame so each player only sees
                // indicators for points/chars they can actually grapple.
                // For fixed grapple points (pass 0, charIdx < 0) the array
                // is indexed by slot in grapple[]; for char grapples
                // (pass 1) it is indexed by charIdx.
                float playerVisFrame;
                if (pass == 0)
                {
                    playerVisFrame = player.ID == 0
                        ? _p1GrapplePtVisFrame[i]
                        : _p2GrapplePtVisFrame[i];
                }
                else if (g.charIdx >= 0 && g.charIdx < 320)
                {
                    playerVisFrame = player.ID == 0
                        ? _p1GrappleVisFrame[g.charIdx]
                        : _p2GrappleVisFrame[g.charIdx];
                }
                else
                {
                    continue;
                }
                if (playerVisFrame <= 0f) continue;

                float t = playerVisFrame;

                // Edge-clamp the ring indicator only when the grapple point
                // is near or past the edge of the half-viewport (within 10%
                // of any edge). When the point is comfortably in view, draw
                // at the exact position (no clamp) so the ring stays on the
                // star. When within the 10% edge margin, clamp to 10% from
                // the edge so the ring stays on-screen.
                float num8 = viewH / gameSize.Y * t
                             + (float)Math.Sin(t * Math.PI) * 0.45f;
                num8 *= 0.3f;

                Vector2 drawPos = drawPosBase;

                // Clamp only in the 10% edge band.
                float edgeXMin = halfRect.X + halfRect.Width * 0.10f;
                float edgeXMax = halfRect.X + halfRect.Width * 0.90f;
                float edgeYMin = halfRect.Height * 0.10f;
                float edgeYMax = halfRect.Height * 0.90f;
                if (drawPos.X < edgeXMin) drawPos.X = edgeXMin;
                if (drawPos.X > edgeXMax) drawPos.X = edgeXMax;
                if (drawPos.Y < edgeYMin) drawPos.Y = edgeYMin;
                if (drawPos.Y > edgeYMax) drawPos.Y = edgeYMax;

                for (int j = 0; j < 3; j++)
                {
                    grappleTex.Draw(
                        drawPos,
                        94,
                        ScrollManager.cannedDepth[0] * new Vector2(1f, 1f) * num8,
                        t + j * 2.0943952f,
                        1f, 1f, 1f, 0.5f, 0.65005004f);
                }
            }
        }
    }

    // Grapple star sparkle fix for splitscreen.
    // Grapple.Update advances this.frame and spawns the star sparkle
    // (AddAdditiveParticle 81) only when the point is within ~1000-2000
    // units of ScrollManager.scroll. In splitscreen scroll follows P1
    // during grapples.Update, so points near P2 stop spawning the star
    // when P1 moves away. Prefix saves the frame; postfix advances it
    // and spawns the star when the point is near P2 but was culled.
    private static FieldInfo _grappleRandField =
        AccessTools.Field(typeof(Grapple), "Rand");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Grapple), "Update")]
    private static void Grapple_Update_Prefix(Grapple __instance, out float __state)
    {
        __state = __instance.frame;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Grapple), "Update")]
    private static void Grapple_Update_Postfix(Grapple __instance, float frameTime, float __state)
    {
        if (!SplitActive || !_hasP2) return;

        // If vanilla advanced the frame, the point was within P1's scroll
        // and the star already spawned. Nothing to do.
        if (Math.Abs(__instance.frame - __state) > 0.0001f) return;

        Vector2 loc = __instance.point;
        Vector2 p2Scroll = ScrollFor(_p2Loc);

        // Match vanilla's culling range: 1000 normally, 2000 when zoomed out.
        float num = ScrollManager.zoom < -25f ? 2000f : 1000f;

        if (loc.X > p2Scroll.X - num && loc.X < p2Scroll.X + num &&
            loc.Y > p2Scroll.Y - 1000f && loc.Y < p2Scroll.Y + 1000f)
        {
            __instance.frame += frameTime;
            if (__instance.frame > 1f)
            {
                __instance.frame -= 0.7f; // midpoint of Rand(0.6, 0.8)
                if (__instance.col != 22 || GameSessionMgr.gameSession.lunaFrame > 0f)
                {
                    ParticleManager.AddAdditiveParticle(81, loc, default(Vector2), 0f, 0f, 0, 0, -1);
                }
            }
        }
    }

    // MapPickup culling fix for splitscreen.
    // MapPickup.Update freezes sparkle particles when the pickup is far from ScrollManager.scroll.
    // In splitscreen, scroll is restored to P1 after Player_Update_Postfix,
    // so pickups near P2 stop animating.
    // Prefix saves the frame.
    // Postfix adds a P2 culling check when the original method didn't update the pickup.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MapPickup), "Update")]
    private static void MapPickup_Update_Prefix(MapPickup __instance, out float __state)
    {
        __state = (float)_mapPickupFrameField.GetValue(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MapPickup), "Update")]
    private static void MapPickup_Update_Postfix(MapPickup __instance, float frameTime, float __state)
    {
        if (!SplitActive || !_hasP2) return;

        float newFrame = (float)_mapPickupFrameField.GetValue(__instance);
        if (Math.Abs(newFrame - __state) > 0.0001f)
            return; // original already updated

        Vector2 loc = __instance.loc;
        Vector2 p2Scroll = ScrollFor(_p2Loc);

        if (loc.X > p2Scroll.X - 1200f && loc.X < p2Scroll.X + 1200f &&
            loc.Y > p2Scroll.Y - 1000f && loc.Y < p2Scroll.Y + 1000f)
        {
            float frame = __state + frameTime;
            if (frame > 1f) frame -= 1f;
            _mapPickupFrameField.SetValue(__instance, frame);

            if ((int)(__state * 20f) != (int)(frame * 20f))
            {
                int num2 = 2;
                int charIdx = (int)_mapPickupCharIdxField.GetValue(__instance);
                if (charIdx > -1) num2 = 5;
                int rarity = __instance.rarity;
                Rand rand = (Rand)_mapPickupRandField.GetValue(null);
                ParticleManager.AddParticle(num2, 21, loc, rand.GetRandomVec2(-20f, 20f, -200f, -80f),
                    rand.GetRandomFloat(0.15f, 0.25f), 0f, rarity, 0, -1);
            }
        }
    }

    // MageRuneSet drawLoc fix for splitscreen.
    // In splitscreen, P2's draw pass can compute drawChest for P1-only mages using P2's wrong scroll,
    // producing a bogus absolute world position.
    // Vanilla then sets drawLoc = loc with that bogus offset,
    // causing the rune to teleport far off-screen ("disappear").
    // We validate drawChest (it should be near the character),
    // and revert to a cached valid drawLoc whenever the current value is stale.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MageRuneSet), "Update")]
    private static void MageRuneSet_Update_Postfix(MageRuneSet __instance, float frameTime, float heartbeat)
    {
        if (!SplitActive) return;
        if (__instance.mage == null || __instance.mage.charIdx < 0) return;

        Character character = CharMgr.character[__instance.mage.charIdx];
        if (character == null) return;

        MonsterDef monsterDef = MonsterCatalog.monsterDef[character.monsterIdx];
        bool vanillaWouldUpdate = character.draw.updatePhysicsDraw
            || (monsterDef != null && monsterDef.gameMonster.shadow && character.draw.pUpdatePhysicsDraw);

        Vector2 drawChest = character.draw.drawChest;
        // drawChest is the absolute world position of the chest.
        // It should be within a few hundred units of character.loc.
        // Anything farther means it was computed with a mismatched scroll (wrong player pass).
        bool drawChestValid = drawChest != Vector2.Zero
                              && Math.Abs(drawChest.X - character.loc.X) < 800f
                              && Math.Abs(drawChest.Y - character.loc.Y) < 800f;

        if (drawChestValid)
        {
            // Remember the valid offset for this character,
            // so we can fall back to it on stale frames.
            _cachedRuneDrawLoc[character.ID] = __instance.drawLoc;
        }
        else
        {
            // drawChest is bogus.
            // Revert both loc and drawLoc to the last known good offset,
            // so the rune stays at the correct position.
            if (_cachedRuneDrawLoc.TryGetValue(character.ID, out Vector2 cached))
            {
                __instance.loc = cached;
                __instance.drawLoc = cached;
            }
        }

        // Vanilla only copies loc to drawLoc when updatePhysicsDraw is true.
        // Ensure drawLoc is always kept in sync when vanilla skipped it.
        if (!vanillaWouldUpdate)
        {
            __instance.drawLoc = __instance.loc;
            _cachedRuneDrawLoc[character.ID] = __instance.drawLoc;
        }
    }

    // Mage.DrawRunes prefix - fix one-frame lag in splitscreen.
    // Vanilla sets canDrawRunes during CharMgr.Draw(),
    // and consumes it in MageMgr.DrawMageAdditive() / CharMgr.DrawPrism().
    // With two draw passes (P1 then P2),
    // the flag set by P2 overwrites P1's value,
    // so a mage visible only to P1 never has its runes drawn in P1's half.
    // We override canDrawRunes with a live GetVis check right before DrawRunes runs.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Mage), "DrawRunes")]
    private static void Mage_DrawRunes_Prefix(Mage __instance)
    {
        if (!SplitActive) return;
        if (__instance.charIdx < 0) return;
        Character c = CharMgr.character[__instance.charIdx];
        if (c == null || !c.exists) return;
        __instance.canDrawRunes = CharMgrGetVis(c, false);
    }

    // MageParticles.UpdateBaseParticles - dual-viewport culling.
    // Vanilla spawns the dark subtractive disc behind a mage,
    // but only when the mage is on-screen according to ScrollManager.scroll.
    // In splitscreen, scroll is restored to the midpoint after PlayerMgr.Update,
    // so mages near only one player are culled and the disc disappears.
    // We override the culling to check visibility against BOTH players.
    private static readonly Type _mageParticlesType =
        AccessTools.TypeByName("ProjectMage.gamestate.mage.MageParticles");
    private static readonly FieldInfo _mageParticlesRandField =
        _mageParticlesType != null ? AccessTools.Field(_mageParticlesType, "Rand") : null;

    [HarmonyPrefix]
    [HarmonyPatch("ProjectMage.gamestate.mage.MageParticles", "UpdateBaseParticles")]
    private static bool MageParticles_UpdateBaseParticles_Prefix(
        object __instance, float pDrawFrame, float drawFrame,
        Character character, MonsterDef mageDef)
    {
        if (!SplitActive) return true; // let vanilla handle it
        if ((int)(pDrawFrame * 4f) == (int)(drawFrame * 4f))
            return false; // nothing to do this frame

        float maxDistX = ScrollManager.screenSize.X * 0.6f;
        float maxDistY = ScrollManager.screenSize.Y * 0.6f;

        bool visible = Math.Abs(character.loc.X - _p1Loc.X) < maxDistX
                       && Math.Abs(character.loc.Y - _p1Loc.Y) < maxDistY;
        if (!visible && _hasP2)
        {
            visible = Math.Abs(character.loc.X - _p2Loc.X) < maxDistX
                      && Math.Abs(character.loc.Y - _p2Loc.Y) < maxDistY;
        }

        if (visible && _mageParticlesRandField != null)
        {
            var rand = (Rand)_mageParticlesRandField.GetValue(null);
            if (rand != null)
            {
                ParticleManager.AddSubtractiveParticle(
                    29, character.loc,
                    rand.GetRandomVec2(-1f, 1f, -1f, 1f) * 10f,
                    (float)mageDef.shadowWidth * 0.01f, 0f, 0, 0, character.ID);
                ParticleManager.AddBackSubtractiveParticle(
                    29, character.loc,
                    rand.GetRandomVec2(-1f, 1f, -1f, 1f) * 10f,
                    (float)mageDef.shadowWidth * 0.001f, 0f, 0, 0, character.ID);
            }
        }
        return false; // skip original
    }

}
