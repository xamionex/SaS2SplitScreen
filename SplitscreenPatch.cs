using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Bestiary.monsters;
using Common;
using HarmonyLib;
using Menumancer.hud;
using ProjectMage.character;
using ProjectMage.config;
using ProjectMage.director;
using ProjectMage.gamestate;
using ProjectMage.map.pickups;
using ProjectMage.player;
using SalmonMaps.director.bloom;
using SalmonMaps.map;

namespace SaS2SplitScreen;

[HarmonyPatch]
[HarmonyPatch]
internal static partial class SplitscreenPatch
{
    // Capture render targets
    private static RenderTarget2D _splitP1Targ;
    private static RenderTarget2D _splitP2Targ;
    private static bool _targCreated;

    // Skip flags for original drawing
    private static bool _skipGeneralHud;
    private static bool _skipGrapples;

    // Per-frame state
    private static bool _inSplitDraw;
    private static bool _inP2Pass;
    private static Vector2 _savedScroll;
    private static object _dgInst;
    // ReSharper disable once NotAccessedField.Local
    private static Vector2 _p1Loc, _p2Loc, _midpoint;
    private static MethodInfo _drawGameMethod;

    private static readonly Vector2 BaseCamOffset = new(0f, -100f);

    // ScrollManager.scroll
    private static bool _scrollInit;
    private static FieldInfo _scrollField;

    // Player helpers with reflection for internal methods
    private static readonly MethodInfo GetMainPlayerMethod;
    private static readonly MethodInfo GetLocalCoopPlayerMethod;
    private static readonly MethodInfo IsLocalCoopModeMethod;
    private static readonly MethodInfo PlayerGetCharacterMethod;
    private static readonly MethodInfo CharMgrGetVisMethod;
    private static readonly FieldInfo PlayerHpBarFrameField;
    private static readonly MethodInfo PlayerMgrDrawMethod = AccessTools.Method(typeof(PlayerMgr), "Draw");
    private static readonly MethodInfo CharAnimSetAnimMethod;
    private static readonly MethodInfo GetCharUseMethod;
    private static readonly FieldInfo CharRectsCharacterField;
    private static readonly Dictionary<int, float> CachedTopY = new();
    private static readonly int[] P1CharsBackup = new int[320];
    private static int _p1CharsCount;
    private static readonly int[] P2CharsBackup = new int[320];
    private static int _p2CharsCount;
    private static int _lastActiveCharsLogTick;
    private static int _lastArenaDeactivateLogTick;
    private static readonly int[] MergedActiveChars = new int[320];
    private static int _mergedActiveCharsCount;
    private static readonly bool[] P1CanGrappleChar = new bool[320];
    private static readonly bool[] P2CanGrappleChar = new bool[320];
    private static readonly float[] P1GrappleVisFrame = new float[320];

    private static readonly float[] P2GrappleVisFrame = new float[320];

    // Per-player visFrame for fixed grapple points (Grapples.grapple[]).
    // Vanilla uses a single shared Grapple.visFrame animated in Grapple.Update against ScrollManager.scroll.
    // In splitscreen scroll follows P1 during grapples.Update, so points near P2 get their visFrame hard-reset to 0 and the ring indicator disappears.
    // These arrays are indexed by slot in Grapples.grapple[] (stable within a map session) and animated by AnimatePerPlayerVisFrame using a direct can-grapple check against each player.
    // DrawGrapplesForPlayer reads these instead of g.visFrame for fixed points.
    private static readonly float[] P1GrapplePtVisFrame = new float[64];
    private static readonly float[] P2GrapplePtVisFrame = new float[64];
    private static readonly Dictionary<int, Vector2> CachedRuneDrawLoc = new();

    // MapPickup reflection caches
    private static readonly FieldInfo MapPickupFrameField = AccessTools.Field(typeof(MapPickup), "frame");

    private static readonly FieldInfo MapPickupCharIdxField = AccessTools.Field(typeof(MapPickup), "charIdx");

    private static readonly FieldInfo MapPickupRandField = AccessTools.Field(typeof(MapPickup), "Rand");

    // SaS2IndicatorsColorChanger integration
    private static Type _iccType;
    private static PropertyInfo _iccMainColorProp;
    private static PropertyInfo _iccCoopColorProp;
    private static bool _iccChecked;
    private static bool _iccColorReadSucceeded;

    static SplitscreenPatch()
    {
        GetMainPlayerMethod = AccessTools.Method(typeof(PlayerMgr), "GetMainPlayer");
        GetLocalCoopPlayerMethod = AccessTools.Method(typeof(PlayerMgr), "GetLocalCoopPlayer");
        IsLocalCoopModeMethod = AccessTools.Method(typeof(PlayerMgr), "IsLocalCoopMode");
        PlayerGetCharacterMethod = AccessTools.Method(typeof(Player), "GetCharacter");
        CharMgrGetVisMethod = AccessTools.Method(typeof(CharMgr), "GetVis", [typeof(Character), typeof(bool)]);
        PlayerHpBarFrameField = AccessTools.Field(typeof(Player), "hpBarFrame");
        CharAnimSetAnimMethod =
            AccessTools.Method(typeof(CharAnim), "SetAnim", [typeof(string), typeof(bool), typeof(bool)]);
        GetCharUseMethod = AccessTools.Method(typeof(PlayerPrompts), "GetCharUse", [typeof(Character)]);
        CharRectsCharacterField = AccessTools.Field(typeof(CharRects), "character");
    }

    // ModActive: the mod is switched on and this is local co-op in gameplay. Gameplay-rule patches (independent doors, no screen-edge tether) follow this, so the players can still walk apart and the split can come back while the view is merged.
    private static bool ModActive =>
        GlobalSettings.SplitscreenEnabled?.Value == true
        && GameState.state == 1
        && IsLocalCoop();

    // SplitActive: the split view itself is on. Everything that draws, positions cameras or merges per-player state follows this; auto-disable (AutoDisablePatch) turns it off to hand the view back to vanilla.
    private static bool SplitActive => ModActive && !_autoDisabled && !DiagForceMerged;

    internal static bool HasP2 { get; private set; }

    internal static Vector2 P1Loc => _p1Loc;
    internal static Vector2 P2Loc => _p2Loc;

    private static bool ShouldSkipIndicators { get; set; }

    private static void EnsureTargets()
    {
        if (_targCreated) return;
        if (GameDraw.sceneTarg == null) return;
        _targCreated = true;

        var gfx = GameDraw.sceneTarg.GraphicsDevice;
        var w = (int)ScrollManager.screenSize.X;
        var h = (int)ScrollManager.screenSize.Y;

        try
        {
            _splitP1Targ = FrameworkImpl.CreateRenderTarget2D("splitP1", gfx, w, h, false, ConfigMgr.surfaceFormat,
                DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
            _splitP2Targ = FrameworkImpl.CreateRenderTarget2D("splitP2", gfx, w, h, false, ConfigMgr.surfaceFormat,
                DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
            Log($"[Splitscreen] Capture targets created ({w}x{h}).");
        }
        catch (Exception e)
        {
            Warn($"[Splitscreen] EnsureTargets: {e.Message}");
        }
    }

    private static bool TryAcquireScroll()
    {
        if (_scrollInit) return _scrollField != null;
        _scrollInit = true;

        var smType = AccessTools.TypeByName("Common.ScrollManager");
        if (smType == null)
        {
            Warn("[Splitscreen] Common.ScrollManager not found.");
            return false;
        }

        _scrollField = smType.GetField("scroll", BindingFlags.Public | BindingFlags.Static);
        if (_scrollField == null || _scrollField.FieldType != typeof(Vector2))
            foreach (var f in smType.GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(Vector2) && f.Name.ToLowerInvariant().Contains("scroll"))
                {
                    _scrollField = f;
                    break;
                }

        if (_scrollField == null)
        {
            Warn("[Splitscreen] ScrollManager.scroll not found.");
            return false;
        }

        _drawGameMethod = AccessTools.Method(typeof(GameDraw), "DrawGame");
        Log($"[Splitscreen] Scroll field: ScrollManager.{_scrollField.Name}");
        return true;
    }

    private static Vector2 GetScroll()
    {
        return (Vector2)_scrollField.GetValue(null);
    }

    private static void SetScroll(Vector2 v)
    {
        _scrollField.SetValue(null, v);

        // Vanilla never changes the scroll without refreshing the cached per-depth values right after (CamMgr.Update does both together).
        ScrollManager.UpdateCannedValues();
    }

    private static Vector2 ScrollFor(Vector2 p)
    {
        return p + BaseCamOffset;
    }

    private static void TryInitIcc()
    {
        if (_iccChecked) return;
        _iccChecked = true;
        _iccType = Type.GetType("SaS2IndicatorsColorChanger.Plugin, amione.SaS2IndicatorsColorChanger");
        if (_iccType == null) return;
        _iccMainColorProp = _iccType.GetProperty("MainPlayerMarkerColor", BindingFlags.Public | BindingFlags.Static);
        _iccCoopColorProp = _iccType.GetProperty("CoopPlayerMarkerColor", BindingFlags.Public | BindingFlags.Static);
        _iccColorReadSucceeded = _iccMainColorProp != null && _iccCoopColorProp != null;
    }

    private static bool IsLocalCoop()
    {
        if (IsLocalCoopModeMethod == null) return false;
        return (bool)IsLocalCoopModeMethod.Invoke(null, null);
    }

    private static Player MainPlayer()
    {
        if (GetMainPlayerMethod == null) return null;
        return (Player)GetMainPlayerMethod.Invoke(null, null);
    }

    private static Player CoopPlayer()
    {
        if (GetLocalCoopPlayerMethod == null) return null;
        return (Player)GetLocalCoopPlayerMethod.Invoke(null, null);
    }

    private static Character GetCharacter(Player player)
    {
        if (player == null || PlayerGetCharacterMethod == null) return null;
        return (Character)PlayerGetCharacterMethod.Invoke(player, null);
    }

    private static bool CharMgrGetVis(Character c, bool param)
    {
        if (CharMgrGetVisMethod == null) return false;
        return (bool)CharMgrGetVisMethod.Invoke(null, [c, param]);
    }

    private static float GetPlayerHpBarFrame(Player player)
    {
        if (player == null || PlayerHpBarFrameField == null) return 0f;
        return (float)PlayerHpBarFrameField.GetValue(player);
    }

    private static Vector2 GetCharGrappleHeadPos(Character c)
    {
        var def = MonsterCatalog.monsterDef[c.monsterIdx];
        float boxH = def?.boxHeight ?? 80;

        var headPos = c.draw.headVec;
        var headVecValid = headPos != Vector2.Zero && Math.Abs(headPos.X - c.loc.X) < boxH * 0.6f &&
                           c.loc.Y - headPos.Y > boxH * 0.25f && c.loc.Y - headPos.Y < boxH * 1.35f;

        if (headVecValid) return headPos;

        if (c.draw.drawChest != Vector2.Zero)
        {
            var chestToHead = boxH * (def != null && def.gameMonster.giant ? 0.5f : 0.35f);
            return c.draw.drawChest + new Vector2(0f, -chestToHead);
        }

        var headOff = boxH * (def != null && def.gameMonster.giant ? 1.2f : 0.9f);
        return c.loc + new Vector2(0f, -headOff);
    }

    // BloomComponent.Draw prefix - redirect goalTarg to the active pass's capture target.
    // The combine shader also has BloomVignette, lightThresh and darkBlur parameters.
    // Vanilla never writes them, so they always hold the shader's own defaults, and the layer data behind the bloomThreshhold and darkBlur statics is never read by the game.
    // An earlier version set all three from those statics on every pass.
    // In areas whose layer data is non-zero (the lantern underhang on the first map) that changed the lighting: the sunlight vanished and only the lamps lit the scene.
    // Left alone, as in vanilla.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BloomComponent), "Draw")]
    private static void BloomDraw_Prefix(ref RenderTarget2D goalTarg)
    {
        if (!_inSplitDraw) return;
        EnsureTargets();
        goalTarg = _inP2Pass switch
        {
            false when _splitP1Targ != null => _splitP1Targ,
            true when _splitP2Targ != null => _splitP2Targ,
            _ => goalTarg
        };
    }

    // Track player positions
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CamMgr), "Update")]
    // ReSharper disable once InconsistentNaming
    private static void CamMgr_Update_Postfix(CamMgr __instance)
    {
        if (__instance != PlayerMgr.player[0].camMgr) return;

        TryAcquireScroll();
        if (!SplitActive || _scrollField == null)
        {
            HasP2 = false;
            ResetHalfCameras();
            return;
        }

        var p1 = MainPlayer();
        var p2 = CoopPlayer();

        HasP2 = p2 is { active: true, charIdx: >= 0 } && p2.charIdx < CharMgr.character.Length;

        if (p1 is { charIdx: >= 0 } && p1.charIdx < CharMgr.character.Length)
            _p1Loc = CharMgr.character[p1.charIdx].loc;
        if (HasP2)
            _p2Loc = CharMgr.character[p2.charIdx].loc;

        _midpoint = (_p1Loc + _p2Loc) * 0.5f;

        if (!HasP2) return;

        var dx = Math.Abs(_p1Loc.X - _p2Loc.X) * 0.5f;
        var dy = Math.Abs(_p1Loc.Y - _p2Loc.Y) * 0.5f;
        ScrollManager.tL = new Vector2(ScrollManager.tL.X - dx, ScrollManager.tL.Y - dy);
        ScrollManager.bR = new Vector2(ScrollManager.bR.X + dx, ScrollManager.bR.Y + dy);

        var halfDx = Math.Abs(_p1Loc.X - _p2Loc.X) * 0.5f + 500f;
        var halfDy = Math.Abs(_p1Loc.Y - _p2Loc.Y) * 0.5f + 500f;
        ScrollManager.midReal = Vector2.Max(ScrollManager.midReal, new Vector2(halfDx, halfDy));
        ScrollManager.maxReal = Vector2.Max(ScrollManager.maxReal, new Vector2(halfDx, halfDy));
    }

    // DrawGame prefix
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameDraw), "DrawGame")]
    // ReSharper disable once InconsistentNaming
    private static void DrawGame_Prefix(object __instance)
    {
        if (!SplitActive || !TryAcquireScroll() || !HasP2) return;

        ShouldSkipIndicators = true;
        _skipGeneralHud = true;
        _skipGrapples = true;

        if (!_inP2Pass)
        {
            _dgInst = __instance;
            _savedScroll = GetScroll();
            _inSplitDraw = true;
            SetScroll(HalfScroll(0));

            // Prepare P1's layer state BEFORE the pass. GameDraw.DrawGame consumes the layer globals (glowMgr.alpha, glowMgr.lightFac) at line 165 (glowMgr.Draw), which runs BEFORE its own PrepareMainEffect at line 168.
            // Single-camera vanilla therefore uses its own previous frame's values; with two passes it would use the OTHER player's.
            // The P2 pass does the same in DrawGame_Postfix.
            var p1 = PlayerMgr.player[0];
            LayerTintCatalog.PrepareMainEffect(p1.camMgr.curLayer, p1.camMgr.prevLayer, p1.camMgr.layerTransitionFrame,
                p1);
        }
        else
        {
            SetScroll(HalfScroll(1));
        }
    }

    // DrawGame postfix
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameDraw), "DrawGame")]
    private static void DrawGame_Postfix()
    {
        if (_inP2Pass) return;

        if (_inSplitDraw && (!SplitActive || !HasP2))
        {
            SetScroll(_savedScroll);
            _inSplitDraw = false;
            ShouldSkipIndicators = false;
            _skipGeneralHud = false;
            _skipGrapples = false;
            return;
        }

        if (!SplitActive || !TryAcquireScroll() || !HasP2)
        {
            ShouldSkipIndicators = false;
            _skipGeneralHud = false;
            _skipGrapples = false;
            return;
        }

        // P1's pass just finished: keep the background alpha it left behind for P1's next frame.
        CaptureBgAlpha(false);

        // F11 stage viewer: copy a pipeline stage of P1's pass before P2's pass overwrites the shared targets.
        DiagCaptureStage();

        _inP2Pass = true;
        var savedCam = PlayerMgr.player[0].camMgr;
        try
        {
            // DrawGame reads the camera through PlayerMgr.player[0], so P2's pass runs with P2's camera swapped in.
            PlayerMgr.player[0].camMgr = PlayerMgr.player[1].camMgr;

            var p2 = PlayerMgr.player[0];
            LayerTintCatalog.PrepareMainEffect(
                p2.camMgr.curLayer, p2.camMgr.prevLayer,
                p2.camMgr.layerTransitionFrame, p2);

            if (!DiagSkipP2Pass)
            {
                _drawGameMethod?.Invoke(_dgInst, null);
                CaptureBgAlpha(true);
            }
        }
        catch (Exception e)
        {
            Warn($"[Splitscreen] P2 pass: {e.Message}");
        }
        finally
        {
            PlayerMgr.player[0].camMgr = savedCam;
            _inP2Pass = false;
        }

        _inSplitDraw = false;
        SetScroll(_savedScroll);

        CompositeHalves();

        // F11 diagnostic mode 3 leaves out everything drawn after the composite, to tell a leak in the composite apart from one in the overlays.
        if (!DiagSkipOverlays)
        {
            DrawAllPerPlayerUi();
            DrawAllGrapples();

            _skipGeneralHud = false;
            SpriteTools.BeginAlpha();
            PlayerMgrDrawMethod?.Invoke(null, null);
            SpriteTools.End();
        }

        ShouldSkipIndicators = false;
        _skipGeneralHud = false;
        _skipGrapples = false;
    }

    // Composite halves
    private static void CompositeHalves()
    {
        if (_splitP1Targ == null || _splitP2Targ == null) return;

        var screenW = (int)ScrollManager.screenSize.X;
        var screenH = (int)ScrollManager.screenSize.Y;
        var halfW = screenW / 2;
        var cropX = halfW / 2;

        GameDraw.sceneTarg.GraphicsDevice.SetRenderTarget(null);

        var src = new Rectangle(cropX, 0, halfW, screenH);
        var dstL = new Rectangle(0, 0, halfW, screenH);
        var dstR = new Rectangle(halfW, 0, halfW, screenH);

        try
        {
            // Normally left = P1's pass and right = P2's pass; F11 diagnostic modes change which render each half shows.
            var leftTarg = _splitP1Targ;
            var rightTarg = _splitP2Targ;
            DiagPickTargets(ref leftTarg, ref rightTarg);

            DiagBeginComposite();
            SpriteTools.sprite.Draw(leftTarg, dstL, src, Color.White);
            SpriteTools.sprite.Draw(rightTarg, dstR, src, Color.White);
            DrawDiagInset(screenW, screenH, halfW, src);
            SpriteTools.End();
        }
        catch (Exception e)
        {
            Warn($"[Splitscreen] Composite: {e.Message}");
        }

        DrawDivider(screenW, screenH);
        DrawDiagLabel();
    }

    // Divider
    private static void DrawDivider(int screenW, int screenH)
    {
        var cx = screenW / 2f - 3f;
        try
        {
            SpriteTools.BeginAlpha();
            var bar = new StringBuilder("|");
            for (var y = 0; y < screenH; y += 16)
                Text.DrawText(bar, new Vector2(cx, y), Color.Black, 0.35f, 0);
            SpriteTools.End();
        }
        catch (Exception e)
        {
            Warn($"[Splitscreen] Divider: {e.Message}");
        }
    }

    // Logging
    private static void Log(string msg)
    {
        SaS2SplitScreen.Instance?.Log.LogInfo(msg);
    }

    private static void Warn(string msg)
    {
        SaS2SplitScreen.Instance?.Log.LogWarning(msg);
    }
}