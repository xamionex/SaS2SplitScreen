using BepInEx.Configuration;

namespace SaS2SplitScreen;

public static class GlobalSettings
{
    // Auto-disable: fall back to the normal shared-screen co-op camera while it is the better view.
    private const string ModeScreenWidth = "Screen Width";
    public const string ModeCustomDistance = "Custom Distance";
    public static ConfigEntry<bool> SplitscreenEnabled;
    public static ConfigEntry<bool> IndependentDoors;
    public static ConfigEntry<bool> Diagnostics;
    public static readonly string[] DistanceModes = [ModeScreenWidth, ModeCustomDistance];

    public static ConfigEntry<bool> AutoDisableWhenClose;
    public static ConfigEntry<bool> AutoDisableInBossFights;
    public static ConfigEntry<string> AutoDisableDistanceMode;
    public static ConfigEntry<int> AutoDisableCustomDistance;

    public static void Bind(ConfigFile cfg)
    {
        const string section = "Splitscreen";
        SplitscreenEnabled = cfg.Bind(section, "Splitscreen", true,
            "Splits the screen vertically in local co-op.\n" +
            "Left half shows Player 1's view; right half shows Player 2's view.");
        IndependentDoors = cfg.Bind(section, "Allow P1 and P2 to travel through doors independently", true,
            "When enabled, walking through a layer-change door moves only the triggering player.\n" +
            "The other player stays where they are.");
        AutoDisableWhenClose = cfg.Bind(section, "AutoDisableWhenClose", false,
            "Temporarily switches back to the normal shared screen while the players are close together, and splits again as soon as they move apart.\n" +
            "\"Close\" is decided by AutoDisableDistanceMode. Players on different layers (one inside a cave, one outside) are never merged.");
        AutoDisableInBossFights = cfg.Bind(section, "AutoDisableInBossFights", true,
            "Temporarily switches back to the normal shared screen whenever the game itself takes over the camera: boss arenas, boss intros, cutscenes, NPC and script focus, death and level-up messages, resting.\n" +
            "Only happens while the players are close (see AutoDisableDistanceMode); players who are far apart keep their own halves, because one shared camera can't show both.");
        AutoDisableDistanceMode = cfg.Bind(section, "AutoDisableDistanceMode", ModeScreenWidth,
            new ConfigDescription(
                "What counts as \"close\" for both auto-disable options.\n" +
                "Screen Width: both players fit on a single screen (this is the largest distance that works).\n" +
                "Custom Distance: the players are within AutoDisableCustomDistance meters of each other, and also fit on a single screen.",
                new AcceptableValueList<string>(DistanceModes)));
        AutoDisableCustomDistance = cfg.Bind(section, "AutoDisableCustomDistance", 10,
            new ConfigDescription(
                "Used when AutoDisableDistanceMode is Custom Distance. Straight-line distance between the two characters, in meters.\n" +
                "A meter here is the height of a player divided by 1.8, so a player is about 1.8 m tall and a door about 2 m.\n" +
                "Values larger than what fits on one screen have no extra effect. The log prints the exact size of one screen in meters ([Splitscreen] Auto-disable scale).",
                new AcceptableValueRange<int>(1, 50)));

        // Developer tooling. Deliberately NOT registered with Mod Options (see Plugin.TryRegisterModOptions); it only exists in the BepInEx config file.
        Diagnostics = cfg.Bind("Debug", "Diagnostics", false,
            "Developer tooling for splitscreen rendering. When enabled: F11 cycles diagnostic views (per-stage render captures, one player's pass on both halves, etc.)\n" +
            "and the per-pass [BG]/[GLOBAL]/[DIAG] lines are written to the log. Leave disabled for normal play.");
    }
}