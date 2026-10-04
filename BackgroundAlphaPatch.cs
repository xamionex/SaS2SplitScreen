using System;
using HarmonyLib;
using ProjectMage.director;
using ProjectMage.player;
using SalmonMaps.map;

namespace SaS2SplitScreen;

// ============================================================================
// Per-player background alpha.
//
// The tint effect used for the sky/background (GameDraw.bloomTintGradientEffect) has an alpha parameter "a" that only MapDraw.ResetLayerTintEffect ever writes, and it is written with the alpha of the map layer being drawn (1 - indoors for the outdoor layers while a cave transition is running).
// GameDraw.DrawBackground, the first draw of a pass, never sets "a"; it relies on whatever the previous draw left there.
// In the vanilla game that is the same camera's previous frame. With two passes per frame it is the OTHER player's pass, so the player who is mid-transition hands their fading alpha to the next pass, and the effect shows on a player who never moved.
//
// Evidence for it: the effect is present already at the first stage of the other player's pass, only appears when the crossing player's draw pass really runs, survives separate render targets, and every logged input of the affected pass is settled.
//
// Fix: remember the value each pass leaves behind and put the player's OWN value back before that player's DrawBackground, which is exactly what a single-camera game does. F11 mode 9 turns the fix off for an A/B.
// ============================================================================

internal static partial class SplitscreenPatch
{
    private static float _bgAlphaP1 = 1f;
    private static float _bgAlphaP2 = 1f;
    private static bool _bgAlphaFailed;

    // What DrawBackground found in the parameter before the fix touched it (for the log).
    private static float _bgAlphaSeen = 1f;
    private static float _bgAlphaApplied = 1f;

    // Remember what the pass that just finished left in the shared effect.
    private static void CaptureBgAlpha(bool p2)
    {
        if (_bgAlphaFailed || GameDraw.bloomTintGradientEffect == null) return;
        try
        {
            var a = GameDraw.bloomTintGradientEffect.Parameters["a"].GetValueSingle();
            if (p2) _bgAlphaP2 = a;
            else _bgAlphaP1 = a;
        }
        catch (Exception e)
        {
            _bgAlphaFailed = true;
            Warn($"[Splitscreen] bg alpha capture: {e.Message}");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameDraw), "DrawBackground", typeof(Map), typeof(Player))]
    private static void DrawBackground_BgAlpha_Prefix()
    {
        if (!_inSplitDraw || !SplitActive || _bgAlphaFailed) return;
        if (GameDraw.bloomTintGradientEffect == null) return;
        try
        {
            var prm = GameDraw.bloomTintGradientEffect.Parameters["a"];
            _bgAlphaSeen = prm.GetValueSingle();
            _bgAlphaApplied = _bgAlphaSeen;
            if (DiagBgAlphaFixOff) return;

            _bgAlphaApplied = _inP2Pass ? _bgAlphaP2 : _bgAlphaP1;
            prm.SetValue(_bgAlphaApplied);
        }
        catch (Exception e)
        {
            _bgAlphaFailed = true;
            Warn($"[Splitscreen] bg alpha apply: {e.Message}");
        }
    }
}