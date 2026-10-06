using System;
using System.Linq;
using Bestiary.monsters;
using Common;
using HarmonyLib;
using ProjectMage.character;
using ProjectMage.gamestate;
using ProjectMage.gamestate.arenastate;
using ProjectMage.gamestate.outro;
using ProjectMage.player;

namespace SaS2SplitScreen;

// ============================================================================
// Auto-disable: hand the view back to vanilla's shared-screen co-op camera while that is the better view.
//
// Two independent triggers (both off by default):
//   AutoDisableWhenClose     the players are close together.
//   AutoDisableInBossFights  the game is driving the camera (see CameraTakeoverReason), but only while the players are close, because the vanilla camera can only frame players that are near each other. A player who is far away keeps their own half instead of ending up off-screen.
//
// "Close" means both players would fit on one screen, always. Custom Distance can make it stricter (a straight-line distance in meters) but never looser.
// Players on different layers are never merged: a single camera draws one layer set, so a player standing outside while the other is in a cave would be drawn against the wrong world.
//
// Only the VIEW is handed back (SplitActive). The gameplay rules that let the players separate (ModActive: independent doors, no screen-edge tether) stay on, otherwise vanilla's tether would hold the players together and the split could never return.
//
// The decision is made once per frame at the start of PlayerMgr.Update, so the whole frame (update and draw) sees one consistent state.
// ============================================================================

internal static partial class SplitscreenPatch
{
    // Both players must fit inside this fraction of the visible world. Vanilla's own screen-edge tether sits at 90% of the width, which leaves nobody any margin, so this is a bit tighter. Vertical is stricter because a character is tall and the camera sits 100 units above the midpoint.
    private const float AutoFitX = 0.85f;
    private const float AutoFitY = 0.70f;

    // Hysteresis: to merge the players must be inside 90% of the limits, to stay merged inside 100%. Stops flicker at the boundary.
    private const float AutoEnterFactor = 0.9f;

    // A camera takeover ends (or pulses, like the 0.1 s focus used while talking to an NPC) without the view snapping back immediately.
    private const int AutoReleaseDelayMs = 500;

    // A player is treated as 1.8 m tall; the height comes from the character's collision box so the unit is stable whatever the zoom setting is.
    private const float PlayerHeightMeters = 1.8f;
    private const float FallbackPlayerHeightUnits = 200f;

    private static bool _autoDisabled;
    private static bool _autoReleasing;
    private static int _autoReleaseStartTick;
    private static bool _autoScaleLogged;

    // Size of one screen in world units while nothing controls the camera. Takeovers zoom in (a boss intro roughly halves the visible area), which would make two players who sit side by side look "too far apart to fit" and bounce the view back to split in the middle of the cutscene.
    private static float _refScreenW;
    private static float _refScreenH;

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(PlayerMgr), "Update")]
    private static void PlayerMgr_Update_AutoDisable_Prefix()
    {
        try
        {
            UpdateAutoDisable();
        }
        catch (Exception e)
        {
            SetAutoDisabled(false, "error");
            Warn($"[Splitscreen] Auto-disable: {e.Message}");
        }
    }

    private static void UpdateAutoDisable()
    {
        var onClose = GlobalSettings.AutoDisableWhenClose?.Value == true;
        var onTakeover = GlobalSettings.AutoDisableInBossFights?.Value == true;
        if (!(onClose || onTakeover) || !ModActive)
        {
            SetAutoDisabled(false, "disabled");
            return;
        }

        var p1 = MainPlayer();
        var p2 = CoopPlayer();
        if (p1 is not { charIdx: >= 0 } || p2 is not { active: true, charIdx: >= 0 }
                                        || p1.charIdx >= CharMgr.character.Length
                                        || p2.charIdx >= CharMgr.character.Length)
        {
            SetAutoDisabled(false, "no second player");
            return;
        }

        var c1 = CharMgr.character[p1.charIdx];
        var c2 = CharMgr.character[p2.charIdx];
        if (c1 == null || c2 == null)
        {
            SetAutoDisabled(false, "no second player");
            return;
        }

        // Size of one screen in world units, from the camera bounds (so it follows the zoom setting and vanilla's zoom-out). Hold the current state until the camera has valid bounds.
        var screenW = ScrollManager.bR.X - ScrollManager.tL.X;
        var screenH = ScrollManager.bR.Y - ScrollManager.tL.Y;
        if (screenW < 200f || screenH < 100f) return;

        var takeover = CameraTakeoverReason();

        // Follow the live size (it grows when vanilla zooms out for distant players) but never let a takeover's zoom-in shrink it.
        if (takeover == null && !_autoReleasing)
        {
            _refScreenW = screenW;
            _refScreenH = screenH;
        }
        else if (_refScreenW > 0f)
        {
            screenW = Math.Max(screenW, _refScreenW);
            screenH = Math.Max(screenH, _refScreenH);
        }

        var dx = Math.Abs(c1.loc.X - c2.loc.X);
        var dy = Math.Abs(c1.loc.Y - c2.loc.Y);
        var unitsPerMeter = UnitsPerMeter(c1, out var playerUnits);
        var distMeters = (float)Math.Sqrt(dx * dx + dy * dy) / unitsPerMeter;

        if (!_autoScaleLogged)
        {
            _autoScaleLogged = true;
            Log(
                $"[Splitscreen] Auto-disable scale: a player is {playerUnits:0} units tall = {PlayerHeightMeters} m, so 1 m = {unitsPerMeter:0.0} units; one screen is currently {screenW / unitsPerMeter:0.0} m wide and {screenH / unitsPerMeter:0.0} m tall.");
        }

        var sameLayer = CharCols.GetLayer(c1.loc + new Vector2(0f, -10f)) ==
                        CharCols.GetLayer(c2.loc + new Vector2(0f, -10f));
        var custom = string.Equals(GlobalSettings.AutoDisableDistanceMode?.Value, GlobalSettings.ModeCustomDistance,
            StringComparison.OrdinalIgnoreCase);
        var limitMeters = custom ? GlobalSettings.AutoDisableCustomDistance?.Value ?? 10 : float.MaxValue;

        // k = 1 is the limit to stay merged; k < 1 is the tighter limit to become merged.
        bool Within(float k)
        {
            return sameLayer
                   && dx <= screenW * AutoFitX * k
                   && dy <= screenH * AutoFitY * k
                   && distMeters <= limitMeters * k;
        }

        // The takeover only counts as a reason when that option is on; it was evaluated above for the screen-size reference either way.
        if (!onTakeover) takeover = null;

        if (!_autoDisabled)
        {
            if (!Within(AutoEnterFactor)) return;

            if (onClose)
                SetAutoDisabled(true,
                    $"players are close ({distMeters:0.0} m apart, screen is {screenW / unitsPerMeter:0.0} m wide)");
            else if (takeover != null)
                SetAutoDisabled(true, $"game controls the camera: {takeover} ({distMeters:0.0} m apart)");
            return;
        }

        // Merged. Leave immediately when the players drift apart or onto different layers.
        if (!Within(1f))
        {
            SetAutoDisabled(false,
                sameLayer ? $"players moved apart ({distMeters:0.0} m)" : "players are on different layers");
            return;
        }

        if (onClose || takeover != null)
        {
            _autoReleasing = false;
            return;
        }

        // Still close, but only the takeover held the merge and it just ended: wait a moment so pulses don't flicker the view.
        var now = Environment.TickCount;
        if (!_autoReleasing)
        {
            _autoReleasing = true;
            _autoReleaseStartTick = now;
            return;
        }

        if (now - _autoReleaseStartTick >= AutoReleaseDelayMs)
            SetAutoDisabled(false, "the game gave the camera back");
    }

    private static void SetAutoDisabled(bool value, string reason)
    {
        _autoReleasing = false;
        if (_autoDisabled == value) return;

        _autoDisabled = value;
        Log(value
            ? $"[Splitscreen] Auto-disabled splitscreen: {reason}."
            : $"[Splitscreen] Splitscreen resumed: {reason}.");
    }

    // Meters from the character's own collision box. Falls back to a typical player height if the definition can't be read.
    private static float UnitsPerMeter(Character c, out float playerUnits)
    {
        playerUnits = FallbackPlayerHeightUnits;
        try
        {
            if (c != null && c.monsterIdx >= 0)
            {
                var def = MonsterCatalog.monsterDef[c.monsterIdx];
                if (def != null && def.boxHeight >= 60) playerUnits = def.boxHeight;
            }
        }
        catch (Exception)
        {
            // keep the fallback
        }

        return playerUnits / PlayerHeightMeters;
    }

    // The ways the vanilla camera stops following the players (CamMgr.Update), or null when nothing is controlling it.
    private static string CameraTakeoverReason()
    {
        var gs = GameSessionMgr.gameSession;
        if (gs == null) return null;

        if (IntroManager.active) return "intro";
        if (OutroManager.active) return "outro";
        if (ArenaStateMgr.active) return "arena mode";
        if (gs.bossSplash is { active: true }) return "boss intro";

        // SetFocus: cutscenes, NPC conversations, deaths, boss and script triggers.
        if (PlayerMgr.player != null)
            if (PlayerMgr.player.Any(player => player?.camMgr is { focusPoint: > 0f }))
                return "camera focus";

        // A boss arena frames the boss and everyone inside it (MapArena.GetGZoom). The same guards as CamMgr.Update.
        var arenas = gs.mapMgr?.arenas;
        if (arenas is { arena: not null, active: > -1 }
            && arenas.active < arenas.arena.Count
            && gs.worldWarpFrame <= 0f
            && gs.worldWarpOutFrame <= 0f
            && gs.obliteratedState == 0
            && arenas.arena[arenas.active].HasBoss())
            return "boss arena";

        if (gs.obliteratedState != 0) return "wipe screen";
        if (gs.checkpointRestockFrame > 0f) return "resting";

        var message = gs.bigGameMessage?.messageState.messageType ?? BigGameMessage.MessageType.None;
        if (message != BigGameMessage.MessageType.None && message != BigGameMessage.MessageType.Matchmade)
            return "game message";

        return null;
    }
}