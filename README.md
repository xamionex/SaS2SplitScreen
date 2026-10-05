# SaS2SplitScreen

## (Testing)

Adds vertical splitscreen support for local co-op in Salt and Sacrifice, configurable
using [Mod Options](https://www.nexusmods.com/saltandsacrifice/mods/40)

Made with https://github.com/xamionex/SaltAndSacrificeBepInExTemplate

## Auto-Disable

Optional (both off by default), under the **Auto-Disable** tab in Mod Options. While active, the view falls back to the
normal shared-screen co-op camera and splits again when it stops applying.

| Option                              | What it does                                                                                                                                                                                                                                 |
|-------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Auto-Disable When Players Are Close | Merge the screens while the players are close together.                                                                                                                                                                                      |
| Auto-Disable in Boss-esque Fights   | Merge the screens while the game itself controls the camera (boss arenas and intros, cutscenes, NPC/script focus, deaths, big messages, resting). Only while the players are close, since one camera cannot frame players who are far apart. |
| Auto-Disable Distance               | What "close" means: **Screen Width** (both players fit on one screen) or **Custom Distance**.                                                                                                                                                |
| Custom Distance (meters)            | Used by Custom Distance. Straight-line distance between the characters. A player is 1.8 m tall, so a meter is the player's height divided by 1.8. Anything above what fits on one screen has no extra effect.                                |

Players on different layers (for example one inside a cave) are never merged. The exact size of one screen in meters is
written to the log as `[Splitscreen] Auto-disable scale`.

## Developer diagnostics

The BepInEx config entry `Debug > Diagnostics` (default off, not shown in Mod Options) enables F11 diagnostic views and
extra render logging.
