using HarmonyLib;
using UnityEngine;

namespace OtArmorStand
{
    internal static class Stands
    {
        // The stand a hover zone belongs to, or null for any other Switch (including the stand's
        // own change-pose switch, which isn't one of its slots and keeps vanilla behavior).
        internal static ArmorStand Of(Switch sw)
        {
            ArmorStand stand = sw.GetComponentInParent<ArmorStand>();
            if (stand == null)
            {
                return null;
            }
            foreach (var slot in stand.m_slots)
            {
                if (slot.m_switch == sw)
                {
                    return stand;
                }
            }
            return null;
        }

        // Vanilla's slot-type test, ArmorStand.CanAttach, judges an item by its m_attachOverride
        // when one is set. The 1.0 Hood of the Vanguard (HelmetDNMediumHood) is a Helmet whose
        // override is Material, so no head slot takes it even though it has an attach_skin model
        // like every other hood. Accept an item whose own type the slot supports as well.
        internal static bool SlotTakes(ArmorStand.ArmorStandSlot slot, ItemDrop.ItemData item)
        {
            var shared = item.m_shared;
            return slot.m_supportedTypes.Count == 0 ||
                slot.m_supportedTypes.Contains(shared.m_itemType) ||
                (shared.m_attachOverride != ItemDrop.ItemData.ItemType.None && slot.m_supportedTypes.Contains(shared.m_attachOverride));
        }

        internal static void CantAttach()
        {
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "$piece_armorstand_cantattach");
        }
    }

    // Hotbar attach (ArmorStand.UseItem) goes through CanAttach, so widen it the same way the
    // panel does; see Stands.SlotTakes.
    [HarmonyPatch(typeof(ArmorStand), "CanAttach")]
    internal static class CanAttachPatch
    {
        private static void Postfix(ArmorStand.ArmorStandSlot slot, ItemDrop.ItemData item, ref bool __result)
        {
            if (!__result && item != null)
            {
                __result = Stands.SlotTakes(slot, item);
            }
        }
    }

    // Use on a stand's hover zone opens it (or, with the alt key, swaps armor) instead of dropping
    // what's on it. Using an item from the hotbar on the stand goes through Switch.UseItem and
    // stays vanilla.
    [HarmonyPatch(typeof(Switch), nameof(Switch.Interact))]
    internal static class InteractPatch
    {
        private static bool Prefix(Switch __instance, Humanoid character, bool hold, bool alt, ref bool __result)
        {
            ArmorStand stand = Stands.Of(__instance);
            if (stand == null || character != Player.m_localPlayer)
            {
                return true;
            }
            __result = false;
            if (hold)
            {
                return false;
            }
            __result = true;
            if (!PrivateArea.CheckAccess(stand.transform.position))
            {
                return false;
            }
            StandSession.Request(stand, alt && Plugin.AltUseSwaps.Value ? StandAction.Swap : StandAction.Open);
            return false;
        }
    }

    [HarmonyPatch(typeof(Switch), nameof(Switch.GetHoverText))]
    internal static class HoverTextPatch
    {
        private static bool Prefix(Switch __instance, ref string __result)
        {
            ArmorStand stand = Stands.Of(__instance);
            if (stand == null)
            {
                return true;
            }
            if (!PrivateArea.CheckAccess(stand.transform.position, 0f, flash: false))
            {
                __result = Localization.instance.Localize(stand.m_name + "\n$piece_noaccess");
                return false;
            }
            string text = __instance.m_hoverText +
                "\n[<color=yellow><b>$KEY_HotbarUse</b></color>] $piece_itemstand_attach" +
                "\n[<color=yellow><b>$KEY_Use</b></color>] $piece_container_open";
            if (Plugin.AltUseSwaps.Value)
            {
                text += "\n[<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] Swap armor";
            }
            __result = Localization.instance.Localize(text);
            return false;
        }
    }

    // Vanilla's RPC_RequestOwn hands the stand to whoever asks. While this client has it open,
    // refuse, so nobody else (a vanilla player's hotbar attach included) can change it underneath
    // the panel; the asker's request just times out. Nothing is stored, so a crash can't leave the
    // stand locked.
    [HarmonyPatch(typeof(ArmorStand), "RPC_RequestOwn")]
    internal static class RequestOwnPatch
    {
        private static bool Prefix(ArmorStand __instance, long sender)
        {
            var session = StandSession.Current;
            return session == null || session.Stand != __instance || sender == ZDOMan.GetSessionID();
        }
    }

    // Dragging onto or out of the stand's panel. Vanilla's InventoryGrid.DropItem either moves the
    // item to the target cell or, if that's taken, swaps the two; reject anything that would leave
    // an item in a stand cell whose slot can't hold it. A drop on the wrong cell is redirected to
    // the right empty one, so dragging a helmet anywhere onto the stand works. Stand cells take one
    // item at a time, like vanilla's attach.
    [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem))]
    internal static class DropItemPatch
    {
        private static bool Prefix(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, ref int amount, ref Vector2i pos, ref bool __result)
        {
            var session = StandSession.Current;
            if (session == null)
            {
                return true;
            }
            Inventory target = __instance.GetInventory();
            Inventory stand = session.Inventory;
            if (target != stand && fromInventory != stand)
            {
                return true;
            }

            ItemDrop.ItemData other = target.GetItemAt(pos.x, pos.y);
            if (other == item)
            {
                return true;
            }
            if (target == stand)
            {
                if (!session.Fits(pos, item))
                {
                    if (fromInventory == stand)
                    {
                        return Reject(ref __result);
                    }
                    Vector2i cell = session.FindCell(item);
                    if (cell.x < 0)
                    {
                        return Reject(ref __result);
                    }
                    pos = cell;
                    other = null;
                }
                if (fromInventory != stand)
                {
                    amount = 1;
                }
            }
            // A swap sends the other item back to where the dragged one came from.
            if (other != null && fromInventory == stand && !session.Fits(item.m_gridPos, other))
            {
                return Reject(ref __result);
            }
            return true;
        }

        private static bool Reject(ref bool result)
        {
            Stands.CantAttach();
            result = false;
            return false;
        }
    }

    // Shift-click from the inventory: vanilla adds the item to the first free cell, which would be
    // the helmet cell whatever the item is. Put it in its own slot's cell instead.
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveItemToThis), typeof(Inventory), typeof(ItemDrop.ItemData))]
    internal static class MoveItemToThisPatch
    {
        private static bool Prefix(Inventory __instance, Inventory fromInventory, ItemDrop.ItemData item)
        {
            var session = StandSession.Current;
            if (session == null || __instance != session.Inventory)
            {
                return true;
            }
            Vector2i cell = session.FindCell(item);
            if (cell.x < 0)
            {
                Stands.CantAttach();
                return false;
            }
            __instance.MoveItemToThis(fromInventory, item, 1, cell.x, cell.y);
            return false;
        }
    }

    // Place stacks and bulk moves would fill the stand's cells in order, ignoring slot types.
    // The Place stacks button is hidden on a stand, but hold-Use still triggers it, and other mods
    // may call these on whatever container is open.
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.StackAll))]
    internal static class StackAllPatch
    {
        private static bool Prefix(Inventory __instance, ref int __result)
        {
            var session = StandSession.Current;
            if (session == null || __instance != session.Inventory)
            {
                return true;
            }
            __result = 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveAll))]
    internal static class MoveAllPatch
    {
        private static bool Prefix(Inventory __instance)
        {
            var session = StandSession.Current;
            return session == null || __instance != session.Inventory;
        }
    }
}
