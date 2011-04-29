using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Styx.Logic.Inventory;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace EquipMe
{
    public partial class EquipMe
    {

        #region item roll

        /// <summary>
        /// Enum to pass to Lua RollOnLoot
        /// </summary>
        public enum LootRollType
        {
            Pass = 0,
            Need = 1,
            Greed = 2,
            Disenchant = 3
        }

        /// <summary>
        /// Returns the best roll type based on whether we want an item or not
        /// </summary>
        /// <param name="roll_id">roll id</param>
        /// <param name="wantitem">whether we want the item</param>
        /// <returns>what can be rolled</returns>
        public static LootRollType GetRollType(int roll_id, bool wantitem)
        {
            // grab the actions available for us to need/greed/de
            var roll_actionsavailable = Lua.GetReturnValues("return select(6, GetLootRollItemInfo(" + roll_id + "))");
            bool canNeed = ToBoolean(roll_actionsavailable.ElementAtOrDefault(0));
            bool canGreed = ToBoolean(roll_actionsavailable.ElementAtOrDefault(1));
            bool canDisenchant = ToBoolean(roll_actionsavailable.ElementAtOrDefault(2));
            /* Set the roll type, logic is as follows:
             * 1. If we can NEED it and we want it = roll need
             * 2. If we can GREED it and we want it = roll greed
             * 3. If we can disenchant it = roll disenchant
             * 4. If we can greed it = roll greed
             * 5. Otherwise pass (this shouldn't happen)
             */
            return canNeed && wantitem ? LootRollType.Need :
                   canGreed && wantitem ? LootRollType.Greed :
                   canDisenchant ? LootRollType.Disenchant :
                   canGreed ? LootRollType.Greed :
                   LootRollType.Pass;
        }

        private void DoItemRoll(int roll_id)
        {
            // don't roll on loot if the setting is off
            if (!EquipMeSettings.Instance.RollOnLoot)
            {
                return;
            }
            // pull the itemstring from the itemlink for the roll
            var roll_itemstring = Lua.GetReturnVal<string>("return string.match(GetLootRollItemLink(" + roll_id + "), 'item[%-?%d:]+')", 0).Trim();
            // if the itemstring is empty for whatever reason, don't do anything
            // TODO: should this attempt to roll pass?
            if (string.IsNullOrEmpty(roll_itemstring))
            {
                return;
            }
            // pulls the item id from the itemstring
            var roll_itemstring_split = roll_itemstring.Split(':');
            var roll_itemid = ToUnsignedInteger(roll_itemstring_split.ElementAtOrDefault(1));
            // don't bother rolling if it's bad
            if (roll_itemid <= 0)
            {
                Log("Bad item in roll, rolling pass");
                Lua.DoString("RollOnLoot(" + roll_id + "," + (int)LootRollType.Pass + ")");
                return;
            }
            // grabs the item info
            var roll_iteminfo = ItemInfo.FromId(roll_itemid);
            // pulls the name from the item info
            var roll_itemname = roll_iteminfo.Name;
            // checks if there is a suffix and appends to roll_itemname
            var roll_item_suffix = ToUnsignedInteger(roll_itemstring_split.ElementAtOrDefault(7));
            if (roll_item_suffix > 0)
            {
                var suffix = StyxWoW.Db[Styx.Patchables.ClientDb.ItemRandomProperties].GetRow(roll_item_suffix).GetField<string>(7);
                if (!string.IsNullOrEmpty(suffix))
                {
                    roll_itemname += " " + suffix;
                }
            }
            // checks if the item is listed on the need list and roll need if so
            if (EquipMeSettings.Instance.RollNeedList.Split(',').Where(needlistitem => ToInteger(needlistitem) > 0).Any(needlistitem =>
                ToInteger(needlistitem) == roll_iteminfo.Id ||
                string.Equals(needlistitem.Trim(), roll_itemname, StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(roll_itemname, needlistitem, RegexOptions.IgnoreCase)))
            {
                Log("Rolling need (if possible) on item matched in need list: {0}", roll_itemname);
                Lua.DoString("RollOnLoot(" + roll_id + "," + (int)GetRollType(roll_id, true) + ")");
                return;
            }
            // if it's not on the need list and doesn't have an inv type (ie, not equippable), greed/de/pass
            if (roll_iteminfo.InventoryType == InventoryType.None)
            {
                var rolltype = GetRollType(roll_id, false);
                Log("Rolling {0} on non-equipped item: {1}", rolltype, roll_itemname);
                Lua.DoString("RollOnLoot(" + roll_id + "," + (int)rolltype + ")");
            }
            else // otherwise compare it to equipped items
            {
                // grab the item stats from wow (this takes into account random properties as it uses a wow func to construct)
                var roll_itemstats = new ItemStats(roll_itemstring);
                // calculates the item score based off given info and stats (noting that this is not an equipped item)
                var roll_itemscore = CalcScore(roll_iteminfo, roll_itemstats);
                var need_item = false;
                // if there's an empty slot
                var emptySlot = InventorySlot.None;
                if (HasEmpty(roll_iteminfo.InventoryType, out emptySlot) && roll_itemscore > 0)
                {
                    Log("Found empty slot: {0}", emptySlot);
                    need_item = true;
                }
                else
                {
                    // get a list of equipped items and their scores
                    var equipped_items = GetReplaceableItems(roll_iteminfo, false);
                    foreach (var equipped_item_kvp in equipped_items)
                    {
                        if (roll_itemscore > equipped_item_kvp.Value.score)
                        {
                            Log(" - Equipped: {0}, score: {1}", equipped_item_kvp.Key.Name, equipped_item_kvp.Value);
                            need_item = true;
                        }
                    }
                }
                var rolltype = GetRollType(roll_id, need_item);
                Log("Rolling {0} on: {1}, score: {2}", rolltype, roll_itemname, roll_itemscore);
                Lua.DoString("RollOnLoot(" + roll_id + "," + (int)rolltype + ")");
            }
        }

        #endregion

        #region wowhead

        /// <summary>
        /// wowhead stat list to stat Styx.Logic.Inventory.Stat mapping
        /// </summary>
        public static readonly Dictionary<string, Stat> WowheadStatList = new Dictionary<string, Stat>
        {
            { "mastrtng", Stat.Mastery },
            { "str", Stat.Strength },
            { "hitrtng", Stat.HitRating },
            { "exprtng", Stat.ExpertiseRating },
            { "critstrkrtng", Stat.CriticalStrikeRating },
            { "agi", Stat.Agility },
            { "hastertng", Stat.HasteRating },
            { "armor", Stat.Armor },
            { "sta", Stat.Stamina },
            { "dodgertng", Stat.DodgeRating },
            { "parryrtng", Stat.ParryRating },
            { "int", Stat.Intellect },
            { "splpwr", Stat.SpellPower },
            { "mledps", Stat.DPS },
            { "rgddps", Stat.DPS },
            { "spi", Stat.Spirit },
            { "armorbonus", Stat.Armor }, // not sure if this is correct
            { "atkpwr", Stat.AttackPower },
            { "dps", Stat.DPS },
            { "health", Stat.Health },
            { "feratkpwr", Stat.AttackPowerInForms }
        };

        /// <summary>
        /// Updated the current weightset with weights from wowhead
        /// TODO: make this a bit neater (eg, use more regex
        /// </summary>
        public static void UpdateWowhead()
        {
            try
            {
                LogDebug("Downloading weights from wowhead...");
                string result = new WebClient().DownloadString("http://www.wowhead.com/data=weight-presets");
                LogDebug("Weights downloaded ({0})", result.Length);
                var currentclass = WoWClass.None;
                var _availableWeightSets = new List<WeightSet>();
                foreach (var s in result.Split('\n'))
                {
                    var line = s.Trim();
                    var m = Regex.Match(line, @"(\d+): {");
                    if (m.Success)
                    {
                        var i = ToInteger(line.Substring(0, line.IndexOf(":")));
                        currentclass = (WoWClass)i;
                        // ignore it if the class is bogus
                        if (currentclass == WoWClass.None)
                        {
                            continue;
                        }
                    }
                    if (!line.Contains("__icon"))
                    {
                        continue;
                    }
                    var axlstats = new Dictionary<Stat, float>();
                    var spec = line.Substring(0, line.IndexOf(":"));
                    var weightsetname = currentclass + "." + spec;
                    var statsline = line.Substring(line.IndexOf("{") + 1);
                    statsline = statsline.Substring(0, statsline.IndexOf("}"));
                    foreach (var statline in statsline.Split(','))
                    {
                        var statname = statline.Substring(0, statline.IndexOf(":"));
                        // ignore stats that have _ in them (ie, __icon)
                        if (statname.Contains("_"))
                        {
                            continue;
                        }
                        // grab the stat from the wowhead list
                        if (!WowheadStatList.ContainsKey(statname))
                        {
                            continue;
                        }
                        var stattype = WowheadStatList[statname];
                        // skip it if it's bs (this shouldn't happen)
                        if (stattype == Stat.None)
                        {
                            continue;
                        }
                        // add it to the list of actual stats
                        try
                        {
                            float statval = ToFloat(statline.Substring(statline.IndexOf(":") + 1));
                            axlstats.Add(stattype, statval);
                        }
                        catch (Exception)
                        {
                            // skip if there was an error parsing the value or if the item already existed in the list
                            continue;
                        }
                    }
                    if (!axlstats.ContainsKey(Stat.Stamina)) // just cuz
                    {
                        axlstats.Add(Stat.Stamina, 1);
                    }

                    if (!axlstats.ContainsKey(Stat.Armor)) // just cuz
                    {
                        axlstats.Add(Stat.Armor, 1);
                    }
                    foreach (Stat notaddedstat in EquipMeSettings.WeightSet_Blank.Weights.Where(blank => !axlstats.ContainsKey(blank.Key)).Select(thekey => thekey.Key))
                    {
                        axlstats.Add(notaddedstat, 0);
                    }
                    // add the weight set
                    LogDebug("Adding weightset: {0}", weightsetname);
                    _availableWeightSets.Add(new WeightSet(weightsetname, axlstats));
                }

                var ret = GetSpecDetails();
                var talentTabId = ToInteger(ret[0]);
                LogDebug("Talent Tab Id: {0}, spec: {1}", talentTabId, TalentTabIds[talentTabId].ToLower());
                if (talentTabId == 0)
                {
                    Log("Unable to determine weight set, using lowbie");
                    EquipMeSettings.Instance.WeightSet_Current = EquipMeSettings.WeightSet_Lowbie;
                }
                else
                {
                    var isdruidtankspecced = ret[1] != "0";
                    foreach (var set in _availableWeightSets.Where(ws => ws.Name.StartsWith(StyxWoW.Me.Class.ToString())))
                    {
                        var spec = set.Name.Substring(set.Name.IndexOf(".") + 1);

                        if (spec.EndsWith("dps"))
                        {
                            if (StyxWoW.Me.Class == WoWClass.DeathKnight)
                            {
                                spec = spec.Replace("dps", "");
                            }
                            else if (StyxWoW.Me.Class == WoWClass.Druid && !isdruidtankspecced)
                            {
                                spec = spec.Replace("dps", "");
                            }
                        }
                        else if (spec.EndsWith("tank"))
                        {
                            if (StyxWoW.Me.Class == WoWClass.Druid && isdruidtankspecced)
                            {
                                spec = spec.Replace("tank", "");
                            }
                        }

                        LogDebug("Parsed spec: {0}", spec);

                        if (talentTabId != 0)
                        {
                            if (!TalentTabIds.ContainsKey(talentTabId) || !TalentTabIds[talentTabId].ToLower().StartsWith(spec))
                                continue;
                        }

                        Log("Found wowhead weight set: {0}", set.Name);
                        EquipMeSettings.Instance.WeightSet_Current = set;
                        break;
                    }
                }
                foreach (var kvp in EquipMeSettings.Instance.WeightSet_Current.Weights.Where(they => they.Value > 0))
                {
                    Log("- {0} = {1}", kvp.Key, kvp.Value);
                }
            }
            catch (Exception ex)
            {
                Log("Unable to update weight set from wowhead, exception:\n{0}", ex);
            }
        }

        #endregion

        #region spec

        /// <summary>
        /// list of talent tab ids to english string (supports multilang clients)
        /// </summary>
        public static readonly Dictionary<int, string> TalentTabIds = new Dictionary<int, string>
        {
            { 0, "Lowbie" },
            { 181 , "Combat" },
            { 182 , "Assassination" },
            { 183 , "Subtlety" },
            { 261 , "Elemental" },
            { 262 , "Restoration" },
            { 263 , "Enhancement" },
            { 398 , "Blood" },
            { 399 , "Frost" },
            { 400 , "Unholy" },
            { 409 , "Tenacity" },
            { 410 , "Ferocity" },
            { 411 , "Cunning" },
            { 746 , "Arms" },
            { 748 , "Restoration" },
            { 750 , "Feral Combat" },
            { 752 , "Balance" },
            { 760 , "Discipline" },
            { 795 , "Shadow" },
            { 799 , "Arcane" },
            { 807 , "Marksmanship" },
            { 809 , "Survival" },
            { 811 , "Beast Mastery" },
            { 813 , "Holy" },
            { 815 , "Fury" },
            { 823 , "Frost" },
            { 831 , "Holy" },
            { 839 , "Protection" },
            { 845 , "Protection" },
            { 851 , "Fire" },
            { 855 , "Retribution" },
            { 865 , "Destruction" },
            { 867 , "Demonology" },
            { 871 , "Affliction" }
        };

        /// <summary>
        /// lua func to return spec details
        /// </summary>
        /// <returns>
        /// string[]
        /// 0 = (int) primary talent tab id
        /// 1 = (int) is druid tank spec (talent Thick Hide in feral tree - GetTalentInfo(2,11))
        /// </returns>
        public static string[] GetSpecDetails()
        {
            return Lua.GetReturnValues("local pointspec = 0; " +
                    "local primaryId = 0; " +
                    "local totalpointspent = 0; " +
                    "for i=1,3 do " +
                        "local id, _, _, _, pointsspent = GetTalentTabInfo(i); " +
                        "totalpointspent = totalpointspent + pointsspent; " +
                        "if pointsspent >= pointspec then " +
                            "pointspec = pointsspent; " +
                            "primaryId = id; " +
                        "end " +
                    "end " +
                    "if totalpointspent == 0 then " +
                        "primaryId = 0; " +
                    "end " +
                    "return primaryId, select(5, GetTalentInfo(2,11)); ").ToArray();
        }

        /// <summary>
        /// Gets the english spec name from the talentid -> string mapping
        /// </summary>
        /// <returns>english spec name or Lowbie if not specced</returns>
        public static string GetSpecName()
        {
            try
            {
                return TalentTabIds[ToInteger(GetSpecDetails()[0])];
            }
            catch (Exception)
            {
                return "Lowbie";
            }
        }

        #endregion

        #region load weight set

        /// <summary>
        /// Loads a weightset from an xml file, pulled from old autoequip
        /// </summary>
        /// <param name="path">path to file</param>
        /// <returns>parsed weightset</returns>
        public static WeightSet LoadWeightSetFromXML(string path)
        {
            XElement weightElm = null;
            try
            {
                weightElm = XElement.Load(path);
            }
            catch (Exception)
            {
                return null;
            }
            if (weightElm == null)
            {
                Log("Error reading file: {0}, no data read!", path);
                return null;
            }
            string weightSetName = "Current";
            if (weightElm.HasAttributes)
            {
                weightSetName = "";
                foreach (XAttribute att in weightElm.Attributes())
                {
                    weightSetName += att.Value;
                }
            }
            var weightDict = new Dictionary<Stat, float>();

            foreach (XElement element in weightElm.Elements())
            {
                string name = element.Name.ToString();
                Stat stat;
                try
                {
                    stat = (Stat)Enum.Parse(typeof(Stat), name, true);
                }
                catch (ArgumentException)
                {
                    Log("Unknown stat name {0} while parsing weight set {1}. Skipping stat.", name, weightSetName);
                    continue;
                }

                float weight;
                if (!float.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out weight))
                {
                    Log("Invalid stat value {0} for stat {1} while parsing weight set {2}. Floating value expected. Skipping stat.", element.Value, name, weightSetName);
                    continue;
                }

                if (weightDict.ContainsKey(stat))
                {
                    Log("Weight set {0} contains duplicate stat {1}", weightSetName, stat);
                    continue;
                }

                weightDict.Add(stat, weight);
            }

            return new WeightSet(weightSetName, weightDict);
        }

        #endregion

        #region logging

        private static string _appName = Assembly.GetExecutingAssembly().GetName().Name.Substring(0, Assembly.GetExecutingAssembly().GetName().Name.IndexOf('_'));

        /// <summary>
        /// Writes a (formatted) string to the debug log
        /// </summary>
        /// <param name="s">text to log</param>
        /// <param name="args">params for any format (optional)</param>
        public static void LogDebug(string s, params object[] args)
        {
            Logging.WriteDebug(Color.DarkSlateGray, "[" + _appName + "] " + s, args);
        }

        /// <summary>
        /// Writes a (formatted) string to the log
        /// </summary>
        /// <param name="s">text to log</param>
        /// <param name="args">params for any format (optional)</param>
        public static void Log(string s, params object[] args)
        {
            Logging.Write(Color.DarkSlateGray, "[" + _appName + "] " + s, args);
        }

        #endregion

        #region string -> int/uint/float/bool

        // just stfu kthx

        /// <summary>
        /// Converts string to int, stripping whitespace
        /// </summary>
        /// <param name="s">input string</param>
        /// <returns>int</returns>
        public static int ToInteger(string s)
        {
            try
            {
                return int.Parse(s.Trim());
            }
            catch (Exception) { }
            return 0;
        }

        /// <summary>
        /// Converts string to uint, stripping whitespace
        /// </summary>
        /// <param name="s">input string</param>
        /// <returns>uint</returns>
        public static uint ToUnsignedInteger(string s)
        {
            try
            {
                return uint.Parse(s.Trim());
            }
            catch (Exception) { }
            return 0;
        }

        /// <summary>
        /// Converts string to float, stripping whitespace
        /// </summary>
        /// <param name="s">input string</param>
        /// <returns>float</returns>
        public static float ToFloat(string s)
        {
            try
            {
                return float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            catch (Exception) { }
            return 0f;
        }

        /// <summary>
        /// Converts string to bool, stripping whitespace
        /// </summary>
        /// <param name="s">input string</param>
        /// <returns>bool</returns>
        public static bool ToBoolean(string s)
        {
            try
            {
                var wang = s.Trim();
                return wang == "1" ? true : bool.Parse(wang);
            }
            catch (Exception) { }
            return false;
        }

        #endregion

        #region get replaceable items

        /// <summary>
        /// If there is an empty slot available which can be filled
        /// </summary>
        /// <param name="type">item type</param>
        /// <returns>yes/no</returns>
        public static bool HasEmpty(InventoryType type, out InventorySlot emptySlot)
        {
            emptySlot = InventorySlot.None;
            foreach (var slot in InventoryManager.GetInventorySlotsByEquipSlot(type))
            {
                var equipped_item = StyxWoW.Me.Inventory.Equipped.GetItemBySlot((uint)slot - 1);
                if (equipped_item == null)
                {
                    emptySlot = slot;
                    return true;
                }
            }
            return false;
        }

        public struct ItemSlotInto
        {
            public float score;
            public InventorySlot slot;
        }

        /// <summary>
        /// Gets a list of replaceable items (if any) taking into account whether or not we can equip the item, and user settings
        /// </summary>
        /// <param name="item">item to check against</param>
        /// <returns>list of potential item replacements</returns>
        public static Dictionary<WoWItem, ItemSlotInto> GetReplaceableItems(ItemInfo item, bool isBound)
        {
            // check if we can even equip the item
            if (!StyxWoW.Me.CanEquipItem(item))
            {
                return new Dictionary<WoWItem, ItemSlotInto>();
            }
            // dont equip it if it's not the armour type we want
            if (EquipMeSettings.Instance.OnlyEquipArmourType != WoWItemArmorClass.None && EquipMeSettings.Instance.OnlyEquipArmourType != item.ArmorClass)
            {
                return new Dictionary<WoWItem, ItemSlotInto>();
            }
            // don't try to equip anything for a blacklisted slot
            if (EquipMeSettings.Instance.BlacklistedSlots.Split(',').Any(slotval =>
                ToInteger(slotval) == (int)item.InventoryType ||
                string.Equals(slotval.Trim(), item.InventoryType.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                return new Dictionary<WoWItem, ItemSlotInto>();
            }
            // if it's not an item type that can be equipped
            if (item.InventoryType == InventoryType.None)
            {
                return new Dictionary<WoWItem, ItemSlotInto>();
            }
            // dont try to equip anything for a blacklisted boe quality
            if (item.Bond == WoWItemBondType.OnEquip && !isBound)
            {
                if (EquipMeSettings.Instance.IngoreEpicBOE && item.Quality == WoWItemQuality.Epic)
                {
                    return new Dictionary<WoWItem, ItemSlotInto>();
                }
                // dont try to equip anything for a blacklisted item quality
                if (EquipMeSettings.Instance.IgnoreRareBOE && item.Quality == WoWItemQuality.Rare)
                {
                    return new Dictionary<WoWItem, ItemSlotInto>();
                }
            }
            var equipped_items = new Dictionary<WoWItem, ItemSlotInto>();
            foreach (var slot in InventoryManager.GetInventorySlotsByEquipSlot(item.InventoryType))
            {
                //Log("Slot in: {0}, slot out: {1}", item.InventoryType, slot);
                if (slot == InventorySlot.None)
                {
                    continue;
                }
                var equipped_item = StyxWoW.Me.Inventory.Equipped.GetItemBySlot((uint)slot - 1);
                if (equipped_item == null)
                {
                    continue;
                }
                // dont replace it if heirloom in target slot and ignore heirlooms is on
                if (EquipMeSettings.Instance.IgnoreHeirlooms && equipped_item.Quality == WoWItemQuality.Heirloom)
                {
                    continue;
                }
                var isl = new ItemSlotInto();
                isl.score = CalcScore(equipped_item.ItemInfo, null);
                isl.slot = slot;
                //Log("Equipped item: {0} - {1}", equipped_item.Name, isl.score);
                equipped_items.Add(equipped_item, isl);
            }
            return equipped_items;
        }

        #endregion

        #region calc item score

        /// <summary>
        /// Calculates an item's score based on the current weightset
        /// </summary>
        /// <param name="item">ItemInfo checked</param>
        /// <param name="stats">ItemStats checked (can be null)</param>
        /// <returns>itemscore is returned (# of slots are returned as score for bags)</returns>
        public static float CalcScore(ItemInfo item, ItemStats stats)
        {
            if (item.BagSlots > 0)
            {
                return item.BagSlots;
            }
            else
            {
                if (stats == null)
                {
                    return EquipMeSettings.Instance.WeightSet_Current.EvaluateItem(item);
                }
                else
                {
                    return EquipMeSettings.Instance.WeightSet_Current.EvaluateItem(item, stats);
                }
            }
        }

        #endregion

    }
}
