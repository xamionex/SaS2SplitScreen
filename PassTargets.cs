using System;
using Common;
using ProjectMage.config;
using ProjectMage.director;

namespace SaS2SplitScreen;

// ============================================================================
// Separate scene render targets for P2's pass.
//
// Evidence so far: the cave-transition effect shows in P1's pass already at its first stage (the scene before refraction) and only when P2's draw pass actually runs (F11 mode 4, which skips that pass, is clean).
// Everything the log can see for P1's pass is settled, so what carries over is not the camera, the bloom values or the tint parameters; it is something the two passes share.
//
// The passes share GameDraw's five scene targets. backTarg owns a 16-bit depth buffer that nothing in the game clears explicitly, and auxTarg is created with PreserveContents.
// A pass that runs mid-transition skips the depth pre-pass entirely (neither of the indoors >= 1 / indoors <= 0 branches runs) and draws both layer sets, so it leaves those targets in a state no normal frame produces.
//
// Result: with these targets on (modes 0 to 7 last round) the leak was unchanged, so shared targets are NOT the carrier. They are now off by default and only used by F11 mode 8.
// ============================================================================

internal static partial class SplitscreenPatch
{
    private static RenderTarget2D _p2Back, _p2Aux, _p2Light, _p2Scene, _p2Post;
    private static RenderTarget2D _p1Back, _p1Aux, _p1Light, _p1Scene, _p1Post;
    private static bool _p2TargetsFailed;

    private static bool P2TargetsSwapped { get; set; }

    private static bool SizeMatches(RenderTarget2D t, int w, int h)
    {
        return t != null && t.Width == w && t.Height == h;
    }

    // This framework's RenderTarget2D has no Dispose, and the rest of the mod never releases targets either, so this only drops the references.
    private static void ReleaseP2Targets()
    {
        _p2Back = _p2Aux = _p2Light = _p2Scene = _p2Post = null;
    }

    // Creates (or re-creates after a resolution change) P2's targets to match the game's own.
    private static void EnsureP2Targets()
    {
        if (_p2TargetsFailed || GameDraw.backTarg == null) return;

        var w = GameDraw.backTarg.Width;
        var h = GameDraw.backTarg.Height;
        if (SizeMatches(_p2Back, w, h) && SizeMatches(_p2Aux, w, h) && SizeMatches(_p2Light, w, h)
            && SizeMatches(_p2Scene, w, h) && SizeMatches(_p2Post, w, h))
            return;

        try
        {
            ReleaseP2Targets();
            var gfx = GameDraw.backTarg.GraphicsDevice;
            var fmt = ConfigMgr.surfaceFormat;

            _p2Back = FrameworkImpl.CreateRenderTarget2D("backTargP2", gfx, w, h, false, fmt, DepthFormat.Depth16, 0, RenderTargetUsage.DiscardContents);
            _p2Aux = FrameworkImpl.CreateRenderTarget2D("auxTargP2", gfx, w, h, false, fmt, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _p2Light = FrameworkImpl.CreateRenderTarget2D("lightTargP2", gfx, w, h, false, fmt, DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
            _p2Scene = FrameworkImpl.CreateRenderTarget2D("sceneTargP2", gfx, w, h, false, fmt, DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
            _p2Post = FrameworkImpl.CreateRenderTarget2D("postTargP2", gfx, w, h, false, fmt, DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
            Log($"[Splitscreen] P2 scene targets created ({w}x{h}).");
        }
        catch (Exception e)
        {
            _p2TargetsFailed = true;
            ReleaseP2Targets();
            Warn($"[Splitscreen] P2 scene targets: {e.Message}");
        }
    }

    // Called right before P2's pass.
    private static void SwapToP2Targets()
    {
        P2TargetsSwapped = false;
        if (!DiagIsolationOn) return;

        EnsureP2Targets();
        if (_p2Back == null || _p2Aux == null || _p2Light == null || _p2Scene == null || _p2Post == null) return;

        _p1Back = GameDraw.backTarg;
        _p1Aux = GameDraw.auxTarg;
        _p1Light = GameDraw.lightTarg;
        _p1Scene = GameDraw.sceneTarg;
        _p1Post = GameDraw.postTarg;

        GameDraw.backTarg = _p2Back;
        GameDraw.auxTarg = _p2Aux;
        GameDraw.lightTarg = _p2Light;
        GameDraw.sceneTarg = _p2Scene;
        GameDraw.postTarg = _p2Post;
        P2TargetsSwapped = true;
    }

    // Called in the P2 pass's finally block, whatever happened in between.
    private static void RestoreP1Targets()
    {
        if (!P2TargetsSwapped) return;

        GameDraw.backTarg = _p1Back;
        GameDraw.auxTarg = _p1Aux;
        GameDraw.lightTarg = _p1Light;
        GameDraw.sceneTarg = _p1Scene;
        GameDraw.postTarg = _p1Post;
        P2TargetsSwapped = false;
    }
}