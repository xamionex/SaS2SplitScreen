using System;
using Common;
using HarmonyLib;
using ProjectMage.gamestate;
using ProjectMage.gamestate.arenastate;
using ProjectMage.gamestate.outro;
using ProjectMage.map.arena;
using ProjectMage.player;

namespace SaS2SplitScreen;

// ============================================================================
// Camera takeovers (boss intros, boss arenas, focus shots, big messages, resting) with two players.
//
// Vanilla has ONE camera (player 0's CamMgr) and every takeover is written for it. Three things go wrong once there are two views:
//
// 1. Activation reads the camera from the GLOBAL ScrollManager. MapArena.Activate measures the screen to build the arena's camera bounds, and BossSplash.Init stores ScrollManager.scroll/zoom as the point the intro swings away from and returns to.
//    In splitscreen that global holds whatever the last per-player draw or prompt left there (usually the second player's position), so the intro ended "where P2 was" and the camera then dragged itself back to the midpoint.
//    Fix: while those two methods run, ScrollManager is set to the main camera's own scroll and zoom, then put back.
//
// 2. Player 1's camera and the forced update of player 2's camera both run the cutscene scrolls (BossSplash.Update, IntroManager.UpdateScroll, ArenaStateMgr.UpdateScroll), so splitscreen played them at double speed.
//    Fix: they are skipped while player 2's camera is being updated.
//
// 3. The halves are locked to their player and never look at the camera at all, so a takeover only reached them through the shared zoom (and nothing moved).
//    Fix: each half smoothly moves toward the target of a takeover, but only when that takeover concerns it:
//      boss intro   the half swings to the boss and back, the same curve the vanilla intro uses (players inside the arena only)
//      boss arena   the half goes to the frame the game computes for the boss and the players inside the arena (players inside only)
//      focus shot   the half goes to the focus point when it is close to that player (see 4)
//      big message  each half pulls in on its own player, like the vanilla camera does for the main player
//    Zoom stays shared (player 1's camera, as before), so the zoom of every takeover above still reaches both halves.
//
// 4. SetFocus is almost always called on the MAIN player's camera whoever caused it: environment triggers (doors, caves, collapses), deaths, boss reveals.
//    With one shared camera that is right; with two views a trigger P2 walks into swung P1's view over to P2's surroundings, and nudged P1's zoom.
//    Fix: while the split is on, a focus that is far from the camera's own player and close to the other player is handed to the other player's camera.
//    Intro and outro cutscenes are left alone (they set every camera deliberately).
// ============================================================================

internal static partial class SplitscreenPatch
{
    // ---- 1. activation reads the main camera, not whatever the global was left at -------------------------------------

    internal struct SavedScroll
    {
        public Vector2 Scroll;
        public float Zoom;
        public bool Valid;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MapArena), "Activate")]
    // ReSharper disable once InconsistentNaming
    private static void MapArena_Activate_Prefix(out SavedScroll __state)
    {
        __state = default;
        if (!ModActive) return;

        var main = PlayerMgr.player?[0]?.camMgr;
        if (main == null) return;

        __state = new SavedScroll { Scroll = ScrollManager.scroll, Zoom = ScrollManager.zoom, Valid = true };
        ScrollManager.scroll = main.scroll;
        ScrollManager.zoom = main.zoom;
        ScrollManager.UpdateCannedValues();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MapArena), "Activate")]
    // ReSharper disable once InconsistentNaming
    private static void MapArena_Activate_Postfix(SavedScroll __state)
    {
        if (!__state.Valid) return;

        ScrollManager.scroll = __state.Scroll;
        ScrollManager.zoom = __state.Zoom;
        ScrollManager.UpdateCannedValues();
    }

    // ---- 2. cutscene scrolls run once per frame, not once per camera --------------------------------------------------

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BossSplash), "Update")]
    private static bool BossSplash_Update_Prefix() => !_inP2CamUpdate;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(IntroManager), "UpdateScroll")]
    private static bool IntroManager_UpdateScroll_Prefix() => !_inP2CamUpdate;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ArenaStateMgr), "UpdateScroll")]
    private static bool ArenaStateMgr_UpdateScroll_Prefix() => !_inP2CamUpdate;

    // ---- 3./4. per-half takeover camera and focus routing ---------------------------------------------------------------

    // A focus shot concerns a player when it happens this close to them (world units; a screen half is roughly 1000).
    private const float FocusRange = 1400f;

    // Same smoothing as the vanilla camera (CamMgr.Update moves at frameTime * 2).
    private const float TakeoverFollowRate = 2f;

    // Extra pull-in height used by the vanilla camera for big messages.
    private static readonly Vector2 MessageOffset = new(0f, -260f);

    private static readonly Vector2[] HalfPos = new Vector2[2];
    private static readonly bool[] HalfActive = new bool[2];
    private static bool _halfCamFailed;

    private static float Dist(Vector2 a, Vector2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    // Where a half's view is centered this frame: the locked player position, or the takeover camera when one applies.
    private static Vector2 HalfScroll(int half)
    {
        if (!_halfCamFailed && HalfActive[half] && SplitActive) return HalfPos[half];
        return ScrollFor(half == 0 ? _p1Loc : _p2Loc);
    }

    // Forget any takeover state; called every frame the split view is off so it can't be stale when the split comes back.
    private static void ResetHalfCameras()
    {
        HalfActive[0] = HalfActive[1] = false;
    }

    private static bool _redirectingFocus;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CamMgr), "SetFocus")]
    // ReSharper disable once InconsistentNaming
    private static bool CamMgr_SetFocus_Prefix(CamMgr __instance, Vector2 loc, float zoom, float duration)
    {
        if (_redirectingFocus || !SplitActive || !HasP2) return true;
        if (IntroManager.active || OutroManager.active) return true;

        var cam0 = PlayerMgr.player?[0]?.camMgr;
        var cam1 = PlayerMgr.player?[1]?.camMgr;
        if (cam0 == null || cam1 == null) return true;

        // A camera that is given a focus close to its own player keeps it (a player talking to an NPC sets it on their own camera).
        var own = __instance == cam1 ? _p2Loc : _p1Loc;
        if (Dist(own, loc) <= FocusRange) return true;

        var d0 = Dist(_p1Loc, loc);
        var d1 = Dist(_p2Loc, loc);
        if (d0 > FocusRange && d1 > FocusRange) return true;

        var target = d1 < d0 ? cam1 : cam0;
        if (target == __instance) return true;

        _redirectingFocus = true;
        try
        {
            target.focusPoint = duration;
            target.focusVec = loc;
            target.focusZoom = zoom;
        }
        finally
        {
            _redirectingFocus = false;
        }

        return false;
    }

    // Focus shot that concerns a half: either camera may hold it (both do when the players are together).
    // ReSharper disable once UnusedParameter.Local
    private static bool TryGetFocus(int half, Vector2 lockPos, out Vector2 focus)
    {
        focus = default;
        var best = float.MaxValue;
        var found = false;
        for (var i = 0; i < 2; i++)
        {
            var cam = PlayerMgr.player[i]?.camMgr;
            if (cam == null || cam.focusPoint <= 0f) continue;

            var d = Dist(lockPos, cam.focusVec);
            if (d > FocusRange || d >= best) continue;

            best = d;
            focus = cam.focusVec;
            found = true;
        }

        return found;
    }

    // Once per frame, after both cameras have been updated.
    private static void UpdateHalfCameras(float dt)
    {
        if (_halfCamFailed)
        {
            HalfActive[0] = HalfActive[1] = false;
            return;
        }

        try
        {
            var gs = GameSessionMgr.gameSession;
            if (gs == null || PlayerMgr.player == null || PlayerMgr.player.Length < 2)
            {
                HalfActive[0] = HalfActive[1] = false;
                return;
            }

            var arenas = gs.mapMgr?.arenas;
            var arenaIdx = -1;
            if (arenas?.arena != null && arenas.active > -1 && arenas.active < arenas.arena.Count
                && gs.worldWarpFrame <= 0f && gs.worldWarpOutFrame <= 0f && gs.obliteratedState == 0
                && arenas.arena[arenas.active].HasBoss())
                arenaIdx = arenas.active;

            var splash = gs.bossSplash != null && gs.bossSplash.active;
            var message = gs.bigGameMessage?.messageState.messageType ?? BigGameMessage.MessageType.None;
            var pullIn = (message != BigGameMessage.MessageType.None && message != BigGameMessage.MessageType.Matchmade)
                         || gs.obliteratedState != 0;
            var follow = Math.Min(1f, dt * TakeoverFollowRate);

            for (var half = 0; half < 2; half++)
            {
                var player = PlayerMgr.player[half];
                var cam = player?.camMgr;
                var character = player == null ? null : GetCharacter(player);
                var lockPos = ScrollFor(half == 0 ? _p1Loc : _p2Loc);
                var inArena = arenaIdx > -1 && character != null && arenas.GetArenaCharIsInIdx(character) == arenaIdx;

                if (splash && inArena)
                {
                    // Same curve as BossSplash.Update, but starting from and returning to this half's own position.
                    var f = gs.bossSplash.frame;
                    var t = f > 4f ? 5f - f : f > 1f ? 1f : f;
                    var ease = 0.5f - (float)Math.Cos(t * 3.1415927f) * 0.5f;
                    HalfPos[half] = lockPos + (gs.bossSplash.bossVec - lockPos) * ease;
                    HalfActive[half] = true;
                    continue;
                }

                // The target of whichever takeover applies to this half, in the same priority order as CamMgr.Update.
                var goal = lockPos;
                var has = false;
                if (TryGetFocus(half, lockPos, out var focus))
                {
                    goal = focus;
                    has = true;
                }
                else if (pullIn && character != null)
                {
                    goal = character.loc + MessageOffset;
                    has = true;
                }
                else if (inArena && cam != null)
                {
                    var frame = arenas.arena[arenaIdx].GetGZoom(cam);
                    goal = new Vector2(frame.X, frame.Y);
                    has = true;
                }

                if (!has && !HalfActive[half]) continue;

                // Start every takeover from the player, so there is never a jump.
                if (!HalfActive[half])
                {
                    HalfPos[half] = lockPos;
                    HalfActive[half] = true;
                }

                if (!has) goal = lockPos;
                HalfPos[half] = HalfPos[half] + (goal - HalfPos[half]) * follow;

                // Back on the player: hand the view back to the plain lock.
                if (!has && Dist(HalfPos[half], lockPos) < 3f)
                    HalfActive[half] = false;
            }
        }
        catch (Exception e)
        {
            _halfCamFailed = true;
            HalfActive[0] = HalfActive[1] = false;
            Warn($"[Splitscreen] Takeover camera disabled: {e.Message}");
        }
    }
}