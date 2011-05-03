using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Logic.Inventory;
using Styx.Plugins.PluginClass;
using Styx.WoWInternals;

namespace EquipMe
{
    public partial class EquipMe : HBPlugin
    {

        #region local variables

        /// <summary>
        /// Check to stop Initialize firing twice
        /// </summary>
        private bool _hasInit = false;

        /// <summary>
        /// Instance of the settings form
        /// </summary>
        private EquipMeGui _settingsForm;

        /// <summary>
        /// A list of lua events to hook
        /// </summary>
        private List<string> _luaEvents = new List<string>()
        {
            "ACTIVE_TALENT_GROUP_CHANGED",
            "CHARACTER_POINTS_CHANGED",

            "PLAYER_ENTERING_WORLD",

            "START_LOOT_ROLL",
            "CONFIRM_DISENCHANT_ROLL",
            "CONFIRM_LOOT_ROLL",

            "ITEM_PUSH",

            "USE_BIND_CONFIRM",
            "LOOT_BIND_CONFIRM",
            "EQUIP_BIND_CONFIRM",
            "AUTOEQUIP_BIND_CONFIRM",
        };

        #endregion

        #region plugin overrides

        public override string Author
        {
            get
            {
                return "eXemplar";
            }
        }

        public override string Name
        {
            get
            {
                return "EquipMe";
            }
        }

        public override Version Version
        {
            get
            {
                return new Version(2, 0);
            }
        }

        public override bool WantButton
        {
            get
            {
                return true;
            }
        }

        public override void OnButtonPress()
        {
            if (_settingsForm == null || _settingsForm.IsDisposed)
            {
                _settingsForm = new EquipMeGui();
            }
            _settingsForm.ShowDialog();
        }

        public override void Initialize()
        {
            if (!_hasInit)
            {
                _hasInit = true;
                _luaEvents.ForEach(evt => Lua.Events.AttachEvent(evt, HandleLuaEvent));
                EquipMeSettings.Instance.LoadSettings();
            }
        }

        #endregion

        #region HandleLuaEvent

        /// <summary>
        /// Handles all the lua events as registered in RegisterLuaEvents()
        /// </summary>
        /// <param name="sender">sender</param>
        /// <param name="args">lua args</param>
        private void HandleLuaEvent(object sender, LuaEventArgs args)
        {
            try
            {
                if (args.EventName == "START_LOOT_ROLL") // fired when a roll starts
                {
                    // don't roll on loot if the setting is off
                    if (!EquipMeSettings.Instance.RollOnLoot)
                    {
                        return;
                    }
                    // get the rollid from the event args
                    var id = ToInteger(args.Args.ElementAtOrDefault(0).ToString());
                    // do a barrel roll
                    DoItemRoll(id);
                }
                else if (args.EventName.StartsWith("CONFIRM_")) // confirms loot+de rolling "will be bound"
                {
                    Lua.DoString("ConfirmLootRoll(" + args.Args.ElementAtOrDefault(0) + "," + args.Args.ElementAtOrDefault(1) + ")");
                }
                else if (args.EventName == "LOOT_BIND_CONFIRM") // confirms another will bind it to you popup
                {
                    Lua.DoString("ConfirmLootSlot(" + args.Args.ElementAtOrDefault(0) + ")");
                }
                else if (args.EventName == "USE_BIND_CONFIRM") // confirms bop loot
                {
                    Lua.DoString("ConfirmBindOnUse()");
                }
                else if (args.EventName.EndsWith("_CONFIRM")) // confirms (auto)equip popup "will bind it to you"
                {
                    Lua.DoString("EquipPendingItem(" + args.Args.ElementAtOrDefault(0) + ")");
                }
                else if (args.EventName == "ITEM_PUSH") // pulse shortly after we get an item
                {
                    EquipMeSettings.Instance.NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
                }
                else // catches spec change and new talent point placements to reload config
                {
                    Log("Reading new settings (context:{0})", args.EventName);
                    EquipMeSettings.Instance.LoadSettings();
                }
                LogDebug("event({0}) - {1}", args.EventName, args.Args.Aggregate((a, b) => a.ToString() + "," + b.ToString()));
            }
            catch (Exception ex)
            {
                Log("Exception\n{0}", ex); // print the exception cuz hb just ignores
            }
        }

        #endregion

        #region pulse

        public override void Pulse()
        {
            // don't, just don't
            if (StyxWoW.Me == null || !StyxWoW.IsInGame || !StyxWoW.IsInWorld || EquipMeSettings.Instance.NextPulse > DateTime.Now)
            {
                return;
            }

            // don't try to pulse when the settings form is up
            if (_settingsForm != null && _settingsForm.Visible)
            {
                EquipMeSettings.Instance.NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
                return;
            }

            // if we're in combat set to next, wait for out of combat
            if (StyxWoW.Me.Combat)
            {
                EquipMeSettings.Instance.NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
                return;
            }
            else // otherwise set to pulsefreq in settings and continue
            {
                EquipMeSettings.Instance.NextPulse = DateTime.Now + TimeSpan.FromSeconds(EquipMeSettings.Instance.PulseFrequency);
            }

            if (string.Equals(EquipMeSettings.Instance.WeightSetName, "blank", StringComparison.OrdinalIgnoreCase))
            {
                if (StyxWoW.Me.Level < 10)
                {
                    Log("Using lowbie weight settings");
                    EquipMeSettings.Instance.WeightSet_Current = EquipMeSettings.WeightSet_Lowbie;
                }
                else
                {
                    Log("Updating blank stats from wowhead");
                    UpdateWowhead();
                }
                EquipMeSettings.Instance.SaveSettings();
            }
            // enumerate each item
            foreach (var item_inv in StyxWoW.Me.BagItems.Where(i => !EquipMeSettings.Instance.BlacklistedInventoryItems.Contains(i.Guid)))
            {
                if (!StyxWoW.Me.CanEquipItem(item_inv))
                {
                    // blacklist if we didn't/can't equip it
                    EquipMeSettings.Instance.BlacklistedInventoryItems.Add(item_inv.Guid);
                    continue;
                }
                var item_score = CalcScore(item_inv);
                var emptySlot = InventorySlot.None;
                if (HasEmpty(item_inv.ItemInfo, out emptySlot) && item_score > 0)
                {
                    Log("Equipping {0} (score: {1}) into empty slot: {2}", item_inv.Name, item_score, (InventorySlot)emptySlot);
                    DoEquip(item_inv.BagIndex, item_inv.BagSlot, emptySlot);
                    EquipMeSettings.Instance.NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
                    EquipMeSettings.Instance.BlacklistedInventoryItems.Add(item_inv.Guid);
                    return;
                }
                // get a list of equipped items and their scores
                var equipped_items = GetReplaceableItems(item_inv.ItemInfo, item_inv.IsSoulbound);
                if (equipped_items.Count <= 0)
                {
                    EquipMeSettings.Instance.BlacklistedInventoryItems.Add(item_inv.Guid);
                    continue;
                }
                var worst_item = equipped_items.OrderBy(ret => ret.Value.score).FirstOrDefault();
                //Log("Checking item {0} - {1}", item_inv, item_score);
                if (worst_item.Key != null && item_score > worst_item.Value.score)
                {
                    // check the bag doesn't exist inside the bag it's trying to replace
                    if (worst_item.Key.ItemInfo.BagSlots > 0 && worst_item.Key.Guid == item_inv.ContainerGuid)
                    {
                        // don't try equip a bag inside another bag, move bag to backpack first
                        Log("Moving bag: {0} into main backpack before equip.", item_inv.Name);
                        Lua.DoString("local slot = 1; for checkslot=1,16 do if GetContainerItemID(0, checkslot) == nil then slot = checkslot; break; end; end; ClearCursor(); PickupContainerItem({0}, {1}); PickupContainerItem(0, slot); ClearCursor();", item_inv.BagIndex + 1, item_inv.BagSlot + 1);
                        EquipMeSettings.Instance.NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
                        return;
                    }
                    Log("Equipping {0} (score: {1}) over equipped {2} (score: {3}) - slot: {4}", item_inv.Name, item_score, worst_item.Key.Name, worst_item.Value.score, worst_item.Value.slot);
                    DoEquip(item_inv.BagIndex, item_inv.BagSlot, worst_item.Value.slot);
                    EquipMeSettings.Instance.NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
                    EquipMeSettings.Instance.BlacklistedInventoryItems.Add(item_inv.Guid);
                    return;
                }
            }

            if (EquipMeSettings.Instance.GemEquipped)
            {
                foreach (var slot in Enum.GetValues(typeof(InventorySlot)).OfType<InventorySlot>())
                {
                    if (slot == InventorySlot.None || slot == InventorySlot.AmmoSlot || slot == InventorySlot.End)
                    {
                        continue;
                    }
                    var equipped_item = StyxWoW.Me.Inventory.GetItemBySlot((uint)slot - 1);
                    if (equipped_item == null)
                    {
                        continue;
                    }
                    for (int gemslot = 0; gemslot < 3; gemslot++)
                    {
                        if (equipped_item.GetSocketColor(gemslot) == WoWSocketColor.None || ItemHasGem(equipped_item, gemslot))
                        {
                            continue;
                        }
                        var bag_gem =
                            (from item in StyxWoW.Me.BagItems
                             where item.ItemInfo.ItemClass == WoWItemClass.Gem && !EquipMeSettings.Instance.BlacklistedInventoryItems.Contains(item.Guid)
                             let score = CalcScore(item)
                             where score > 0 && GemFitsIn(item.ItemInfo.GemClass, equipped_item.GetSocketColor(gemslot))
                             orderby score descending
                             select new { item, score }).FirstOrDefault();
                        if (bag_gem == null || bag_gem.item == null)
                        {
                            continue;
                        }
                        EquipMeSettings.Instance.BlacklistedInventoryItems.Add(bag_gem.item.Guid);
                        Log("Equipping gem {0} (score: {1}) into item: {2}, slot id: {3}, colour: {4}", bag_gem.item.Name, bag_gem.score, equipped_item.Name, gemslot + 1, equipped_item.ItemInfo.SocketColor[gemslot]);
                        Lua.DoString("ClearCursor(); SocketInventoryItem({0}); PickupContainerItem({1}, {2}); ClickSocketButton({3}); AcceptSockets(); ClearCursor(); CloseSocketInfo();", (int)slot, bag_gem.item.BagIndex + 1, bag_gem.item.BagSlot + 1, gemslot + 1);
                        EquipMeSettings.Instance.NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
                        return;
                    }
                }
            }
        }

        #endregion

    }
}