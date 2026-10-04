using System;
using System.Runtime.CompilerServices;
using Common;
using HarmonyLib;
using ProjectMage.director;
using ProjectMage.gamestate;
using ProjectMage.particles;
using ProjectMage.player;
using SalmonMaps.director;
using SalmonMaps.director.bloom;
using SalmonMaps.map;

namespace SaS2SplitScreen;

// ============================================================================
// Per-pass render instrumentation (developer tooling, off by default).
//
// Logs what each split-screen pass draws the background with ([BG]) and the shared bloom/light/particle globals ([GLOBAL]).
// Only active with the BepInEx config entry Debug > Diagnostics enabled; otherwise both patches return immediately.
// ============================================================================

internal static partial class SplitscreenPatch
{
    private static int _lastBgP1 = int.MinValue;
    private static int _lastBgP2 = int.MinValue;
    private static int _lastLogTickP1;
    private static int _lastLogTickP2;

    // [GLOBAL] is unthrottled while any camera is mid-transition (flag set in the update phase), so a run captures every frame of both passes.
    private static int _lastSharedTick;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameDraw), "DrawBackground", typeof(Map), typeof(Player))]
    private static void DrawBackground_Postfix(Map map, Player p)
    {
        if (!DiagEnabled) return;
        try
        {
            if (!IsLocalCoop() || !SplitActive) return;
            if (map == null || p == null || p.camMgr == null) return;

            var cm = p.camMgr;
            var pass = _inP2Pass ? "P2" : "P1";
            var bg = map.background;

            var bgChanged = pass == "P1" ? bg != _lastBgP1 : bg != _lastBgP2;
            if (pass == "P1") _lastBgP1 = bg;
            else _lastBgP2 = bg;

            var changing = cm.curLayer != cm.prevLayer;
            var now = Environment.TickCount;

            // Each pass has its own throttle tick. A shared one let the transitioning pass starve the other pass's lines, which hid exactly the state that matters. While any camera is mid-transition both passes log every frame.
            var last = pass == "P1" ? _lastLogTickP1 : _lastLogTickP2;
            if (!bgChanged && !changing && !_anyCamTransitionLive && now - last <= 250) return;
            if (pass == "P1") _lastLogTickP1 = now;
            else _lastLogTickP2 = now;
            Log($"[BG] pass={pass} cam={RuntimeHelpers.GetHashCode(cm):x8} " +
                $"cur={cm.curLayer} prev={cm.prevLayer} " +
                $"ltf={cm.layerTransitionFrame:0.00} ind={cm.indoors:0.00} bg={bg} " +
                $"scroll=({ScrollManager.scroll.X:0},{ScrollManager.scroll.Y:0}) diag={DiagMode} " +
                $"back={RuntimeHelpers.GetHashCode(GameDraw.backTarg):x8} " +
                $"aSeen={_bgAlphaSeen:0.00} aUsed={_bgAlphaApplied:0.00}");
        }
        catch (Exception e)
        {
            Warn($"[BG] log: {e.Message}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameDraw), "DrawGame")]
    private static void DrawGame_Log_Postfix()
    {
        if (!DiagEnabled) return;
        if (!IsLocalCoop() || !SplitActive) return;
        var live = _anyCamTransitionLive;
        var now = Environment.TickCount;
        if (!live && now - _lastSharedTick < 250) return;
        _lastSharedTick = now;

        try
        {
            var mapMgr = GameSessionMgr.gameSession?.mapMgr;
            var glow = mapMgr?.glowMgr;
            var pass = _inP2Pass ? "P2" : "P1";

            int p1 = 0, p2 = 0, p4 = 0, p5 = 0, p9 = 0;
            try
            {
                if (ParticleManager.particleList != null)
                {
                    p1 = ParticleManager.particleList[1].totalParticles;
                    p2 = ParticleManager.particleList[2].totalParticles;
                    p4 = ParticleManager.particleList[4].totalParticles;
                    p5 = ParticleManager.particleList[5].totalParticles;
                    p9 = ParticleManager.particleList[9].totalParticles;
                }
            }
            catch
            {
                // ignored
            }

            Log($"[GLOBAL] pass={pass} " +
                $"glowAlpha={glow?.alpha ?? -1f:0.00} " +
                $"lightFac={glow?.lightFac ?? -1f:0.00} " +
                $"bloomBase={BloomComponent.bloomBase:0.00} " +
                $"bloomIntensity={BloomComponent.bloomIntensity:0.00} " +
                $"bloomSat={BloomComponent.bloomSat:0.00} " +
                $"finalVig=({BloomComponent.finalVignette.X:0.00},{BloomComponent.finalVignette.Y:0.00}) " +
                $"brite=({BloomComponent.brite.X:0.00},{BloomComponent.brite.Y:0.00}) " +
                $"lightMap={BloomComponent.lightMap:0.00} darkBlur={BloomComponent.darkBlur:0.00} " +
                $"waterLev={Water.waterLev:0.00} mapBrite={mapMgr?.map?.brite ?? -1f:0.00} " +
                $"ptl1={p1} ptl2={p2} ptl4={p4} ptl5={p5} ptl9={p9}");
        }
        catch (Exception e)
        {
            Warn($"[GLOBAL] log: {e.Message}");
        }
    }
}