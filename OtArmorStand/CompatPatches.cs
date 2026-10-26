using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OtArmorStand
{
    // Simple Sort (aedenthorn.SimpleSort) re-applies the last container sort every time a
    // container panel opens, and the stand's panel is a container panel to it (the stub
    // InventoryGui.Show opens over the session's fake inventory). Its sort packs items into
    // cells in sort order by writing m_gridPos directly, without going through any of the
    // guarded Inventory calls. Normalize then has to move everything back — but when two
    // items end up in each other's cells neither has an empty cell to step aside to, and the
    // first gets dumped into the player's inventory: "opening the stand pulls an item off it".
    // Refuse sorts against the stand's inventory (the sticky open-sort and the sort hotkeys
    // both go through SortByType). Matches nothing when Simple Sort isn't installed.
    [HarmonyPatch]
    internal static class SimpleSortGuard
    {
        private static readonly Type Sorter = AccessTools.TypeByName("SimpleSort.BepInExPlugin");

        [HarmonyPatch]
        private static bool Prepare() => Sorter != null && AccessTools.Method(Sorter, "SortByType") != null;

        [HarmonyPatch]
        private static MethodInfo TargetMethod() => AccessTools.Method(Sorter, "SortByType");

        private static bool Prefix(Inventory inventory)
        {
            var session = StandSession.Current;
            return session == null || inventory != session.Inventory;
        }
    }

    // The stock Normalize only ever steps items into an empty cell. A sorter packs the panel
    // by swapping items into each other's cells (or longer cycles), and those got ejected to
    // the player. This replaces it: empty cells first, then misplaced pairs swap directly,
    // and only items that genuinely have no cell left are dropped. Same last-resort behavior
    // as before for a truly homeless item.
    [HarmonyPatch(typeof(StandSession), "Normalize")]
    internal static class NormalizePatch
    {
        private static bool Prefix(StandSession __instance)
        {
            var items = new List<ItemDrop.ItemData>(__instance.Inventory.GetAllItems());

            foreach (var item in items)
            {
                if (__instance.Fits(item.m_gridPos, item))
                {
                    continue;
                }
                var cell = __instance.FindCell(item);
                if (cell.x >= 0)
                {
                    item.m_gridPos = cell;
                }
            }

            bool moved = true;
            while (moved)
            {
                moved = false;
                foreach (var item in items)
                {
                    if (item == null || __instance.Fits(item.m_gridPos, item))
                    {
                        continue;
                    }
                    foreach (var other in items)
                    {
                        if (other == item || other == null || __instance.Fits(other.m_gridPos, other) ||
                            !__instance.Fits(item.m_gridPos, other) || !__instance.Fits(other.m_gridPos, item))
                        {
                            continue;
                        }
                        var tmp = item.m_gridPos;
                        item.m_gridPos = other.m_gridPos;
                        other.m_gridPos = tmp;
                        moved = true;
                        break;
                    }
                }
            }

            foreach (var item in items)
            {
                if (item == null || __instance.Fits(item.m_gridPos, item))
                {
                    continue;
                }
                var cell = __instance.FindCell(item);
                if (cell.x >= 0)
                {
                    item.m_gridPos = cell;
                    continue;
                }
                __instance.Inventory.RemoveItem(item);
                Player player = Player.m_localPlayer;
                if (player == null || !player.GetInventory().AddItem(item))
                {
                    Vector3 at = player != null ? player.transform.position : __instance.Stand.transform.position;
                    ItemDrop.DropItem(item, 0, at + Vector3.up, Quaternion.identity);
                }
            }
            return false;
        }
    }
}
