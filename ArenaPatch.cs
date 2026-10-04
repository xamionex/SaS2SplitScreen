using System;
using HarmonyLib;
using ProjectMage.gamestate;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // Detect arena deactivation in splitscreen.
    // If a boss arena deactivates because no players are counted inside, bosses linked to it become invulnerable due to the arena guard in HitManager.CheckHit.
    // In splitscreen players can be far apart, so one player might be inside while the other is outside, but if the inside player is not counted correctly the arena drops.
    [HarmonyPrefix]
    [HarmonyPatch("ProjectMage.map.arena.MapArenas", "Update")]
    private static void MapArenas_Update_Prefix(object __instance, out int __state)
    {
        __state = -2;
        if (!SplitActive) return;
        var arenas = GameSessionMgr.gameSession?.mapMgr?.arenas;
        if (arenas == null) return;
        __state = arenas.active;
    }

    [HarmonyPostfix]
    [HarmonyPatch("ProjectMage.map.arena.MapArenas", "Update")]
    private static void MapArenas_Update_Postfix(object __instance, int __state)
    {
        if (!SplitActive) return;
        var arenas = GameSessionMgr.gameSession?.mapMgr?.arenas;
        if (arenas == null) return;
        if (arenas.active == __state) return;

        var now = Environment.TickCount;
        if (now - _lastArenaDeactivateLogTick <= 500) return;
        _lastArenaDeactivateLogTick = now;
        switch (__state)
        {
            case >= 0 when arenas.active < 0:
                Warn($"[Splitscreen] Active arena {__state} was deactivated. Bosses may become invulnerable.");
                break;
            case < 0 when arenas.active >= 0:
                Log($"[Splitscreen] Arena {arenas.active} activated.");
                break;
        }
    }
}