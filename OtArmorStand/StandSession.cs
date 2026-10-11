using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OtArmorStand
{
    internal enum StandAction
    {
        Open,
        Swap,
    }

    // A stand's slots mirrored into a temporary Inventory, so the vanilla container panel can show
    // and edit them. The stand's ZDO stays the only real storage: every frame, cells whose item
    // changed are written back to the stand with vanilla's own keys (<slot>_item, <slot>_variant,
    // <slot>_itemData) and visual RPC, exactly as vanilla's hotbar attach does. That only works
    // while this client owns the stand's ZDO, so a session is only created after ownership is
    // confirmed, and ownership isn't handed away while the panel is open (see RequestOwnPatch).
    internal class StandSession
    {
        private const int Width = 3;
        private const float OwnershipTimeout = 2f;

        // Cells (index = y * Width + x) laid out like a body on a 3x3 grid, with unused cells
        // hidden by GuiPatches:
        //   Cape    Helmet  Weapon
        //   Shield  Chest   Belt
        //   Shield  Legs    Weapon
        // Hand/back slots go to the shield column if they can hold a shield, else the weapon one,
        // spilling into the other column and then extra rows (another mod's stand, say).
        private static readonly Dictionary<VisSlot, int> ArmorCells = new Dictionary<VisSlot, int>
        {
            { VisSlot.Shoulder, 0 }, { VisSlot.Helmet, 1 }, { VisSlot.Chest, 4 }, { VisSlot.Utility, 5 }, { VisSlot.Legs, 7 },
        };
        private static readonly int[] WeaponCells = { 2, 8 };
        private static readonly int[] ShieldCells = { 3, 6 };

        private static readonly VisSlot[] ArmorSlots =
        {
            VisSlot.Helmet, VisSlot.Chest, VisSlot.Legs, VisSlot.Shoulder, VisSlot.Utility,
        };

        private static readonly AccessTools.FieldRef<ArmorStand, ZNetView> StandView =
            AccessTools.FieldRefAccess<ArmorStand, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Container, ZNetView> ContainerView =
            AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Container, Inventory> ContainerInventory =
            AccessTools.FieldRefAccess<Container, Inventory>("m_inventory");
        private static readonly MethodInfo UpdateSupports = AccessTools.Method(typeof(ArmorStand), "UpdateSupports");

        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HelmetItem =
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_helmetItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> ChestItem =
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_chestItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> LegItem =
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_legItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> ShoulderItem =
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_shoulderItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> UtilityItem =
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_utilityItem");

        // The session whose panel is showing, if any.
        internal static StandSession Current;

        private static ArmorStand _pendingStand;
        private static StandAction _pendingAction;
        private static float _pendingDeadline;

        internal readonly ArmorStand Stand;
        internal readonly ZNetView View;
        internal readonly Inventory Inventory;
        internal readonly Container Stub;

        private readonly Vector2i[] _cellOfSlot;
        private readonly int[] _slotAtCell;
        private readonly ItemDrop.ItemData[] _written;
        // Slots holding an item whose prefab isn't known here (from a mod this client lacks). They're
        // left alone rather than mirrored, so writing back can't clear them.
        private readonly bool[] _locked;
        // Slots with a cell: the first slot for each VisSlot.
        private readonly bool[] _mirrored;

        private StandSession(ArmorStand stand)
        {
            Stand = stand;
            View = StandView(stand);

            // The buildable stand lists each of its seven slots twice (same switch, same VisSlot).
            // Vanilla only ever attaches to the first of a pair, so only that one gets a cell.
            int slotCount = stand.m_slots.Count;
            _cellOfSlot = new Vector2i[slotCount];
            _mirrored = new bool[slotCount];
            var primaries = new List<int>();
            for (int i = 0; i < slotCount; i++)
            {
                if (PrimaryOf(i) == i)
                {
                    primaries.Add(i);
                }
            }
            var cellOf = new Dictionary<int, int>();
            var taken = new HashSet<int>();
            foreach (int slot in primaries)
            {
                if (ArmorCells.TryGetValue(stand.m_slots[slot].m_slot, out int cell) && taken.Add(cell))
                {
                    cellOf[slot] = cell;
                }
            }
            foreach (int slot in primaries)
            {
                if (cellOf.ContainsKey(slot))
                {
                    continue;
                }
                bool shield = stand.m_slots[slot].m_supportedTypes.Contains(ItemDrop.ItemData.ItemType.Shield);
                int cell = FirstFree(shield ? ShieldCells : WeaponCells, taken);
                if (cell < 0)
                {
                    cell = FirstFree(shield ? WeaponCells : ShieldCells, taken);
                }
                if (cell < 0)
                {
                    cell = Width * 3;
                    while (taken.Contains(cell))
                    {
                        cell++;
                    }
                }
                taken.Add(cell);
                cellOf[slot] = cell;
            }
            int height = 3;
            foreach (int cell in taken)
            {
                height = Math.Max(height, cell / Width + 1);
            }
            _slotAtCell = new int[Width * height];
            for (int i = 0; i < _slotAtCell.Length; i++)
            {
                _slotAtCell[i] = -1;
            }
            foreach (var pair in cellOf)
            {
                Place(pair.Key, pair.Value);
            }

            // m_bkg is only decoration, and InventoryGui never reads it for containers.
            Inventory = new Inventory(stand.m_name, null, Width, height);
            _written = new ItemDrop.ItemData[slotCount];
            _locked = new bool[slotCount];
            ZDO zdo = View.GetZDO();
            foreach (int slot in primaries)
            {
                ItemDrop.ItemData item = LoadItem(zdo, slot);
                if (item != null)
                {
                    Inventory.AddItem(item, _cellOfSlot[slot]);
                    _written[slot] = item;
                }
            }
            RecoverDuplicates(zdo);

            // InventoryGui.Show wants a Container. This one stays inactive so its Awake never runs:
            // no RPCs registered on the stand's ZNetView, no ZDO save of its own, no drop-on-destroy,
            // and it's invisible to FindObjectsOfType, so other mods scanning for chests (quick
            // stacking, crafting from containers) don't see it. Parenting it to the stand closes the
            // panel if the stand is destroyed, and keeps the panel's distance check working.
            var go = new GameObject("OtArmorStand_Panel");
            go.SetActive(false);
            go.transform.SetParent(stand.transform, false);
            Stub = go.AddComponent<Container>();
            Stub.m_name = stand.m_name;
            ContainerView(Stub) = View;
            ContainerInventory(Stub) = Inventory;
        }

        private void Place(int slot, int cell)
        {
            _slotAtCell[cell] = slot;
            _cellOfSlot[slot] = new Vector2i(cell % Width, cell / Width);
            _mirrored[slot] = true;
        }

        private static int FirstFree(int[] cells, HashSet<int> taken)
        {
            foreach (int cell in cells)
            {
                if (!taken.Contains(cell))
                {
                    return cell;
                }
            }
            return -1;
        }

        // The stand slot shown in a cell, or null for a hidden one.
        internal ArmorStand.ArmorStandSlot SlotAt(Vector2i cell)
        {
            int index = cell.y * Width + cell.x;
            if (cell.x < 0 || cell.y < 0 || cell.x >= Width || index >= _slotAtCell.Length || _slotAtCell[index] < 0)
            {
                return null;
            }
            return Stand.m_slots[_slotAtCell[index]];
        }

        private int PrimaryOf(int slot)
        {
            VisSlot visSlot = Stand.m_slots[slot].m_slot;
            return Stand.m_slots.FindIndex(s => s.m_slot == visSlot);
        }

        // The item saved in a slot, or null if it's empty. An item whose prefab this client doesn't
        // have (another mod's) locks the slot instead, so writing back can't clear it.
        private ItemDrop.ItemData LoadItem(ZDO zdo, int slot)
        {
            int hash = zdo.GetInt(ItemKey(slot));
            if (hash == 0)
            {
                return null;
            }
            GameObject prefab = ObjectDB.instance.GetItemPrefab(hash);
            if (prefab == null)
            {
                _locked[slot] = true;
                return null;
            }
            ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_stack = 1;
            ItemDrop.LoadFromZDO(item, zdo, slot);
            return item;
        }

        // 1.0.0 gave the duplicate slots their own cells, so a stand can hold a second piece there
        // that it never shows (both of a pair draw to the same VisEquipment slot). Move it into its
        // primary slot if that's free, otherwise drop it the vanilla way. Either way the duplicate's
        // visual clear also blanks the primary's shared VisEquipment slot, so redraw the primaries.
        private void RecoverDuplicates(ZDO zdo)
        {
            bool recovered = false;
            for (int slot = 0; slot < _mirrored.Length; slot++)
            {
                if (_mirrored[slot] || zdo.GetInt(ItemKey(slot)) == 0)
                {
                    continue;
                }
                int primary = PrimaryOf(slot);
                Vector2i cell = _cellOfSlot[primary];
                recovered = true;
                ItemDrop.ItemData item = _locked[primary] || Inventory.GetItemAt(cell.x, cell.y) != null ? null : LoadItem(zdo, slot);
                if (item != null)
                {
                    Inventory.AddItem(item, cell);
                    zdo.Set(ItemKey(slot), 0);
                    View.InvokeRPC(ZNetView.Everybody, "RPC_SetVisualItem", slot, 0, 0);
                }
                else
                {
                    View.InvokeRPC("RPC_DropItem", slot);
                }
            }
            if (!recovered)
            {
                return;
            }
            Plugin.Log.LogInfo($"Recovered items from duplicate slots of {Stand.name}.");
            Sync();
            for (int slot = 0; slot < _written.Length; slot++)
            {
                ItemDrop.ItemData item = _written[slot];
                if (_mirrored[slot] && item != null)
                {
                    int hash = item.m_dropPrefab.name.GetStableHashCode();
                    View.InvokeRPC(ZNetView.Everybody, "RPC_SetVisualItem", slot, 0, 0);
                    View.InvokeRPC(ZNetView.Everybody, "RPC_SetVisualItem", slot, hash, item.m_variant);
                }
            }
        }

        // Puts back any item another mod moved to the wrong cell (chest sorters sort this panel
        // too). One with no free cell of its own goes to the player's inventory, or the ground.
        private void Normalize()
        {
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(Inventory.GetAllItems()))
            {
                if (Fits(item.m_gridPos, item))
                {
                    continue;
                }
                Vector2i cell = FindCell(item);
                if (cell.x >= 0)
                {
                    item.m_gridPos = cell;
                    continue;
                }
                Inventory.RemoveItem(item);
                Player player = Player.m_localPlayer;
                if (player == null || !player.GetInventory().AddItem(item))
                {
                    Vector3 at = player != null ? player.transform.position : Stand.transform.position;
                    ItemDrop.DropItem(item, 0, at + Vector3.up, Quaternion.identity);
                }
            }
        }

        private static int ItemKey(int slot) => (slot + "_item").GetStableHashCode();

        private static int VariantKey(int slot) => (slot + "_variant").GetStableHashCode();

        // Asks for ownership of the stand (vanilla's RPC_RequestOwn, which every client has) and
        // runs the action once it arrives; UpdatePending polls for it.
        internal static void Request(ArmorStand stand, StandAction action)
        {
            ZNetView view = StandView(stand);
            if (view == null || !view.IsValid())
            {
                return;
            }
            if (!view.IsOwner())
            {
                view.InvokeRPC("RPC_RequestOwn");
            }
            _pendingStand = stand;
            _pendingAction = action;
            _pendingDeadline = Time.time + OwnershipTimeout;
            UpdatePending();
        }

        internal static void UpdatePending()
        {
            if (_pendingStand == null)
            {
                return;
            }
            ZNetView view = StandView(_pendingStand);
            if (view == null || !view.IsValid() || Player.m_localPlayer == null)
            {
                _pendingStand = null;
                return;
            }
            if (view.IsOwner())
            {
                ArmorStand stand = _pendingStand;
                _pendingStand = null;
                Run(stand, _pendingAction);
            }
            else if (Time.time > _pendingDeadline)
            {
                // The owner refused (it has the panel open) or didn't answer.
                _pendingStand = null;
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$msg_inuse");
            }
        }

        private static void Run(ArmorStand stand, StandAction action)
        {
            Player player = Player.m_localPlayer;
            if (Current != null && Current.Stand == stand)
            {
                if (action == StandAction.Swap)
                {
                    Current.Swap(player);
                    Current.Sync();
                }
                return;
            }

            var session = new StandSession(stand);
            if (action == StandAction.Open)
            {
                Close();
                Current = session;
                InventoryGui.instance.Show(session.Stub);
            }
            else
            {
                session.Swap(player);
                session.Sync();
                session.Dispose();
            }
        }

        // Called every frame from InventoryGui.Update: writes changes back, and ends the session
        // once the panel shows something else or the stand is gone.
        internal static void Tick(Container shown)
        {
            if (Current == null)
            {
                return;
            }
            if (Current.Stand == null || !Current.View.IsValid())
            {
                Current = null;
                return;
            }
            Current.Normalize();
            Current.Sync();
            if (shown != Current.Stub)
            {
                Current.Dispose();
                Current = null;
            }
            else if (!Current.View.IsOwner())
            {
                // Shouldn't happen while RequestOwnPatch holds the stand, but if ownership moved
                // anyway, edits here could no longer be saved.
                InventoryGui.instance.Hide();
            }
        }

        internal static void Close()
        {
            if (Current == null)
            {
                return;
            }
            if (Current.Stand != null && Current.View.IsValid())
            {
                Current.Sync();
            }
            Current.Dispose();
            Current = null;
        }

        private void Dispose()
        {
            if (Stub != null)
            {
                Object.Destroy(Stub.gameObject);
            }
        }

        // Whether the item may sit in the given cell: the vanilla attach rules for that cell's slot.
        internal bool Fits(Vector2i cell, ItemDrop.ItemData item)
        {
            if (cell.x < 0 || cell.y < 0 || cell.x >= Width || cell.y * Width + cell.x >= _slotAtCell.Length)
            {
                return false;
            }
            int slot = _slotAtCell[cell.y * Width + cell.x];
            return slot >= 0 && !_locked[slot] && CanAttach(Stand.m_slots[slot], item);
        }

        // The first empty cell the item fits, or (-1, -1).
        internal Vector2i FindCell(ItemDrop.ItemData item)
        {
            for (int slot = 0; slot < _cellOfSlot.Length; slot++)
            {
                if (!_mirrored[slot])
                {
                    continue;
                }
                Vector2i cell = _cellOfSlot[slot];
                if (Inventory.GetItemAt(cell.x, cell.y) == null && Fits(cell, item))
                {
                    return cell;
                }
            }
            return new Vector2i(-1, -1);
        }

        // Vanilla's ArmorStand.CanAttach (widened by Stands.SlotTakes) plus the check in
        // ArmorStand.UseItem that anything other than chest and legs has an "attach" model to show.
        private static bool CanAttach(ArmorStand.ArmorStandSlot slot, ItemDrop.ItemData item)
        {
            if (item.m_dropPrefab == null)
            {
                return false;
            }
            var shared = item.m_shared;
            if (!Stands.SlotTakes(slot, item))
            {
                return false;
            }
            if (shared.m_itemType == ItemDrop.ItemData.ItemType.Legs || shared.m_itemType == ItemDrop.ItemData.ItemType.Chest)
            {
                return true;
            }
            Transform root = item.m_dropPrefab.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                string name = root.GetChild(i).gameObject.name;
                if (name == "attach" || name == "attach_skin")
                {
                    return true;
                }
            }
            return false;
        }

        // Writes every slot whose item changed since the last write back to the stand.
        internal void Sync()
        {
            ZDO zdo = View.GetZDO();
            bool cleared = false, attached = false;
            for (int slot = 0; slot < _written.Length; slot++)
            {
                if (_locked[slot] || !_mirrored[slot])
                {
                    continue;
                }
                Vector2i cell = _cellOfSlot[slot];
                ItemDrop.ItemData item = Inventory.GetItemAt(cell.x, cell.y);
                if (item == _written[slot])
                {
                    continue;
                }
                if (!View.IsOwner())
                {
                    Plugin.Log.LogWarning($"Lost ownership of {Stand.name} before its changes were saved.");
                    return;
                }
                _written[slot] = item;
                if (item == null)
                {
                    zdo.Set(ItemKey(slot), 0);
                    View.InvokeRPC(ZNetView.Everybody, "RPC_SetVisualItem", slot, 0, 0);
                    cleared = true;
                }
                else
                {
                    int hash = item.m_dropPrefab.name.GetStableHashCode();
                    zdo.Set(ItemKey(slot), hash);
                    zdo.Set(VariantKey(slot), item.m_variant);
                    ItemDrop.SaveToZDO(item, zdo, slot);
                    View.InvokeRPC(ZNetView.Everybody, "RPC_SetVisualItem", slot, hash, item.m_variant);
                    Game.instance.IncrementPlayerStat(PlayerStatType.ArmorStandUses);
                    attached = true;
                }
            }
            if (cleared)
            {
                // SetVisualItem skips this when clearing a slot; vanilla's DropItem calls it itself.
                UpdateSupports.Invoke(Stand, null);
            }
            if (attached)
            {
                Stand.m_effects.Create(Stand.transform.position, Quaternion.identity);
            }
        }

        // Trades each armor piece on the stand for the one the player is wearing in that slot.
        // Empty stand slots are skipped, leaving the player's piece on. The worn piece goes onto the
        // stand and the stand's piece takes its place in the inventory, so no free slot is needed
        // unless the player wears nothing there (or wears something the stand can't hold).
        internal void Swap(Player player)
        {
            Inventory inventory = player.GetInventory();
            int swapped = 0, noRoom = 0;
            foreach (VisSlot visSlot in ArmorSlots)
            {
                int slot = Stand.m_slots.FindIndex(s => s.m_slot == visSlot);
                if (slot < 0 || _locked[slot] || !_mirrored[slot])
                {
                    continue;
                }
                Vector2i cell = _cellOfSlot[slot];
                ItemDrop.ItemData standItem = Inventory.GetItemAt(cell.x, cell.y);
                if (standItem == null)
                {
                    continue;
                }

                ItemDrop.ItemData worn = Worn(player, visSlot);
                if (worn != null && CanAttach(Stand.m_slots[slot], worn))
                {
                    Vector2i wornPos = worn.m_gridPos;
                    player.UnequipItem(worn, triggerEquipEffects: false);
                    inventory.RemoveItem(worn);
                    Inventory.RemoveItem(standItem);
                    Inventory.AddItem(worn, cell);
                    inventory.AddItem(standItem, wornPos);
                }
                else
                {
                    if (!inventory.HaveEmptySlot())
                    {
                        noRoom++;
                        continue;
                    }
                    Inventory.RemoveItem(standItem);
                    inventory.AddItem(standItem);
                }
                player.EquipItem(standItem);
                swapped++;
            }

            if (noRoom > 0)
            {
                player.Message(MessageHud.MessageType.Center, "$inventory_full");
            }
            else if (swapped == 0)
            {
                player.Message(MessageHud.MessageType.Center, "No armor on the stand to swap");
            }
        }

        private static ItemDrop.ItemData Worn(Player player, VisSlot slot)
        {
            switch (slot)
            {
                case VisSlot.Helmet: return HelmetItem(player);
                case VisSlot.Chest: return ChestItem(player);
                case VisSlot.Legs: return LegItem(player);
                case VisSlot.Shoulder: return ShoulderItem(player);
                case VisSlot.Utility: return UtilityItem(player);
                default: return null;
            }
        }
    }
}
