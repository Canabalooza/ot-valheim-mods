using System;
using System.Reflection;
using HarmonyLib;

namespace OtArmorStand
{
    // Vanilla TombStone.Interact never opens the panel when the whole grave fits: it calls
    // Container.TakeAll straight away (EasyFitInInventory). Simple Sort hangs its sticky
    // sort off InventoryGui.Show, so that auto-loot path sorts nothing — items land in
    // death order — while the "inventory already partly full" path opens the chest panel,
    // sorts it, and loots sorted. Two different outcomes from the same E press. Sort the
    // grave's inventory right before TakeAll using Simple Sort's own container settings so
    // both paths match. No-ops when Simple Sort is not installed, when its container
    // sticky sort is off, or on any error (looting must never depend on this patch).
    [HarmonyPatch]
    internal static class GraveAutoLootSortPatch
    {
        private static readonly Type Sorter = AccessTools.TypeByName("SimpleSort.BepInExPlugin");

        private static MethodInfo _sortByType;
        private static FieldInfo _sortTypeField, _ascField, _stickyField;
        private static bool _resolved;

        [HarmonyPatch]
        private static bool Prepare() => Sorter != null;

        [HarmonyPatch]
        private static MethodInfo TargetMethod() => AccessTools.Method(typeof(Container), "TakeAll");

        private static object EntryValue(FieldInfo field)
        {
            var entry = field?.GetValue(null);
            return entry?.GetType().GetProperty("Value")?.GetValue(entry);
        }

        private static void Prefix(Container __instance)
        {
            try
            {
                if (__instance.GetComponent<TombStone>() == null)
                {
                    return;
                }
                if (!_resolved)
                {
                    _resolved = true;
                    _sortByType = AccessTools.Method(Sorter, "SortByType");
                    _sortTypeField = AccessTools.Field(Sorter, "containerSortType");
                    _ascField = AccessTools.Field(Sorter, "containerSortAsc");
                    _stickyField = AccessTools.Field(Sorter, "containerStickySort");
                }
                if (_sortByType == null || _sortTypeField == null || _ascField == null)
                {
                    return;
                }
                if (EntryValue(_stickyField) is bool sticky && !sticky)
                {
                    return;
                }
                var inventory = __instance.GetInventory();
                if (inventory == null || inventory.NrOfItems() == 0)
                {
                    return;
                }
                _sortByType.Invoke(null, new object[]
                {
                    EntryValue(_sortTypeField), inventory, EntryValue(_ascField), false
                });
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("grave auto-loot sort skipped: " + e.Message);
            }
        }
    }
}
