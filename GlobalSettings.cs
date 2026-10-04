using BepInEx.Configuration;

namespace SaS2SplitScreen;

public static class GlobalSettings
{
    public static ConfigEntry<bool> SplitscreenEnabled;
    public static ConfigEntry<bool> IndependentDoors;

    public static void Bind(ConfigFile cfg)
    {
        SplitscreenEnabled = cfg.Bind("Splitscreen", "Splitscreen", false, "Splits the screen vertically in local co-op.\n" + "Left half shows Player 1's view; right half shows Player 2's view.");
        IndependentDoors = cfg.Bind("Splitscreen", "Allow P1 and P2 to travel through doors independently", false, "When enabled, walking through a layer-change door moves only the triggering player.\n" + "The other player stays where they are.");
    }
}