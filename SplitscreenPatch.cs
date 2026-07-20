using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Common;
using HarmonyLib;
using Menumancer.hud;
using Menumancer.UIFormat;
using ProjectMage;
using ProjectMage.character;
using ProjectMage.config;
using ProjectMage.director;
using ProjectMage.gamestate;
using ProjectMage.gamestate.arenastate;
using ProjectMage.gamestate.outro;
using ProjectMage.map.entities;
using ProjectMage.map.pickups;
using ProjectMage.particles;
using ProjectMage.gamestate.mage;
using ProjectMage.player;
using ProjectMage.player.menu;
using ProjectMage.texturesheet;
using SalmonMaps.director.bloom;
using SalmonMaps.map;
using Bestiary.monsters;

namespace SaS2SplitScreen;

[HarmonyPatch]
[HarmonyPatch]
internal static partial class SplitscreenPatch
{
    // Active guard
    private static bool SplitActive =>
        GlobalSettings.SplitscreenEnabled?.Value == true
        && GameState.state == 1
        && IsLocalCoop();

    // Capture render targets
    private static RenderTarget2D _splitP1Targ;
    private static RenderTarget2D _splitP2Targ;
    private static bool _targCreated;
    internal static bool HasP2 => _hasP2;
    internal static Vector2 P1Loc => _p1Loc;
    internal static Vector2 P2Loc => _p2Loc;

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
            _splitP1Targ = FrameworkImpl.CreateRenderTarget2D(
                "splitP1", gfx, w, h, false,
                ConfigMgr.surfaceFormat, DepthFormat.None, 0,
                RenderTargetUsage.DiscardContents);
            _splitP2Targ = FrameworkImpl.CreateRenderTarget2D(
                "splitP2", gfx, w, h, false,
                ConfigMgr.surfaceFormat, DepthFormat.None, 0,
                RenderTargetUsage.DiscardContents);
            Log($"[Splitscreen] Capture targets created ({w}x{h}).");
        }
        catch (Exception e)
        {
            Warn($"[Splitscreen] EnsureTargets: {e.Message}");
        }
    }

    // Skip flags for original drawing
    private static bool _skipIndicators = false;
    private static bool _skipGeneralHud = false;
    private static bool _skipGrapples = false;

    public static bool ShouldSkipIndicators => _skipIndicators;

    // Per-frame state
    private static bool _inSplitDraw;
    private static bool _inP2Pass;
    private static Vector2 _savedScroll;
    private static object _dgInst;
    private static Vector2 _p1Loc, _p2Loc, _midpoint;
    private static bool _hasP2;
    private static MethodInfo _drawGameMethod;

    private static readonly Vector2 _baseCamOffset = new Vector2(0f, -100f);

    // ScrollManager.scroll
    private static bool _scrollInit;
    private static FieldInfo _scrollField;

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

    private static Vector2 GetScroll() => (Vector2)_scrollField.GetValue(null);
    private static void SetScroll(Vector2 v) => _scrollField.SetValue(null, v);
    private static Vector2 ScrollFor(Vector2 p) => p + _baseCamOffset;

    // Player helpers with reflection for internal methods
    private static MethodInfo _getMainPlayerMethod;
    private static MethodInfo _getLocalCoopPlayerMethod;
    private static MethodInfo _isLocalCoopModeMethod;
    private static MethodInfo _playerGetCharacterMethod;
    private static MethodInfo _charMgrGetVisMethod;
    private static FieldInfo _playerHpBarFrameField;
    private static MethodInfo _playerMgrDrawMethod = AccessTools.Method(typeof(PlayerMgr), "Draw");
    private static MethodInfo _charAnimSetAnimMethod;
    private static MethodInfo _getCharUseMethod;
    private static FieldInfo _charRectsCharacterField;
    private static Dictionary<int, float> _cachedTopY = new Dictionary<int, float>();
    private static int[] _p1CharsBackup = new int[320];
    private static int _p1CharsCount = 0;
    private static int[] _p2CharsBackup = new int[320];
    private static int _p2CharsCount = 0;
    private static int _lastActiveCharsLogTick = 0;
    private static int _lastArenaDeactivateLogTick = 0;
    private static int[] _mergedActiveChars = new int[320];
    private static int _mergedActiveCharsCount = 0;
    private static bool[] _p1CanGrappleChar = new bool[320];
    private static bool[] _p2CanGrappleChar = new bool[320];
    private static float[] _p1GrappleVisFrame = new float[320];
    private static float[] _p2GrappleVisFrame = new float[320];
    // Per-player visFrame for fixed grapple points (Grapples.grapple[]).
    // Vanilla uses a single shared Grapple.visFrame animated in
    // Grapple.Update against ScrollManager.scroll. In splitscreen scroll
    // follows P1 during grapples.Update, so points near P2 get their
    // visFrame hard-reset to 0 and the ring indicator disappears. These
    // arrays are indexed by slot in Grapples.grapple[] (stable within a
    // map session) and animated by AnimatePerPlayerVisFrame using a direct
    // can-grapple check against each player. DrawGrapplesForPlayer reads
    // these instead of g.visFrame for fixed points.
    private static float[] _p1GrapplePtVisFrame = new float[64];
    private static float[] _p2GrapplePtVisFrame = new float[64];
    private static readonly Dictionary<int, Vector2> _cachedRuneDrawLoc = new Dictionary<int, Vector2>();

    // MapPickup reflection caches
    private static readonly FieldInfo _mapPickupFrameField =
        AccessTools.Field(typeof(MapPickup), "frame");
    private static readonly FieldInfo _mapPickupCharIdxField =
        AccessTools.Field(typeof(MapPickup), "charIdx");
    private static readonly FieldInfo _mapPickupRandField =
        AccessTools.Field(typeof(MapPickup), "Rand");

    // SaS2IndicatorsColorChanger integration
    private static Type _iccType;
    private static PropertyInfo _iccMainColorProp;
    private static PropertyInfo _iccCoopColorProp;
    private static bool _iccChecked;
    private static bool _iccColorReadSucceeded;

    private static void TryInitIcc()
    {
        if (_iccChecked) return;
        _iccChecked = true;
        _iccType = Type.GetType("SaS2IndicatorsColorChanger.Plugin, amione.SaS2IndicatorsColorChanger");
        if (_iccType == null) return;
        _iccMainColorProp = _iccType.GetProperty("MainPlayerMarkerColor",
            BindingFlags.Public | BindingFlags.Static);
        _iccCoopColorProp = _iccType.GetProperty("CoopPlayerMarkerColor",
            BindingFlags.Public | BindingFlags.Static);
        _iccColorReadSucceeded = _iccMainColorProp != null && _iccCoopColorProp != null;
    }

    static SplitscreenPatch()
    {
        _getMainPlayerMethod = AccessTools.Method(typeof(PlayerMgr), "GetMainPlayer");
        _getLocalCoopPlayerMethod = AccessTools.Method(typeof(PlayerMgr), "GetLocalCoopPlayer");
        _isLocalCoopModeMethod = AccessTools.Method(typeof(PlayerMgr), "IsLocalCoopMode");
        _playerGetCharacterMethod = AccessTools.Method(typeof(Player), "GetCharacter");
        _charMgrGetVisMethod = AccessTools.Method(typeof(CharMgr), "GetVis", new[] { typeof(Character), typeof(bool) });
        _playerHpBarFrameField = AccessTools.Field(typeof(Player), "hpBarFrame");
        _charAnimSetAnimMethod = AccessTools.Method(typeof(CharAnim), "SetAnim",
            new[] { typeof(string), typeof(bool), typeof(bool) });
        _getCharUseMethod = AccessTools.Method(typeof(PlayerPrompts), "GetCharUse",
            new[] { typeof(Character) });
        _charRectsCharacterField = AccessTools.Field(typeof(CharRects), "character");
    }

    private static bool IsLocalCoop()
    {
        if (_isLocalCoopModeMethod == null) return false;
        return (bool)_isLocalCoopModeMethod.Invoke(null, null);
    }

    private static Player MainPlayer()
    {
        if (_getMainPlayerMethod == null) return null;
        return (Player)_getMainPlayerMethod.Invoke(null, null);
    }

    private static Player CoopPlayer()
    {
        if (_getLocalCoopPlayerMethod == null) return null;
        return (Player)_getLocalCoopPlayerMethod.Invoke(null, null);
    }

    private static Character GetCharacter(Player player)
    {
        if (player == null || _playerGetCharacterMethod == null) return null;
        return (Character)_playerGetCharacterMethod.Invoke(player, null);
    }

    private static bool CharMgrGetVis(Character c, bool param)
    {
        if (_charMgrGetVisMethod == null) return false;
        return (bool)_charMgrGetVisMethod.Invoke(null, new object[] { c, param });
    }

    private static float GetPlayerHpBarFrame(Player player)
    {
        if (player == null || _playerHpBarFrameField == null) return 0f;
        return (float)_playerHpBarFrameField.GetValue(player);
    }

    private static Vector2 GetCharGrappleHeadPos(Character c)
    {
        MonsterDef def = MonsterCatalog.monsterDef[c.monsterIdx];
        float boxH = def != null ? def.boxHeight : 80;

        Vector2 headPos = c.draw.headVec;
        bool headVecValid = headPos != Vector2.Zero
                            && Math.Abs(headPos.X - c.loc.X) < boxH * 0.6f
                            && c.loc.Y - headPos.Y > boxH * 0.25f
                            && c.loc.Y - headPos.Y < boxH * 1.35f;

        if (headVecValid)
        {
            return headPos;
        }

        if (c.draw.drawChest != Vector2.Zero)
        {
            float chestToHead = boxH * (def != null && def.gameMonster.giant ? 0.5f : 0.35f);
            return c.draw.drawChest + new Vector2(0f, -chestToHead);
        }

        float headOff = boxH * (def != null && def.gameMonster.giant ? 1.2f : 0.9f);
        return c.loc + new Vector2(0f, -headOff);
    }

    // BloomComponent.Draw prefix - redirect goalTarg
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BloomComponent), "Draw")]
    private static void BloomDraw_Prefix(ref RenderTarget2D goalTarg)
    {
        if (!_inSplitDraw) return;
        EnsureTargets();
        if (!_inP2Pass && _splitP1Targ != null) goalTarg = _splitP1Targ;
        else if (_inP2Pass && _splitP2Targ != null) goalTarg = _splitP2Targ;
    }

    // Track player positions
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CamMgr), "Update")]
    private static void CamMgr_Update_Postfix(CamMgr __instance)
    {
        if (__instance != PlayerMgr.player[0].camMgr) return;

        TryAcquireScroll();
        if (!SplitActive)
        {
            _hasP2 = false;
            return;
        }

        if (_scrollField == null)
        {
            _hasP2 = false;
            return;
        }

        var p1 = MainPlayer();
        var p2 = CoopPlayer();

        _hasP2 = p2 != null && p2.active && p2.charIdx >= 0
                 && p2.charIdx < CharMgr.character.Length;

        if (p1 != null && p1.charIdx >= 0 && p1.charIdx < CharMgr.character.Length)
            _p1Loc = CharMgr.character[p1.charIdx].loc;
        if (_hasP2)
            _p2Loc = CharMgr.character[p2.charIdx].loc;

        _midpoint = (_p1Loc + _p2Loc) * 0.5f;

        if (_hasP2)
        {
            float dx = Math.Abs(_p1Loc.X - _p2Loc.X) * 0.5f;
            float dy = Math.Abs(_p1Loc.Y - _p2Loc.Y) * 0.5f;
            ScrollManager.tL = new Vector2(ScrollManager.tL.X - dx, ScrollManager.tL.Y - dy);
            ScrollManager.bR = new Vector2(ScrollManager.bR.X + dx, ScrollManager.bR.Y + dy);
        }

        if (_hasP2)
        {
            float halfDx = Math.Abs(_p1Loc.X - _p2Loc.X) * 0.5f + 500f;
            float halfDy = Math.Abs(_p1Loc.Y - _p2Loc.Y) * 0.5f + 500f;
            ScrollManager.midReal = Vector2.Max(ScrollManager.midReal, new Vector2(halfDx, halfDy));
            ScrollManager.maxReal = Vector2.Max(ScrollManager.maxReal, new Vector2(halfDx, halfDy));
        }
    }

    // DrawGame prefix
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameDraw), "DrawGame")]
    private static void DrawGame_Prefix(object __instance)
    {
        if (!SplitActive || !TryAcquireScroll() || !_hasP2) return;

        _skipIndicators = true;
        _skipGeneralHud = true;
        _skipGrapples = true;

        if (!_inP2Pass)
        {
            _dgInst = __instance;
            _savedScroll = GetScroll();
            _inSplitDraw = true;
            SetScroll(ScrollFor(_p1Loc));
        }
        else
        {
            SetScroll(ScrollFor(_p2Loc));
        }
    }

    // DrawGame postfix
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameDraw), "DrawGame")]
    private static void DrawGame_Postfix()
    {
        if (_inP2Pass) return;

        if (_inSplitDraw && (!SplitActive || !_hasP2))
        {
            SetScroll(_savedScroll);
            _inSplitDraw = false;
            _skipIndicators = false;
            _skipGeneralHud = false;
            _skipGrapples = false;
            return;
        }

        if (!SplitActive || !TryAcquireScroll() || !_hasP2)
        {
            _skipIndicators = false;
            _skipGeneralHud = false;
            _skipGrapples = false;
            return;
        }

        _inP2Pass = true;
        var savedCam = PlayerMgr.player[0].camMgr;
        try
        {
            PlayerMgr.player[0].camMgr = PlayerMgr.player[1].camMgr;

            var p2 = PlayerMgr.player[0];
            LayerTintCatalog.PrepareMainEffect(
                p2.camMgr.curLayer, p2.camMgr.prevLayer,
                p2.camMgr.layerTransitionFrame, p2);

            _drawGameMethod?.Invoke(_dgInst, null);
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

        DrawAllPerPlayerUI();
        DrawAllGrapples();

        _skipGeneralHud = false;
        SpriteTools.BeginAlpha();
        _playerMgrDrawMethod?.Invoke(null, null);
        SpriteTools.End();

        _skipIndicators = false;
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
            SpriteTools.BeginAlpha();
            SpriteTools.sprite.Draw(_splitP1Targ, dstL, src, Color.White);
            SpriteTools.sprite.Draw(_splitP2Targ, dstR, src, Color.White);
            SpriteTools.End();
        }
        catch (Exception e)
        {
            Warn($"[Splitscreen] Composite: {e.Message}");
        }

        DrawDivider(screenW, screenH);
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
    private static void Log(string msg) => SaS2SplitScreen.Instance?.Log.LogInfo(msg);
    private static void Warn(string msg) => SaS2SplitScreen.Instance?.Log.LogWarning(msg);
}
