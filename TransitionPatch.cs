using System;
using System.Linq;
using Common;
using HarmonyLib;
using ProjectMage.player;
using SalmonMaps.map;

namespace SaS2SplitScreen;

// Layer-transition policy for splitscreen: settle the layer state so no transition animates on the OTHER player's half, but leave the player who is actually mid-transition (prev != cur, layerTransitionFrame < 1)
// LIVE so the vanilla cave enter/exit transition - its real shaders and background morph - plays only on their own half.
//
// The original "settle everyone" version (this patch alone) disabled the transition for both players; that is the confirmed leak-free baseline.
// The per-player skip keeps that baseline for the non-crossing player while restoring the vanilla transition for the crossing player.
//
// DEBUG KEYS (splitscreen gameplay only):
//   F9  - force-start the transition on P1's camera (no character move),
//   F10 - force-start the transition on P2's camera.
// This is for testing: it triggers the vanilla transition while both players stand still, so you can see exactly which half renders it.
//
// STRICTLY a no-op outside active splitscreen gameplay (state == 1): menus / load-save screens must keep vanilla layer behavior or the character previews break.
internal static partial class SplitscreenPatch
{
    private static int _lastLiveLogTick;
    private static bool _dbgP1KeyDown, _dbgP2KeyDown;
    private static int _dbgForcedTick;

    // Set during the update phase (before the draw-phase camMgr swap) when any player's camera is mid-transition.
    // The draw-time loggers read this so P2's pass is not throttled while P1 transitions.
    private static bool _anyCamTransitionLive;

    // Debug-forced transition state. The vanilla CamMgr.Update re-samples the character's real tile layer every frame, so a one-shot forced curLayer change is immediately reversed.
    // To make F9/F10 a reliable test we hold the forced state for a full 15-frame transition.
    private static int _forcedP1Cur = -1, _forcedP1Prev = -1;
    private static float _forcedP1Ltf = -1f;
    private static int _forcedP2Cur = -1, _forcedP2Prev = -1;
    private static float _forcedP2Ltf = -1f;

    private static void UpdateTransitionLiveFlag()
    {
        _anyCamTransitionLive = false;
        if (PlayerMgr.player == null) return;
        if (!(from player in PlayerMgr.player where player is { camMgr: not null } select player.camMgr).Any(cm => cm.curLayer != cm.prevLayer && cm.layerTransitionFrame < 1f)) return;
        _anyCamTransitionLive = true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CamMgr), "Update")]
    [HarmonyPriority(Priority.Last)]
    // ReSharper disable once InconsistentNaming
    private static void DisableTransition_Postfix(CamMgr __instance, float frameTime)
    {
        if (!SplitActive) return; // includes GameState.state == 1 check

        UpdateTransitionLiveFlag();

        var isP1 = __instance == PlayerMgr.player[0].camMgr;
        var isP2 = !isP1 && PlayerMgr.player.Length > 1 && __instance == PlayerMgr.player[1].camMgr;

        // Apply any in-progress debug-forced transition (overrides the vanilla re-sample so the forced transition plays in full).
        if (isP1 && _forcedP1Cur >= 0)
        {
            AdvanceForced(__instance, ref _forcedP1Cur, ref _forcedP1Prev, ref _forcedP1Ltf, "P1", frameTime);
            return;
        }

        if ((isP2 && _forcedP2Cur >= 0) || (isP2 && _forcedP2Cur >= 0))
        {
            AdvanceForced(__instance, ref _forcedP2Cur, ref _forcedP2Prev, ref _forcedP2Ltf, "P2", frameTime);
            return;
        }

        // The player who is mid-transition keeps their live state so the vanilla transition renders on their half only.
        if (IsMidTransition(__instance))
        {
            var now = Environment.TickCount;
            if (now - _lastLiveLogTick <= 250) return;
            _lastLiveLogTick = now;
            Log($"[Splitscreen] layer transition kept live for {PlayerLabel(__instance)} (cur {__instance.curLayer}, prev {__instance.prevLayer}, ltf {__instance.layerTransitionFrame:0.00})");

            return;
        }

        __instance.prevLayer = __instance.curLayer;
        __instance.layerTransitionFrame = 1f;
        if (LayerTintCatalog.layerTintData != null && __instance.curLayer >= 0 && __instance.curLayer < LayerTintCatalog.layerTintData.Count) __instance.indoors = LayerTintCatalog.layerTintData[__instance.curLayer].indoorf;
        else __instance.indoors = 0f;
    }

    // Advance a debug-forced transition by one tick, mirroring the vanilla machine (ltf += frameTime*4, so the duration is 0.25s at any fps).
    // Indoors is LERPED between prev and cur by ltf, exactly like vanilla, so the interior/exterior layers and lighting crossfade over the full 15 frames instead of snapping.
    // Clears the forced state when complete so real walk transitions work again.
    private static void AdvanceForced(CamMgr cam, ref int cur, ref int prev,
        ref float ltf, string label, float frameTime)
    {
        cam.prevLayer = prev;
        cam.curLayer = cur;
        cam.layerTransitionFrame = ltf;

        float prevF = 0f, curF = 0f;
        if (LayerTintCatalog.layerTintData != null)
        {
            if (prev >= 0 && prev < LayerTintCatalog.layerTintData.Count) prevF = LayerTintCatalog.layerTintData[prev].indoorf;
            if (cur >= 0 && cur < LayerTintCatalog.layerTintData.Count) curF = LayerTintCatalog.layerTintData[cur].indoorf;
        }

        cam.indoors = prevF + (curF - prevF) * ltf;

        if (!(ltf < 1f)) return;

        ltf += Math.Max(frameTime, 0.001f) * 4f; // vanilla rate: 0.25s
        if (ltf >= 1f)
        {
            ltf = 1f;
            cam.indoors = curF;
            // Mark complete: the walk-driven transition takes over.
            cur = -1;
            Log($"[Splitscreen] DEBUG forced transition complete for {label}");
        }
        else
        {
            Log($"[Splitscreen] DEBUG forced {label}: ltf {ltf:0.00} (cur {cur}, prev {prev})");
        }
    }

    // Debug trigger keys. Runs once per frame from P1's CamMgr.Update (P1's camera updates every tick in splitscreen), so edge detection here is stable.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CamMgr), "Update")]
    // ReSharper disable once InconsistentNaming
    private static void CamMgr_Update_DebugKeys_Postfix(CamMgr __instance)
    {
        if (!SplitActive) return;
        if (__instance != PlayerMgr.player[0].camMgr) return;

        HandleDebugKey(Keys.F9, ref _dbgP1KeyDown, 0, "P1");
        HandleDebugKey(Keys.F10, ref _dbgP2KeyDown, 1, "P2");
        PollDiagKey();
    }

    private static void HandleDebugKey(Keys key, ref bool wasDown, int playerIdx, string label)
    {
        var isDown = FrameworkImpl.GetKeyState(key) == KeyState.Down;
        var pressed = isDown && !wasDown;
        wasDown = isDown;
        if (!pressed) return;

        var p = PlayerMgr.player[playerIdx];
        if (p?.camMgr == null) return;

        // Throttle: only one forced transition per player per 300ms.
        var now = Environment.TickCount;
        if (now - _dbgForcedTick < 300) return;
        _dbgForcedTick = now;

        ForceTransition(p.camMgr, label);
    }

    // Force a layer change on the camera to trigger the vanilla transition in place.
    // Picks a layer whose indoorf differs from the current one so the sky/background swap (the visible "black circle" effect) actually happens, mirroring a real cave enter/exit.
    // The forced state is held for 15 frames (see AdvanceForced) so it cannot be reversed by the vanilla re-sample.
    private static void ForceTransition(CamMgr cam, string label)
    {
        var cur = cam.curLayer;
        var target = cur;
        if (LayerTintCatalog.layerTintData != null)
        {
            var curIndoor = cur >= 0 && cur < LayerTintCatalog.layerTintData.Count && LayerTintCatalog.layerTintData[cur].indoors;
            for (var i = 0; i < LayerTintCatalog.layerTintData.Count; i++)
                if (LayerTintCatalog.layerTintData[i].indoors != curIndoor)
                {
                    target = i;
                    break;
                }
        }

        if (target == cur)
            if (LayerTintCatalog.layerTintData != null)
                target = (cur + 1) % LayerTintCatalog.layerTintData.Count;

        if (label == "P1")
        {
            _forcedP1Prev = cur;
            _forcedP1Cur = target;
            // Start just above 0 so DrawBackground's hold branch (ltf > 0 && ltf < 1) keeps the outdoor bg on frame 1 instead of flashing the cave bg.
            _forcedP1Ltf = 0.001f;
        }
        else
        {
            _forcedP2Prev = cur;
            _forcedP2Cur = target;
            _forcedP2Ltf = 0.001f;
        }

        Log($"[Splitscreen] DEBUG: forced transition for {label}: {cur} -> {target} (ltf 0, held 15 frames)");
    }

    private static bool IsMidTransition(CamMgr cam)
    {
        return cam != null && cam.curLayer != cam.prevLayer && cam.layerTransitionFrame < 1f;
    }

    private static string PlayerLabel(CamMgr cam)
    {
        if (PlayerMgr.player == null) return "?";
        return PlayerMgr.player.Length switch
        {
            > 0 when cam == PlayerMgr.player[0].camMgr => "P1",
            > 1 when cam == PlayerMgr.player[1].camMgr => "P2",
            _ => "?"
        };
    }
}