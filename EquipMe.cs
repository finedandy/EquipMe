using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Styx.Logic.BehaviorTree;
using Styx.Logic.Combat;
using Styx.Logic.Inventory;
using Styx.Plugins.PluginClass;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace EquipMe
{
    public class EquipMe : HBPlugin
    {

        #region variables

        private EquipMeSettings _theSettings;
        private Form _settingsForm;
        private bool _doUpdateWeights;
        private readonly string _cachedPath = Logging.ApplicationPath + "\\Settings\\EquipMeWeights.txt";
        private readonly List<WeightSet> _availableWeightSets = new List<WeightSet>();
        private WeightSet _currentWeightSet;
        private DateTime _nextPulseTime = DateTime.Now;
        private readonly WebClient _downloadClient = new WebClient();
        private const string WeightSetUrl = "http://www.wowhead.com/data=weight-presets";
        private readonly Dictionary<string, Stat> _wowheadStatList = new Dictionary<string, Stat>
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

        #endregion

        #region override

        public override void Initialize()
        {
            CheckSettings();
            Lua.Events.AttachEvent("ACTIVE_TALENT_GROUP_CHANGED", HandleActiveTalentGroupChanged);
            Lua.Events.AttachEvent("CHARACTER_POINTS_CHANGED", HandleActiveTalentGroupChanged);
        }

        public void HandleActiveTalentGroupChanged(object sender, LuaEventArgs args)
        {
            CheckWeightSet();
        }

        public override bool WantButton
        {
            get
            {
                return true;
            }
        }

        public override string ButtonText
        {
            get
            {
                return "Settings";
            }
        }

        public override void OnButtonPress()
        {
            CheckSettings();
            if (_settingsForm == null || _settingsForm.IsDisposed)
            {
                _settingsForm = CreateSettingsForm();
            }
            _settingsForm.ShowDialog();
        }

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
                return new Version(1, 3);
            }
        }

        #endregion

        #region helper

        private static void Log(string s)
        {
            Logging.Write(Color.DarkSlateGray, "[EquipMe] " + s);
        }

        private void CheckSettings()
        {
            if (_theSettings == null)
            {
                _theSettings = new EquipMeSettings();
            }
        }

        public WeightSet GetCurrentWeightSet
        {
            get
            {
                return _currentWeightSet;
            }
        }

        #endregion

        #region weight set

        private string DownloadWeightSet()
        {
            string result = "";
            try
            {
                result = _downloadClient.DownloadString(WeightSetUrl);
            }
            catch (Exception e)
            {
                Log("Error downloading weight presents from wowhead");
                Log(e.Message);
            }
            return result;
        }

        private string ReadWeightSet()
        {
            var result = "";
            try
            {
                result = File.ReadAllText(_cachedPath);
            }
            catch (Exception e)
            {
                Log("Error reading cached copy of weights");
                Log(e.Message);
            }
            return result;
        }

        private void UpdateWeightSets()
        {
            _availableWeightSets.Clear();
            _currentWeightSet = null;
            string weightsetstring;
            if (_theSettings.UseCachedWeights)
            {
                Log("Using cached copy of weights");
                weightsetstring = ReadWeightSet();
                if (string.IsNullOrEmpty(weightsetstring))
                {
                    Log("Unable to read cached copy of weights!");
                    return;
                }
            }
            else
            {
                Log("Downloading weights from wowhead");
                weightsetstring = DownloadWeightSet();
                if (string.IsNullOrEmpty(weightsetstring))
                {
                    Log("Nothing downloaded from wowhead, trying from cached copy");
                    weightsetstring = ReadWeightSet();
                    if (string.IsNullOrEmpty(weightsetstring))
                    {
                        Log("Unable to read cached copy of weights!");
                        return;
                    }
                }
                else
                {
                    // save a copy of the results just in case
                    try
                    {
                        File.WriteAllText(_cachedPath, weightsetstring);
                    }
                    catch (Exception)
                    { }
                }
            }
            var currentclass = WoWClass.None;
            foreach (var s in weightsetstring.Split('\n'))
            {
                var line = s.Trim();
                var m = Regex.Match(line, @"(\d+): {");
                if (m.Success)
                {
                    var i = int.Parse(line.Substring(0, line.IndexOf(":")));
                    currentclass = (WoWClass)i;
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
                    // otherwise if we don't have the stat, skip it
                    if (!_wowheadStatList.ContainsKey(statname))
                    {
                        continue;
                    }
                    // grab the stat
                    Stat stattype = _wowheadStatList[statname];
                    // skip it if it's bs
                    if (stattype == Stat.None)
                    {
                        continue;
                    }
                    // add it to the list of actual stats
                    try
                    {
                        float statval = float.Parse(statline.Substring(statline.IndexOf(":") + 1));
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
                // add the weight set
                _availableWeightSets.Add(new WeightSet(weightsetname, axlstats));
            }
        }

        private void CheckWeightSet()
        {
            var ret = Lua.GetReturnValues("local pointspec = 0; " +
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
            var isdruidtankspecced = ret[1] != "0";
            foreach (var set in _availableWeightSets.Where(s => s.Name.StartsWith(StyxWoW.Me.Class.ToString())))
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

                var talentTabId = int.Parse(ret[0]);
                //Log("Talent Tab Id: " + talentTabId);
                if (talentTabId != 0)
                {
                    if (!TalentTabIds.ContainsKey(talentTabId) || !TalentTabIds[talentTabId].ToLower().StartsWith(spec))
                        continue;
                }

                if (_currentWeightSet == null || _currentWeightSet.Name != set.Name)
                {
                    Log("Setting weight set to: " + set.Name);
                    foreach (var kvp in set.Weights)
                    {
                        Log("- " + kvp.Key + " = " + kvp.Value);
                    }
                }
                _currentWeightSet = set;
                break;
            }
        }

        #endregion

        #region item score

        // calculates an item score
        private float CalcScore(WoWItem item)
        {
            float score;
            if (item.ItemInfo.BagSlots > 0)
            {
                score = item.ItemInfo.BagSlots;
            }
            else
            {
                score = _currentWeightSet.EvaluateItem(item);
            }
            return score;
        }

        #endregion

        #region TalentTabInfo

        private static readonly Dictionary<int, string> TalentTabIds =
            new Dictionary<int, string>
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

        #endregion

        #region pulse

        private bool _init;

        public override void Pulse()
        {
            // don't run at all if there's a problem
            if (StyxWoW.Me == null || !StyxWoW.IsInGame || !StyxWoW.IsInWorld)
            {
                return;
            }

            CheckSettings();

            // Check if it's time to run again
            if (_nextPulseTime > DateTime.Now)
            {
                return;
            }

            // if we don't have any weight sets, assume we need to download them (or the update button was pressed)
            if (_availableWeightSets.Count <= 0 || _doUpdateWeights)
            {
                _doUpdateWeights = false;
                UpdateWeightSets();
            }

            if (!_init)
            {
                CheckWeightSet();
                _init = true;
            }

            // if we still don't have any, must have been a problem downloading them
            if (_availableWeightSets.Count <= 0)
            {
                Log("Unable to download weight sets, trying again in 1 minute");
                _nextPulseTime = DateTime.Now + TimeSpan.FromMinutes(1);
                return;
            }

            // if the weight set is null, something must have broken somewhere
            if (_currentWeightSet == null)
            {
                Log("Unable to determine weight set!");
                _availableWeightSets.Clear();
                _init = false;
                _nextPulseTime = DateTime.Now + TimeSpan.FromMinutes(5);
                return;
            }

            // set the time to next run the pulse to 30 seconds
            _nextPulseTime = DateTime.Now + TimeSpan.FromSeconds(30);
            // check each possible inventory slot
            foreach (WoWInventorySlot slot in Enum.GetValues(typeof(WoWInventorySlot)))
            {
                // dont equip items in combat (except for mh/oh)
                if (slot != WoWInventorySlot.MainHand && slot != WoWInventorySlot.OffHand && StyxWoW.Me.Combat)
                {
                    continue;
                }

                // ignore blacklisted slots
                if (_theSettings.BlacklistedInventorySlots.Contains(slot))
                {
                    continue;
                }

                // if the slot is an offhand
                if (slot == WoWInventorySlot.OffHand)
                {
                    WoWItem mh = StyxWoW.Me.Inventory.Equipped.GetEquippedItem(WoWInventorySlot.MainHand);
                    if (mh != null)
                    {
                        // if our mh weapon is a two hander, ignore the offhand slot
                        if (mh.ItemInfo.InventoryType == InventoryType.TwoHandWeapon)
                        {
                            //Log("Skipping offhand slot, mainhand is TwoHandWeapon");
                            continue;
                        }
                    }
                    // if we don't know dual weilding or shield, skip of the offhand slot
                    if (!SpellManager.RawSpells.Contains(WoWSpell.FromId(674)) || SpellManager.RawSpells.Contains(WoWSpell.FromId(9116)))
                    {
                        //Log("Skipping offhand slot, don't know Dual Weild or Shield");
                        continue;
                    }
                }

                // hb bug ?
                var targetslot = (InventorySlot)(slot + 1);
                // get the equipped item in the given slot
                WoWItem invitem = StyxWoW.Me.Inventory.Equipped.GetEquippedItem(slot);

                float currentscore = float.MinValue;
                IOrderedEnumerable<WoWItem> foundbagitems;

                if (invitem == null)
                {
                    // if we have no item in that slot
                    // find the best bag item that we can equip and fits in the slot
                    // have to do this seperately because comparing inventory slots seems currently broken
                    foundbagitems = from i in StyxWoW.Me.BagItems
                                    where _theSettings.OnlyEquipArmourType >= 0 && (int)i.ItemInfo.ArmorClass == _theSettings.OnlyEquipArmourType
                                    && InventoryManager.GetInventorySlotsByEquipSlot(i.ItemInfo.InventoryType).Contains(targetslot)
                                    && !_theSettings.BlacklistedEquipQualities.Contains(i.Quality)
                                    orderby CalcScore(i) descending
                                    select i;
                }
                else
                {
                    // if we're ignoring heirlooms and the item is a heirloom
                    if (_theSettings.IgnoreHeirlooms && invitem.Quality == WoWItemQuality.Heirloom)
                    {
                        continue;
                    }
                    // otherwise check the score of the equipped item
                    currentscore = CalcScore(invitem);
                    // find the best bag item that's better than it
                    foundbagitems = from i in StyxWoW.Me.BagItems
                                    where _theSettings.OnlyEquipArmourType >= 0 && (int)i.ItemInfo.ArmorClass == _theSettings.OnlyEquipArmourType
                                    && i.ItemInfo.InventoryType == invitem.ItemInfo.InventoryType
                                    && CalcScore(i) > currentscore
                                    && !_theSettings.BlacklistedEquipQualities.Contains(i.Quality)
                                    orderby CalcScore(i) descending
                                    select i;
                }

                // todo: better checking for Me.CanEquipItem
                var bagitem = foundbagitems.Where(item => StyxWoW.Me.CanEquipItem(item)).FirstOrDefault();

                // if we found an item that scores better that we can equip
                if (bagitem == null) continue;
                // print the shit
                if (invitem == null)
                {
                    Log("Slot: " + slot + " is empty, equpping: " + bagitem.Name + " (slot:" + targetslot + " bag_i:" + (bagitem.BagIndex + 1) + " bag_s:" + (bagitem.BagSlot + 1) + ")");
                }
                else
                {
                    // don't try equip a bag inside another bag
                    if ((int)slot - 19 == bagitem.BagIndex)
                    {
                        Log("Moving bag: " + bagitem.Name + " into backpack before equip");
                        Lua.DoString(@"local slot = 1; for checkslot=1,16 do if GetContainerItemID(0, checkslot) == nil then slot = checkslot; break; end; end; ClearCursor(); PickupContainerItem({0}, {1}); PickupContainerItem(0, slot); ", bagitem.BagIndex + 1, bagitem.BagSlot + 1);
                        return;
                    }
                    Log("Equipping: '" + bagitem.Name + "' " + bagitem.ItemInfo.InventoryType + " over '" + invitem.Name + "' " + invitem.ItemInfo.InventoryType + " (slot:" + targetslot + " score:" + CalcScore(bagitem) + ">" + currentscore + " bag_i:" + (bagitem.BagIndex + 1) + " bag_s:" + (bagitem.BagSlot + 1) + ")");
                }
                // equip it
                Lua.DoString("ClearCursor(); PickupContainerItem({0}, {1}); EquipCursorItem({2}); if StaticPopup1Button1 and StaticPopup1Button1:IsVisible() then StaticPopup1Button1:Click(); end; ", bagitem.BagIndex + 1, bagitem.BagSlot + 1, (int)targetslot);

                // if we equipped a ring, trinket or a bag, don't equip any more until next run (stops trying to equip things that have already been equipped to other slot)
                if (bagitem.ItemInfo.InventoryType == InventoryType.Bag || bagitem.ItemInfo.InventoryType == InventoryType.Finger || bagitem.ItemInfo.InventoryType == InventoryType.Trinket)
                {
                    return;
                }
            }
        }

        #endregion

        #region settings

        private class EquipMeSettings : Settings
        {

            public EquipMeSettings()
                : base(Logging.ApplicationPath + "\\Settings\\EquipMe_" + StyxWoW.Me.Name + ".xml")
            {
                Load();
            }

            ~EquipMeSettings()
            {
                Save();
            }

            // bag 1 through 4 as default blacklist
            [Setting, DefaultValue("19,20,21,22")]
            private string SettingBlacklistedSlots { get; set; }

            // epic and rare as default blacklist
            [Setting, DefaultValue("3,4")]
            private string SettingBlacklistedEquipQualities { get; set; }

            [Setting, DefaultValue(false)]
            public bool UseCachedWeights { get; set; }

            [Setting, DefaultValue(true)]
            public bool IgnoreHeirlooms { get; set; }

            [Setting, DefaultValue(-1)]
            public int OnlyEquipArmourType { get; set; }

            public List<WoWInventorySlot> BlacklistedInventorySlots
            {
                get
                {
                    var ret = new List<WoWInventorySlot>();
                    if (SettingBlacklistedSlots.Length <= 0)
                    {
                        return ret;
                    }
                    foreach (var s in SettingBlacklistedSlots.Split(','))
                    {
                        try
                        {
                            ret.Add((WoWInventorySlot)int.Parse(s));
                        }
                        catch (Exception) { }
                    }
                    return ret;
                }
                set
                {
                    if (value.Count <= 0)
                    {
                        SettingBlacklistedSlots = "";
                    }
                    else
                    {
                        string s = value.Aggregate("", (current, v) => current + ((int)v + ","));
                        SettingBlacklistedSlots = s.Substring(0, s.Length - 1);
                    }
                }
            }

            public List<WoWItemQuality> BlacklistedEquipQualities
            {
                get
                {
                    var ret = new List<WoWItemQuality>();
                    if (SettingBlacklistedEquipQualities.Length <= 0)
                    {
                        return ret;
                    }
                    foreach (var s in SettingBlacklistedEquipQualities.Split(','))
                    {
                        try
                        {
                            ret.Add((WoWItemQuality)int.Parse(s));
                        }
                        catch (Exception) { }
                    }
                    return ret;
                }
                set
                {
                    if (value.Count <= 0)
                    {
                        SettingBlacklistedEquipQualities = "";
                    }
                    else
                    {
                        var s = value.Aggregate("", (current, v) => current + ((int)v + ","));
                        SettingBlacklistedEquipQualities = s.Substring(0, s.Length - 1);
                    }
                }
            }

        }

        #endregion

        #region settings form

        public Form CreateSettingsForm()
        {
            var heirlooms = new CheckBox
                                {
                                    Text = "Ignore Heirlooms",
                                    Checked = _theSettings.IgnoreHeirlooms,
                                    AutoSize = true
                                };
            heirlooms.CheckedChanged += HeirloomsCheckedChanged;

            var labelbadslots = new Label { Text = "Ignore inventory slots:", AutoSize = true };

            var badslots = new CheckedListBox { Text = "Inventory slot blacklist" };
            foreach (var index in from WoWInventorySlot slot in Enum.GetValues(typeof(WoWInventorySlot))
                                  let index = badslots.Items.Add(slot)
                                  where _theSettings.BlacklistedInventorySlots.Contains(slot)
                                  select index)
            {
                badslots.SetItemChecked(index, true);
            }
            badslots.ItemCheck += CheckedlistboxItemCheck;

            var labelbadquality = new Label { Text = "Don't equip:", AutoSize = true };

            var badquality = new CheckedListBox { Text = "Item quality blacklist" };

            foreach (var index in from WoWItemQuality quality in Enum.GetValues(typeof(WoWItemQuality))
                                  let index = badquality.Items.Add(quality)
                                  where _theSettings.BlacklistedEquipQualities.Contains(quality)
                                  select index)
            {
                badquality.SetItemChecked(index, true);
            }
            badquality.ItemCheck += CheckedlistboxItemCheck;

            var updateweightsets = new Button { Text = "Update Weight Sets", AutoSize = true };
            updateweightsets.Click += UpdateweightsetsClick;

            var cachedweights = new CheckBox
                                    {
                                        AutoSize = true,
                                        Text = "Use cached weights",
                                        Checked = _theSettings.UseCachedWeights
                                    };
            cachedweights.Click += CachedweightsClick;

            var labelonlyequiparmourtype = new Label { Text = "Only equip armour type", AutoSize = true };
            var onlyequiparmourtype = new ComboBox();
            foreach (var index in from WoWItemArmorClass quality in Enum.GetValues(typeof(WoWItemArmorClass))
                                  let index = onlyequiparmourtype.Items.Add(quality)
                                  where _theSettings.OnlyEquipArmourType == (int)quality
                                  select index)
            {
                onlyequiparmourtype.SelectedIndex = index;
            }
            onlyequiparmourtype.SelectedValueChanged += onlyequiparmourtype_SelectedValueChanged;

            var panel = new FlowLayoutPanel
                            {
                                FlowDirection = FlowDirection.TopDown,
                                AutoSize = true,
                                Dock = DockStyle.Fill
                            };
            panel.Controls.Add(heirlooms);
            panel.Controls.Add(labelbadslots);
            panel.Controls.Add(badslots);
            panel.Controls.Add(labelbadquality);
            panel.Controls.Add(badquality);
            panel.Controls.Add(cachedweights);
            panel.Controls.Add(updateweightsets);
            panel.Controls.Add(labelonlyequiparmourtype);
            panel.Controls.Add(onlyequiparmourtype);

            var form = new Form();
            form.FormClosing += FormFormClosing;
            form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            form.AutoSize = true;
            form.Text = "EquipMe Settings";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.StartPosition = FormStartPosition.CenterParent;
            form.Controls.Add(panel);
            return form;
        }

        void onlyequiparmourtype_SelectedValueChanged(object sender, EventArgs e)
        {
            _theSettings.OnlyEquipArmourType = (int)((ComboBox)sender).SelectedValue;
        }

        void CachedweightsClick(object sender, EventArgs e)
        {
            _theSettings.UseCachedWeights = ((CheckBox)sender).Checked;
        }

        void FormFormClosing(object sender, FormClosingEventArgs e)
        {
            _theSettings.Save();
        }

        void UpdateweightsetsClick(object sender, EventArgs e)
        {
            _nextPulseTime = DateTime.Now - TimeSpan.FromSeconds(1);
            _doUpdateWeights = true;
            if (!TreeRoot.IsRunning)
            {
                ObjectManager.Update();
                Pulse();
            }
        }

        void CheckedlistboxItemCheck(object sender, ItemCheckEventArgs e)
        {
            var box = (CheckedListBox)sender;
            try
            {
                if (box.Items.Contains(WoWItemQuality.Uncommon))
                {
                    var list = _theSettings.BlacklistedEquipQualities;
                    if (e.NewValue == CheckState.Checked)
                    {
                        list.Add((WoWItemQuality)box.Items[e.Index]);
                    }
                    else
                    {
                        list.Remove((WoWItemQuality)box.Items[e.Index]);
                    }
                    _theSettings.BlacklistedEquipQualities = list;
                }
                else
                {
                    var list = _theSettings.BlacklistedInventorySlots;
                    if (e.NewValue == CheckState.Checked)
                    {
                        list.Add((WoWInventorySlot)box.Items[e.Index]);
                    }
                    else
                    {
                        list.Remove((WoWInventorySlot)box.Items[e.Index]);
                    }
                    _theSettings.BlacklistedInventorySlots = list;
                }
            }
            catch (Exception) { }
        }

        void HeirloomsCheckedChanged(object sender, EventArgs e)
        {
            _theSettings.IgnoreHeirlooms = ((CheckBox)sender).Checked;
        }

        #endregion

    }
}