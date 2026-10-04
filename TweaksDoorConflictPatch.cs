using System;
using System.Reflection;
using HarmonyLib;
using ProjectMage.player;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // Cross-mod guard against SaS2Tweaks "P2 Can Trigger Doors".
    //
    // SaS2Tweaks transpiles CharMovement.MoveCoopPlayers so that when P2 crosses a layer-change door, HandleP2LayerChange teleports P1 to P2's destination (P2CanTriggerDoors == true).
    // That behavior directly conflicts with this mod's IndependentDoors option, which is supposed to let each player travel through doors without dragging the other along.
    //
    // When splitscreen is active we force the Tweaks config to false.
    // With the config false, HandleP2LayerChange early-returns after doing movedChar.loc = pLoc, and this mod's MoveCoopPlayers_Prefix has already rewritten pLoc to movedChar.loc, so the assignment is a no-op and each player stays where they are.
    //
    // When splitscreen is inactive we do not touch the Tweaks value, so the user's preference applies in normal co-op.
    // This is "automatic force off" per the user's instruction: the Tweaks option is forced off for the duration of splitscreen and the user is free to re-enable it afterwards.
    private static Type _tweaksGlobalSettingsType;
    private static FieldInfo _tweaksP2DoorsField;
    private static PropertyInfo _configEntryValueProp;
    private static bool _tweaksChecked;
    private static bool _tweaksForceOffLogged;

    private static void TryInitTweaks()
    {
        if (_tweaksChecked) return;
        _tweaksChecked = true;

        _tweaksGlobalSettingsType = Type.GetType("SaS2Tweaks.GlobalSettings, amione.SaS2Tweaks");
        if (_tweaksGlobalSettingsType == null) return;

        _tweaksP2DoorsField =
            _tweaksGlobalSettingsType.GetField("P2CanTriggerDoors", BindingFlags.Public | BindingFlags.Static);
        if (_tweaksP2DoorsField == null) return;

        // ConfigEntry<bool>.Value is a public property on the generic type.
        // We resolve it off the live instance rather than the open generic.
        var entry = _tweaksP2DoorsField.GetValue(null);
        if (entry == null) return;
        _configEntryValueProp = entry.GetType().GetProperty("Value");
    }

    private static void ForceTweaksP2DoorsOff()
    {
        TryInitTweaks();
        if (_tweaksP2DoorsField == null || _configEntryValueProp == null) return;

        var entry = _tweaksP2DoorsField.GetValue(null);
        if (entry == null) return;

        try
        {
            var current = (bool)_configEntryValueProp.GetValue(entry, null);
            if (!current) return;

            _configEntryValueProp.SetValue(entry, false, null);
            if (_tweaksForceOffLogged) return;
            _tweaksForceOffLogged = true;
            Log("[Splitscreen] SaS2Tweaks 'P2 Can Trigger Doors' auto-disabled (it conflicts with splitscreen independent doors). Re-enable it in Mod Options after disabling splitscreen.");
        }
        catch (Exception e)
        {
            // SoftDependency: if Tweaks internals change, fail silently.
            _tweaksP2DoorsField = null;
            Warn($"[Splitscreen] Could not override SaS2Tweaks P2CanTriggerDoors: {e.Message}");
        }
    }

    // Per-frame hook. CamMgr.Update for P1 runs every frame in gameplay; we piggyback on the existing postfix so we don't add another patch.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CamMgr), "Update")]
    // ReSharper disable once InconsistentNaming
    private static void CamMgr_Update_TweaksGuard_Postfix(CamMgr __instance)
    {
        // Only run on P1's camera, mirroring CamMgr_Update_Postfix.
        if (__instance != PlayerMgr.player[0].camMgr) return;

        if (SplitActive)
            ForceTweaksP2DoorsOff();
        else
            _tweaksForceOffLogged = false;
    }
}