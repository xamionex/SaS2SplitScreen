using System;
using System.Linq;
using System.Text;
using Common;
using HarmonyLib;
using Menumancer.hud;
using ProjectMage.config;
using ProjectMage.director;
using ProjectMage.player;

namespace SaS2SplitScreen;

// ============================================================================
// Diagnostic views (developer tooling, off by default).
//
// Enabled by the BepInEx config entry Debug > Diagnostics (not shown in Mod Options). While it is off, DiagMode is always 0 and nothing here runs.
// When it is on, F11 cycles the modes below; the active mode is printed in red top-left and logged as [DIAG]. Every mode changes only how the already-rendered pass targets are composited.
//
//   0  normal
//   1  P1's pass, stage 1: scene before refraction        (GameDraw.backTarg)
//   2  P1's pass, stage 2: after refraction + foreground  (GameDraw.auxTarg)
//   3  P1's pass, stage 3: post-processed, before bloom   (GameDraw.sceneTarg)
//   4  P2's pass SKIPPED; P1's final render on both halves; P2's layer state printed as text
//   5  P1's final render on both halves (inset: P2)
//   6  P2's final render on both halves (inset: P1)
//   7  normal composite, nothing drawn after it
//   8  normal view with the background-alpha fix OFF, for A/B (see BackgroundAlphaPatch.cs)
//   9  FORCED merged view: the vanilla single camera, for comparing split against vanilla at the same spot
//
// Use: put the pass you want to inspect on screen, then provoke the effect with the OTHER player (e.g. walk them through a cave entrance).
// ============================================================================

internal static partial class SplitscreenPatch
{
    private const int ModeStageBack = 1;
    private const int ModeStageAux = 2;
    private const int ModeStageScene = 3;
    private const int ModeSkipP2 = 4;
    private const int ModeP1Both = 5;
    private const int ModeP2Both = 6;
    private const int ModeNoOverlays = 7;
    private const int ModeBgAlphaFixOff = 8;
    private const int ModeForceMerged = 9;

    private static bool _diagKeyDown;

    private static readonly string[] DiagLabels =
    [
        "normal",
        "P1 stage 1: scene before refraction (backTarg)",
        "P1 stage 2: after refraction + foreground (auxTarg)",
        "P1 stage 3: post-processed, before bloom (sceneTarg)",
        "P2 pass SKIPPED, P1 on both halves",
        "P1 final on both halves (inset: P2)",
        "P2 final on both halves (inset: P1)",
        "composite only, no overlays/HUD",
        "background-alpha fix OFF (previous behaviour)",
        "FORCED merged view (vanilla single camera) for A/B"
    ];

    // One reusable copy target for whichever pipeline stage is being viewed.
    private static RenderTarget2D _diagStage;
    private static bool _diagStageFailed;

    private static int _diagMode;

    private static bool DiagEnabled => GlobalSettings.Diagnostics?.Value == true;

    // Single choke point: with diagnostics disabled every mode check below sees 0 (normal), even if the config is switched off while a mode is active.
    private static int DiagMode => DiagEnabled ? _diagMode : 0;

    // Set during the update phase when any player's camera is mid-transition. The draw-phase camMgr swap hides P1's camera during P2's pass, so the loggers read this instead.
    private static bool _anyCamTransitionLive;

    private static bool DiagSkipOverlays => DiagMode == ModeNoOverlays;
    private static bool DiagSkipP2Pass => DiagMode == ModeSkipP2;
    private static bool DiagBgAlphaFixOff => DiagMode == ModeBgAlphaFixOff;

    // Mode 9 hands the whole view back to vanilla, so the same spot can be compared split and merged with one key.
    private static bool DiagForceMerged => DiagMode == ModeForceMerged;
    private static bool DiagStageMode => DiagMode >= ModeStageBack && DiagMode <= ModeStageScene;

    // Runs on every camera update; the key is polled once per frame, from P1's camera (which updates every tick in splitscreen).
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CamMgr), "Update")]
    // ReSharper disable once InconsistentNaming
    private static void CamMgr_Update_Diag_Postfix(CamMgr __instance)
    {
        if (!DiagEnabled)
        {
            _diagMode = 0;
            _anyCamTransitionLive = false;
            return;
        }

        // ModActive, not SplitActive: the key has to keep working while the forced merged view is on.
        if (!ModActive) return;

        UpdateTransitionLiveFlag();

        if (__instance != PlayerMgr.player[0].camMgr) return;
        PollDiagKey();
    }

    private static void UpdateTransitionLiveFlag()
    {
        _anyCamTransitionLive = false;
        if (PlayerMgr.player == null) return;

        if (PlayerMgr.player.Select(player => player?.camMgr).Any(cm =>
                cm != null && cm.curLayer != cm.prevLayer && !(cm.layerTransitionFrame >= 1f)))
            _anyCamTransitionLive = true;
    }

    // Edge-detected once per frame.
    private static void PollDiagKey()
    {
        var isDown = FrameworkImpl.GetKeyState(Keys.F11) == KeyState.Down;
        var pressed = isDown && !_diagKeyDown;
        _diagKeyDown = isDown;
        if (!pressed) return;

        _diagMode = (_diagMode + 1) % DiagLabels.Length;
        Log($"[DIAG] F11 view mode {_diagMode}: {DiagLabels[_diagMode]}");
    }

    private static void EnsureDiagStage()
    {
        if (_diagStage != null || _diagStageFailed) return;
        if (GameDraw.sceneTarg == null) return;
        try
        {
            var gfx = GameDraw.sceneTarg.GraphicsDevice;
            var w = (int)ScrollManager.screenSize.X;
            var h = (int)ScrollManager.screenSize.Y;
            _diagStage = FrameworkImpl.CreateRenderTarget2D(
                "diagStage", gfx, w, h, false,
                ConfigMgr.surfaceFormat, DepthFormat.None, 0,
                RenderTargetUsage.DiscardContents);
            Log($"[DIAG] stage copy target created ({w}x{h}).");
        }
        catch (Exception e)
        {
            _diagStageFailed = true;
            Warn($"[DIAG] stage target: {e.Message}");
        }
    }

    // Runs right after P1's pass finishes and before P2's pass starts, so the shared pipeline targets still hold P1's content.
    private static void DiagCaptureStage()
    {
        if (!DiagStageMode) return;
        try
        {
            EnsureDiagStage();
            if (_diagStage == null) return;

            var src = DiagMode == ModeStageBack ? GameDraw.backTarg
                : DiagMode == ModeStageAux ? GameDraw.auxTarg
                : GameDraw.sceneTarg;
            if (src == null) return;

            var gfx = src.GraphicsDevice;
            gfx.SetRenderTarget(_diagStage);
            gfx.Clear(new Color(0f, 0f, 0f, 1f));
            SpriteTools.BeginOpaque(null);
            SpriteTools.sprite.Draw(src, new Rectangle(0, 0, _diagStage.Width, _diagStage.Height), Color.White);
            SpriteTools.End();
        }
        catch (Exception e)
        {
            Warn($"[DIAG] stage capture: {e.Message}");
        }
    }

    // Chooses what each half of the composite shows.
    private static void DiagPickTargets(ref RenderTarget2D left, ref RenderTarget2D right)
    {
        switch (DiagMode)
        {
            case ModeStageBack:
            case ModeStageAux:
            case ModeStageScene:
                if (_diagStage != null)
                {
                    left = _diagStage;
                    right = _diagStage;
                }

                break;
            case ModeSkipP2:
            case ModeP1Both:
                right = _splitP1Targ;
                break;
            case ModeP2Both:
                left = _splitP2Targ;
                break;
        }
    }

    // The split targets are written by the bloom combine with an opaque blend (as vanilla writes the backbuffer), so whatever alpha the shader outputs ends up in them.
    // Vanilla never sees that alpha because the backbuffer ignores it.
    // Compositing the halves with an alpha blend let it through: wherever the scene's alpha was below 1 the picture was blended with the black backbuffer, which darkened and "fogged" the lit areas.
    // The halves therefore have to be composited opaque, exactly like the combine pass itself.
    private static void DiagBeginComposite()
    {
        SpriteTools.BeginOpaque(null);
    }

    // Called inside the composite's sprite batch.
    private static void DrawDiagInset(int screenW, int screenH, int halfW, Rectangle src)
    {
        var p2Inset = DiagStageMode || DiagMode == ModeP1Both;
        var p1Inset = DiagMode == ModeP2Both;
        if (!p2Inset && !p1Inset) return;

        var w = (int)(halfW * 0.3f);
        var h = (int)(screenH * 0.3f);
        const int margin = 12;

        if (p2Inset)
        {
            var dst = new Rectangle(screenW - w - margin, screenH - h - margin, w, h);
            SpriteTools.sprite.Draw(_splitP2Targ, dst, src, Color.White);
        }
        else
        {
            var dst = new Rectangle(margin, screenH - h - margin, w, h);
            SpriteTools.sprite.Draw(_splitP1Targ, dst, src, Color.White);
        }
    }

    private static void DrawDiagLabel()
    {
        if (DiagMode == 0) return;
        try
        {
            SpriteTools.BeginAlpha();
            var red = new Color(1f, 0f, 0f, 1f);
            Text.DrawText(new StringBuilder("F11 DIAG " + DiagMode + ": " + DiagLabels[DiagMode]),
                new Vector2(12f, 12f), red, 0.35f, 0);

            // With P2's pass skipped nothing shows P2, so print its layer state to steer by.
            if (DiagMode == ModeSkipP2 && PlayerMgr.player[1] != null && PlayerMgr.player[1].camMgr != null)
            {
                var c = PlayerMgr.player[1].camMgr;
                Text.DrawText(
                    new StringBuilder("P2 layer cur=" + c.curLayer + " prev=" + c.prevLayer + " ltf=" +
                                      c.layerTransitionFrame.ToString("0.00")),
                    new Vector2(12f, 44f), red, 0.35f, 0);
            }

            SpriteTools.End();
        }
        catch (Exception e)
        {
            Warn($"[DIAG] label: {e.Message}");
        }
    }
}