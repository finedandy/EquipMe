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
            RegisterLuaEvents();
            HandleLuaEvent(null, new LuaEventArgs("PLUGIN.INIT", 0, null));
        }

        public override void Dispose()
        {
            EquipMeSettings.Instance.SaveSettings();
        }

        #endregion

        #region HandleLuaEvent

        /// <summary>
        /// (Re)registers lua events as set in _luaEvents
        /// </summary>
        private void RegisterLuaEvents()
        {
            _luaEvents.ForEach(evt => Lua.Events.DetachEvent(evt, HandleLuaEvent)); // TODO: is this really necessary ?
            _luaEvents.ForEach(evt => Lua.Events.AttachEvent(evt, HandleLuaEvent));
        }

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
                    // get the rollid from the event args
                    var id = ToInteger(args.Args.ElementAtOrDefault(0).ToString());
                    // do a barrel roll
                    DoItemRoll(id);
                }
                else if (args.EventName == "PLAYER_ENTERING_WORLD") // re-initialise when we relog/reload etc
                {
                    Initialize(); // TODO: this can be removed if we don't need to rehook lua events any more, so it just gets caught in the else{}
                }
                else if (args.EventName.StartsWith("CONFIRM_")) // confirms loot rolling "will be bound"
                {
                    Lua.DoString("ConfirmLootRoll(" + args.Args.ElementAtOrDefault(0) + "," + args.Args.ElementAtOrDefault(1) + ")");
                }
                else if (args.EventName.EndsWith("_CONFIRM")) // confirms equip popup "will bind it to you"
                {
                    Lua.DoString("EquipPendingItem(" + args.Args.FirstOrDefault() + ")");
                }
                else if (args.EventName == "ITEM_PUSH") // pulse shortly after we get an item
                {
                    EquipMeSettings.Instance.NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
                }
                else // catches spec change and new talent point placements to reload config
                {
                    Log("Reading new settings (context:{0})", args.EventName);
                    EquipMeSettings.Instance.LoadSettings();
                    if (string.Equals(EquipMeSettings.Instance.WeightSetName, "blank", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("Updating blank stats from wowhead");
                        UpdateWowhead();
                    }
                }
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
                return;
            }

            // if we're in combat set to next, wait for out of combat
            if (StyxWoW.Me.Combat)
            {
                return;
            }
            else // otherwise set to pulsefreq in settings and continue
            {
                EquipMeSettings.Instance.NextPulse = DateTime.Now + TimeSpan.FromSeconds(EquipMeSettings.Instance.PulseFrequency);
            }

            // enumerate each item
            foreach (var item_inv in StyxWoW.Me.BagItems.Where(i => !EquipMeSettings.Instance.BlacklistedInventoryItems.Contains(i.Guid)))
            {
                var item_score = CalcScore(item_inv.ItemInfo, null);
                var emptySlot = InventorySlot.None;
                if (HasEmpty(item_inv.ItemInfo, out emptySlot) && item_score > 0)
                {
                    Log("Equipping {0} (score: {1}) into empty slot: {2}", item_inv.Name, item_score, (InventorySlot)emptySlot);
                    Lua.DoString("ClearCursor(); PickupContainerItem({0}, {1}); EquipCursorItem({2});", item_inv.BagIndex + 1, item_inv.BagSlot + 1, (int)emptySlot);
                    EquipMeSettings.Instance.NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
                    return;
                }
                else
                {
                    // get a list of equipped items and their scores
                    var equipped_items = GetReplaceableItems(item_inv.ItemInfo, item_inv.IsSoulbound);
                    if (equipped_items.Count <= 0)
                    {
                        //Log("No replaceable items for: {0}", item_inv.Name);
                        continue;
                    }
                    var worst_item = equipped_items.OrderBy(ret => ret.Value.score).FirstOrDefault();
                    //Log("Checking item {0} - {1}", item_inv, item_score);
                    if (worst_item.Key != null && item_score > worst_item.Value.score)
                    {
                        Log("Equipping {0} (score: {1}) over equipped {2} (score: {3}) - slot: {4}", item_inv.Name, item_score, worst_item.Key.Name, worst_item.Value.score, (int)worst_item.Value.slot);
                        Lua.DoString("ClearCursor(); PickupContainerItem({0}, {1}); EquipCursorItem({2});", item_inv.BagIndex + 1, item_inv.BagSlot + 1, (int)worst_item.Value.slot);
                        EquipMeSettings.Instance.NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
                        return;
                    }
                }

                // blacklist if we didn't equip it
                EquipMeSettings.Instance.BlacklistedInventoryItems.Add(item_inv.Guid);
            }
        }

        #endregion

    }
}
