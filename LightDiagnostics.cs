using System;
using Common;
using HarmonyLib;
using ProjectMage.gamestate;
using ProjectMage.player;
using SalmonMaps.director.bloom;
using SalmonMaps.map.glows;

namespace SaS2SplitScreen;

// ============================================================================
// Lighting diagnostics (developer tooling, only with Debug > Diagnostics enabled).
//
// One [LIGHT] line per draw pass, written just before the bloom/light-map combine, with every value that decides how lit a scene looks:
// the layer, the lighting statics set from the layer data, how many lights went into the light map, the zoom and the scroll.
// pass=P1 / P2 are the two split halves, pass=V is a single vanilla camera (the merged view).
// To compare split with vanilla at one spot, stand there with both players, read the P1 and P2 lines, press F11 until mode 9 (forced merged view), and read the V line.
// ============================================================================

internal static partial class SplitscreenPatch
{
    private static int _lightGlows0;
    private static readonly int[] LightLogTick = new int[3];

    // Count of lights that are about to be drawn into the light map this pass (Prepare consumes and clears the list).
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GlowMgr), "Prepare")]
    // ReSharper disable once InconsistentNaming
    private static void GlowMgr_Prepare_Light_Prefix(GlowMgr __instance)
    {
        if (!DiagEnabled) return;
        _lightGlows0 = __instance.list[0].totalGlows;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BloomComponent), "Draw")]
    private static void BloomComponent_Draw_Light_Prefix()
    {
        if (!DiagEnabled) return;

        try
        {
            var split = _inSplitDraw && SplitActive;
            var idx = !split ? 2 : _inP2Pass ? 1 : 0;
            var now = Environment.TickCount;
            if (now - LightLogTick[idx] < 600) return;
            LightLogTick[idx] = now;

            var cam = PlayerMgr.player?[0]?.camMgr;
            var glow = GameSessionMgr.gameSession?.mapMgr?.glowMgr;
            var tag = idx == 2 ? "V" : idx == 1 ? "P2" : "P1";

            Log($"[LIGHT] pass={tag} layer={cam?.curLayer}/{cam?.prevLayer} ltf={cam?.layerTransitionFrame:0.00} " +
                $"lightMap={BloomComponent.lightMap:0.00} glowAlpha={glow?.alpha:0.00} lightFac={glow?.lightFac:0.00} lights={_lightGlows0} " +
                $"desat={BloomComponent.lightDesat:0.00} red={BloomComponent.lightRed:0.00} blue={BloomComponent.lightBlue:0.00} sub={BloomComponent.lightSub:0.00} " +
                $"bloom(base={BloomComponent.bloomBase:0.00} int={BloomComponent.bloomIntensity:0.00} sat={BloomComponent.bloomSat:0.00} baseSat={BloomComponent.baseSat:0.00} thr={BloomComponent.bloomThreshhold:0.00} floor={BloomComponent.floorValue:0.00} extract={BloomComponent.BloomExtractBase:0.00}) " +
                $"vig=({BloomComponent.finalVignette.X:0.00},{BloomComponent.finalVignette.Y:0.00},{BloomComponent.finalVignette.Z:0.00}) " +
                $"brite=({BloomComponent.brite.X:0.00},{BloomComponent.brite.Y:0.00},{BloomComponent.brite.Z:0.00}) darkBlur={BloomComponent.darkBlur:0.00} " +
                $"zoom={ScrollManager.zoom:0.0} scroll=({ScrollManager.scroll.X:0},{ScrollManager.scroll.Y:0}) " +
                $"bounds=({ScrollManager.tL.X:0},{ScrollManager.tL.Y:0})-({ScrollManager.bR.X:0},{ScrollManager.bR.Y:0}) glowBrite={GameSessionMgr.gameSession?.mapMgr?.map?.glowBrite:0.00}");
        }
        catch (Exception e)
        {
            Warn($"[LIGHT] {e.Message}");
        }
    }

    // Forced merged view: the F11 mode label is normally drawn by the split composite, so draw it here for the vanilla path.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameSessionMgr), "Draw")]
    private static void GameSessionMgr_Draw_DiagLabel_Postfix()
    {
        if (!DiagEnabled || DiagMode == 0 || SplitActive || !ModActive) return;
        DrawDiagLabel();
    }
}