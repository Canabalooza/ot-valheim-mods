using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace OtArmorStand
{
    // Vanilla only fills an armor stand from the hotbar (aim at it, press the item's number) and
    // empties it with Use, which drops every armor piece on the ground at once because the helmet,
    // chest, legs, cape and belt slots share one hover zone. This opens the stand as a small chest
    // instead: one cell per slot, filled by dragging from the inventory and emptied with Take all.
    // It also adds a swap that trades the stand's armor for what the player is wearing. Everything
    // is client-side: the stand keeps its items in the same ZDO keys vanilla uses, so players
    // without the mod see the same stand and can still use it the vanilla way.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "tlisonbee.valheim.otarmorstand";
        public const string PluginName = "OtArmorStand";
        public const string PluginVersion = "1.0.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> AltUseSwaps;
        internal static ConfigEntry<bool> ShowSwapButton;
        internal static ConfigEntry<float> PlaceholderOpacity;
        internal static ConfigEntry<float> PlaceholderBrightness;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            AltUseSwaps = Config.Bind(
                "General",
                "AltUseSwaps",
                true,
                "Holding the alternate-place key (Left Shift by default) while using an armor stand " +
                "swaps its armor with what you're wearing, instead of opening it.");
            ShowSwapButton = Config.Bind(
                "General",
                "ShowSwapButton",
                true,
                "Show a Swap button on an open armor stand's panel, in place of Place stacks (which " +
                "does nothing on a stand).");
            PlaceholderOpacity = Config.Bind(
                "Placeholders",
                "Opacity",
                0.5f,
                "Opacity (0-1) of the black-and-white icon an empty slot shows of what goes there. " +
                "0 hides them.");
            PlaceholderBrightness = Config.Bind(
                "Placeholders",
                "Brightness",
                0.75f,
                "Brightness (0-1) of the placeholder icons: 1 keeps the icon's own grey levels, lower " +
                "values darken it toward black.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        private void Update()
        {
            StandSession.UpdatePending();
        }

        private void OnDestroy()
        {
            StandSession.Close();
            _harmony?.UnpatchSelf();
        }
    }
}
