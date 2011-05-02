using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Styx.Logic.Inventory;
using Styx.Patchables;
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
            // pull the itemstring from the itemlink for the roll
            var roll_itemstring = Lua.GetReturnVal<string>("return string.match(GetLootRollItemLink(" + roll_id + "), 'item[%-?%d:]+')", 0).Trim();
            // if the itemstring is empty for whatever reason, don't do anything
            if (string.IsNullOrEmpty(roll_itemstring))
            {
                Lua.DoString("RollOnLoot(" + roll_id + "," + (int)LootRollType.Pass + ")");
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
                var rolltypeneed = GetRollType(roll_id, true);
                Log("Rolling need (if possible - {0}) on item matched in need list: {1}", rolltypeneed, roll_itemname);
                Lua.DoString("RollOnLoot(" + roll_id + "," + (int)rolltypeneed + ")");
                return;
            }
            // if we can't equip it, greed/de/pass (taking into account ignore level settings
            GameError g_error;
            bool b = StyxWoW.Me.CanEquipItem(roll_iteminfo, out g_error);
            if (!b && !(EquipMeSettings.Instance.RollIgnoreLevel && g_error == GameError.CantEquipLevelI && StyxWoW.Me.Level + EquipMeSettings.Instance.RollIgnoreLevelDiff <= roll_iteminfo.RequiredLevel))
            {
                var rolltypenonequip = GetRollType(roll_id, false);
                Log("Rolling {0} on non-equippable item: {1}", rolltypenonequip, roll_itemname);
                Lua.DoString("RollOnLoot(" + roll_id + "," + (int)rolltypenonequip + ")");
            }
            // grab the item stats from wow (this takes into account random properties as it uses a wow func to construct)
            var roll_itemstats = new ItemStats(roll_itemstring);
            roll_itemstats.DPS = roll_iteminfo.DPS;
            // calculates the item score based off given info and stats (noting that this is not an equipped item)
            var roll_itemscore = CalcScore(roll_iteminfo, roll_itemstats);
            var need_item = false;
            // if there's an empty slot
            var emptySlot = InventorySlot.None;
            if (HasEmpty(roll_iteminfo, out emptySlot) && roll_itemscore > 0)
            {
                Log(" - Found empty slot: {0}", emptySlot);
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
                Log("Using lowbie weightset");
                EquipMeSettings.Instance.WeightSet_Current = EquipMeSettings.WeightSet_Lowbie;
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

        /// <summary>
        /// Writes a (formatted) string to the debug log
        /// </summary>
        /// <param name="s">text to log</param>
        /// <param name="args">params for any format (optional)</param>
        public static void LogDebug(string s, params object[] args)
        {
            Logging.WriteDebug(Color.DarkSlateGray, "[EquipMe] " + s, args);
        }

        /// <summary>
        /// Writes a (formatted) string to the log
        /// </summary>
        /// <param name="s">text to log</param>
        /// <param name="args">params for any format (optional)</param>
        public static void Log(string s, params object[] args)
        {
            Logging.Write(Color.DarkSlateGray, "[EquipMe] " + s, args);
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

        #region item replacements

        /// <summary>
        /// Checks if a weapon class is in a string, by id or name
        /// </summary>
        /// <param name="clazz">weapon class</param>
        /// <param name="str">string</param>
        /// <returns>yes/no</returns>
        public static bool IsWeaponClassInString(WoWItemWeaponClass clazz, string str)
        {
            if (str.Split(',').Where(slotstr => !string.IsNullOrEmpty(slotstr.Trim())).Any(slotstr =>
                (WoWItemWeaponClass)ToInteger(slotstr) == clazz ||
                string.Equals(clazz.ToString(), slotstr, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Checks against settings if we can equip a weapon by it's class
        /// </summary>
        /// <param name="slot">item slot</param>
        /// <param name="clazz">weapon class</param>
        /// <returns>true if we can equip it, false if we can't</returns>
        public static bool CanEquipWeapon(InventorySlot slot, WoWItemWeaponClass clazz)
        {
            // mh
            if (slot == InventorySlot.MainHandSlot && EquipMeSettings.Instance.WeaponMainHand.Trim().Length > 0)
            {
                return IsWeaponClassInString(clazz, EquipMeSettings.Instance.WeaponMainHand.Trim());
            }
            // oh
            if (slot == InventorySlot.SecondaryHandSlot && EquipMeSettings.Instance.WeaponOffHand.Trim().Length > 0)
            {
                return IsWeaponClassInString(clazz, EquipMeSettings.Instance.WeaponOffHand.Trim());
            }
            // ranged
            if (slot == InventorySlot.RangedSlot && EquipMeSettings.Instance.WeaponRanged.Trim().Length > 0)
            {
                return IsWeaponClassInString(clazz, EquipMeSettings.Instance.WeaponRanged.Trim());
            }
            // otherwise we can equip it no probs
            return true;
        }

        /// <summary>
        /// If there is an empty slot available which can be filled
        /// </summary>
        /// <param name="type">item type</param>
        /// <returns>yes/no</returns>
        public static bool HasEmpty(ItemInfo item, out InventorySlot emptySlot)
        {
            emptySlot = InventorySlot.None;
            foreach (var slot in InventoryManager.GetInventorySlotsByEquipSlot(item.InventoryType))
            {
                if ((int)slot - 1 < 0)
                {
                    continue;
                }
                // if we can't equip a weapon according to settings
                if (!CanEquipWeapon(slot, item.WeaponClass))
                {
                    continue;
                }
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
        public static Dictionary<WoWItem, ItemSlotInto> GetReplaceableItems(ItemInfo item, bool isSoulBound)
        {
            var equipped_items = new Dictionary<WoWItem, ItemSlotInto>();
            // dont equip it if it's not the armour type we want
            if (EquipMeSettings.Instance.OnlyEquipArmourType != WoWItemArmorClass.None && EquipMeSettings.Instance.OnlyEquipArmourType != item.ArmorClass)
            {
                return equipped_items;
            }
            // don't try to equip anything for a blacklisted slot
            if (EquipMeSettings.Instance.BlacklistedSlots.Split(',').Any(slotval =>
                ToInteger(slotval) == (int)item.InventoryType ||
                string.Equals(slotval.Trim(), item.InventoryType.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                return equipped_items;
            }
            // if it's not an item type that can be equipped
            if (item.InventoryType == InventoryType.None)
            {
                return equipped_items;
            }
            // dont try to equip anything for a blacklisted boe quality
            if (item.Bond == WoWItemBondType.OnEquip && !isSoulBound)
            {
                // epic
                if (EquipMeSettings.Instance.IngoreEpicBOE && item.Quality == WoWItemQuality.Epic)
                {
                    return equipped_items;
                }
                // rare
                if (EquipMeSettings.Instance.IgnoreRareBOE && item.Quality == WoWItemQuality.Rare)
                {
                    return equipped_items;
                }
            }
            foreach (var slot in InventoryManager.GetInventorySlotsByEquipSlot(item.InventoryType))
            {
                if ((int)slot - 1 < 0)
                {
                    continue;
                }
                // if we can't equip a weapon according to settings
                if (!CanEquipWeapon(slot, item.WeaponClass))
                {
                    continue;
                }
                //Log("Slot in: {0}, slot out: {1}", item.InventoryType, slot);
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
                isl.score = CalcScore(equipped_item);
                isl.slot = slot;
                //Log("Equipped item: {0} - {1}", equipped_item.Name, isl.score);
                equipped_items.Add(equipped_item, isl);
            }
            return equipped_items;
        }

        #endregion

        #region calc item score

        /// <summary>
        /// just a wrapper for the real calcscore
        /// </summary>
        /// <param name="item">wowitem to calc score of</param>
        /// <returns></returns>
        public static float CalcScore(WoWItem item)
        {
            return CalcScore(item.ItemInfo, item.GetItemStats());
        }

        /// <summary>
        /// calculates an item score based on info and stats
        /// </summary>
        /// <param name="item">info</param>
        /// <param name="stats">stats</param>
        /// <returns>float score</returns>
        public static float CalcScore(ItemInfo item, ItemStats stats)
        {
            // if it's not a gem or it's a simple gem, just use teh weightset or slots
            if (item.ItemClass != WoWItemClass.Gem || item.GemClass == WoWItemGemClass.Simple)
            {
                return item.BagSlots > 0 ? item.BagSlots : EquipMeSettings.Instance.WeightSet_Current.EvaluateItem(item, stats);
            }
            // this is a dirty fucking dbc hack
            // look up the gemproperties entry
            var gementry = StyxWoW.Db[ClientDb.GemProperties].GetRow((uint)item.InternalInfo.GemProperties);
            if (gementry == null)
            {
                return 0;
            }
            // get the spell index from the gemproperties dbc
            var spellindex = gementry.GetField<uint>(1);
            // look up the spellitemenchantment by the index
            var spellentry = StyxWoW.Db[ClientDb.SpellItemEnchantment].GetRow(spellindex);
            if (spellentry == null)
            {
                return 0;
            }
            var newstats = stats;
            // for each of the 3 stats (minstats used, not maxstats)
            for (uint statnum = 5; statnum <= 7; statnum++)
            {
                try
                {
                    // grab the stat amount
                    var amount = spellentry.GetField<int>(statnum);
                    if (amount <= 0)
                    {
                        continue;
                    }
                    // grab the stat type and convert it to something that can be looked up in the stats dic below
                    var stat = (WoWItemStatType)spellentry.GetField<int>(11);
                    var stattype = (StatTypes)Enum.Parse(typeof(StatTypes), stat.ToString());
                    if (!newstats.Stats.ContainsKey(stattype))
                    {
                        // add it if it doesn't already exist
                        newstats.Stats.Add(stattype, amount);
                    }
                }
                catch (Exception) { }
            }
            return EquipMeSettings.Instance.WeightSet_Current.EvaluateItem(item, newstats);
        }

        #endregion

        #region equip

        /// <summary>
        /// equips an item given a 0 based index and slot, and string
        /// </summary>
        /// <param name="bagindex">0 based index</param>
        /// <param name="bagslot">0 based slot</param>
        /// <param name="slot">inv slot</param>
        public static void DoEquip(int bagindex, int bagslot, InventorySlot slot)
        {
            Lua.DoString("ClearCursor(); PickupContainerItem({0}, {1}); EquipCursorItem({2})", bagindex + 1, bagslot + 1, (int)slot);
        }

        #endregion

        #region gems

        /// <summary>
        /// Checks if an item has a gem in a given slot (0 based slot index)
        /// </summary>
        /// <param name="item">item to check</param>
        /// <param name="slot">0 based index</param>
        /// <returns>true = has a gem in that slot</returns>
        public static bool ItemHasGem(WoWItem item, int slot)
        {
            return item.GetEnchantment((uint)slot + 2).Id > 0;
        }

        /// <summary>
        /// Checks if a gem fits into a given socket, taking into account settings for socket bonuses
        /// </summary>
        /// <param name="item">item class</param>
        /// <param name="socket">socket to check</param>
        /// <returns>yes = gem can fit</returns>
        public static bool GemFitsIn(WoWItemGemClass item, WoWSocketColor socket)
        {
            // if the gem is simple (ie, uncut) or not a gem, can't fit in any socket
            if (item == WoWItemGemClass.Simple || item == WoWItemGemClass.None) 
            {
                return false;
            }
            // if the gem is a meta gem, can only fit in a meta slot
            if (item == WoWItemGemClass.Meta)
            {
                return socket == WoWSocketColor.Meta;
            }
            // if the gem is a prismatic, can fit in anything bar meta
            if (item == WoWItemGemClass.Prismatic)
            {
                return socket != WoWSocketColor.Meta;
            }
            // if we're gemming for the bonus, check the socket matches or it's two types match
            if (EquipMeSettings.Instance.GemBonus)
            {
                if (item == WoWItemGemClass.Orange)
                {
                    return socket == WoWSocketColor.Red || socket == WoWSocketColor.Yellow;
                }
                else if (item == WoWItemGemClass.Green)
                {
                    return socket == WoWSocketColor.Yellow || socket == WoWSocketColor.Blue;
                }
                else if (item == WoWItemGemClass.Purple)
                {
                    return socket == WoWSocketColor.Blue || socket == WoWSocketColor.Red;
                }
                else
                {
                    return string.Equals(item.ToString(), socket.ToString(), StringComparison.OrdinalIgnoreCase);
                }
            }
            // otherwise we can equip any coloured gem into any coloured socket
            return true;
        }

        #endregion

    }
}