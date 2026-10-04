using System;
using System.Text;
using Common;
using Menumancer.hud;
using ProjectMage.config;
using ProjectMage.director;
using ProjectMage.player;

namespace SaS2SplitScreen;

// ============================================================================
// F11 diagnostic view (debug build).
//
// Result of the previous round: the effect is already inside the NON-crossing player's own render target (modes "P1 on both halves" and "P2 on both halves" both showed it), while every value the log can see for that pass is settled.
// This round bisects INSIDE that pass. Each F11 press cycles the modes below; the mode is printed in red top-left and logged as [DIAG].
//
// Test for every mode: park P2 next to a cave boundary, then walk P2 in and out while watching the BIG view. Ignore the small inset (it is the crossing player and is expected to show the effect).
//
//   1  P1's pass, stage 1: scene before refraction        (GameDraw.backTarg)
//   2  P1's pass, stage 2: after refraction + foreground  (GameDraw.auxTarg)
//   3  P1's pass, stage 3: post-processed, before bloom   (GameDraw.sceneTarg)
//   4  P2's pass SKIPPED; P1's final render on both halves; P2's layer state printed as text
//   5  P1's final render on both halves (inset: P2)        (previous mode 1)
//   6  P2's final render on both halves (inset: P1)        (previous mode 2)
//   7  normal composite, nothing drawn after it            (previous mode 3)
//   8  normal view with separate P2 scene targets ON (did not matter last round)
//   9  normal view with the per-player background-alpha fix OFF, for A/B against mode 0 (see BackgroundAlphaPatch.cs)
//
// Reading the results:
//   effect first appears in stage N          -> it is produced between stage N-1 and N (1 = map/particles/chars, 2 = refraction + foreground layers + glows, 3 = post effect, none = bloom/light/vignette).
//   effect gone in mode 4                    -> carried over from the previous P2 DRAW pass.
//   effect still there in mode 4             -> comes from world/update state that real crossings change, not from draw state.
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
    private const int ModeIsolationOn = 8;
    private const int ModeBgAlphaFixOff = 9;

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
        "separate P2 scene targets ON (test only)",
        "background-alpha fix OFF (previous behaviour)"
    ];

    // One reusable copy target for whichever pipeline stage is being viewed.
    private static RenderTarget2D _diagStage;
    private static bool _diagStageFailed;

    private static int DiagMode { get; set; }

    private static bool DiagSkipOverlays => DiagMode == ModeNoOverlays;
    private static bool DiagSkipP2Pass => DiagMode == ModeSkipP2;
    private static bool DiagIsolationOn => DiagMode == ModeIsolationOn;
    private static bool DiagBgAlphaFixOff => DiagMode == ModeBgAlphaFixOff;
    private static bool DiagStageMode => DiagMode >= ModeStageBack && DiagMode <= ModeStageScene;

    // Edge-detected once per frame from the same hook as F9/F10.
    private static void PollDiagKey()
    {
        var isDown = FrameworkImpl.GetKeyState(Keys.F11) == KeyState.Down;
        var pressed = isDown && !_diagKeyDown;
        _diagKeyDown = isDown;
        if (!pressed) return;

        DiagMode = (DiagMode + 1) % DiagLabels.Length;
        Log($"[DIAG] F11 view mode {DiagMode}: {DiagLabels[DiagMode]}");
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

    // The stage copies can have partial alpha, so they are composited opaque; every other mode keeps the normal alpha composite.
    private static void DiagBeginComposite()
    {
        if (DiagStageMode) SpriteTools.BeginOpaque(null);
        else SpriteTools.BeginAlpha();
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