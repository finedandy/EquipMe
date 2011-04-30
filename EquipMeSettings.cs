using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Styx;
using Styx.Helpers;
using Styx.Logic.Inventory;
using DefaultValue = Styx.Helpers.DefaultValueAttribute;

namespace EquipMe
{
    public sealed class EquipMeSettings : Settings
    {

        #region save/load

        public enum SettingsType
        {
            Settings,
            Weights
        }

        /// <summary>
        /// Returns a settings path updated with current deets
        /// </summary>
        /// <param name="weights">if it's a weights file or otherwise just a settings file</param>
        /// <returns>path to settings</returns>
        public string GetSettingsPath(SettingsType type)
        {
            return Logging.ApplicationPath + "\\Settings\\EquipMe\\EquipMe_" + StyxWoW.Me.Name + "_" + EquipMe.GetSpecName() + (UsePVP && Styx.Logic.Battlegrounds.IsInsideBattleground ? "_PVP" : "") + "_" + type.ToString() + ".xml";
        }

        /// <summary>
        /// Constructor, load the settings
        /// </summary>
        public EquipMeSettings()
            : base(Logging.ApplicationPath + Path.PathSeparator) // take that you shitty default constructor
        {
            LoadSettings();
        }

        /// <summary>
        /// (re)loads settings
        /// </summary>
        public void LoadSettings()
        {
            _currentSpec = null;
            BlacklistedInventoryItems.Clear();
            NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
            //
            try
            {
                base.LoadFromXML(XElement.Load(GetSettingsPath(SettingsType.Settings)));
            }
            catch (Exception) { }
            var _path = GetSettingsPath(SettingsType.Weights);
            EquipMe.Log("Loading weights from: {0}", _path);
            var newset = EquipMe.LoadWeightSetFromXML(_path);
            if (newset != null)
            {
                WeightSet_Current = newset;
            }
            else
            {
                SaveSettings();
            }
        }

        /// <summary>
        /// saves settings
        /// </summary>
        public void SaveSettings()
        {
            _currentSpec = null;
            BlacklistedInventoryItems.Clear();
            NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
            //
            base.SaveToFile(GetSettingsPath(SettingsType.Settings));
            var _path = GetSettingsPath(SettingsType.Weights);
            XElement saveElm = File.Exists(_path) ? XElement.Load(_path) : new XElement("WeightSet");
            saveElm.SetAttributeValue("Name", WeightSet_Current.Name);
            foreach (KeyValuePair<Stat, float> setval in WeightSet_Current.Weights)
            {
                saveElm.SetElementValue(setval.Key.ToString(), setval.Value);
            }
            saveElm.Save(_path);
        }

        #endregion

        #region singleton

        public static EquipMeSettings Instance
        {
            get
            {
                return Nested.instance;
            }
        }

        class Nested
        {
            static Nested()
            {
            }
            internal static readonly EquipMeSettings instance = new EquipMeSettings();
        }

        #endregion

        #region settings

        /// <summary>
        /// A list of items that have been checked and deemed "not equippable"
        /// Cleared when you level up or change spec
        /// </summary>
        public List<ulong> BlacklistedInventoryItems = new List<ulong>();

        /// <summary>
        /// Determines a point of time in the future when the next Pulse() method should run
        /// </summary>
        public DateTime NextPulse = DateTime.Now;

        #region weightsets

        /// <summary>
        /// A blank weight set, name = "blank" and all stats = 0
        /// </summary>
        public static WeightSet WeightSet_Blank = new WeightSet("blank", Enum.GetNames(typeof(Stat)).Where(name => name.ToLower() != "none").ToDictionary(o => (Stat)Enum.Parse(typeof(Stat), o), k => 0f));

        /// <summary>
        /// A lowbie weight set, name = "lowbie" and all stats 1
        /// </summary>
        public static WeightSet WeightSet_Lowbie = new WeightSet("lowbie", Enum.GetNames(typeof(Stat)).Where(name => name.ToLower() != "none").ToDictionary(o => (Stat)Enum.Parse(typeof(Stat), o), k => 1f));

        /// <summary>
        /// The current weight set, by default set to blank
        /// </summary>
        public WeightSet WeightSet_Current = WeightSet_Blank;

        #endregion

        #region category: general

        [Setting]
        [DefaultValue("")]
        [Category("General")]
        [DisplayName("Blacklisted Slots")]
        [Description("A list of comma seperated slots to ignore. Available values:\n " +
            "Head = 1 \n" +
            "Neck = 2 \n" +
            "Shoulder = 3 \n" +
            "Body = 4 \n" +
            "Chest = 5 \n" +
            "Waist = 6 \n" +
            "Legs = 7 \n" +
            "Feet = 8 \n" +
            "Wrist = 9 \n" +
            "Hand = 10 \n" +
            "Finger = 11 \n" +
            "Trinket = 12 \n" +
            "Weapon = 13 \n" +
            "Shield = 14 \n" +
            "Ranged = 15 \n" +
            "Cloak = 16 \n" +
            "TwoHandWeapon = 17 \n" +
            "Bag = 18 \n" +
            "Tabard = 19 \n" +
            "Robe = 20 \n" +
            "WeaponMainHand = 21 \n" +
            "WeaponOffHand = 22 \n" +
            "Holdable = 23 \n" +
            "Ammo = 24 \n" +
            "Thrown = 25 \n" +
            "RangedRight = 26 \n" +
            "Quiver = 27 \n" +
            "Relic = 28")]
        public string BlacklistedSlots { get; set; }

        [Setting]
        [DefaultValue(true)]
        [Category("General")]
        [DisplayName("Ignore BOE Epic")]
        [Description("Don't try to equip epic (purple) bind on equip items that aren't already soulbound.")]
        public bool IngoreEpicBOE { get; set; }

        [Setting]
        [DefaultValue(false)]
        [Category("General")]
        [DisplayName("Ignore BOE Rare")]
        [Description("Don't try to equip rare (blue) bind on equip items that aren't already soulbound.")]
        public bool IgnoreRareBOE { get; set; }

        [Setting]
        [DefaultValue(true)]
        [Category("General")]
        [DisplayName("Ignore Heirlooms")]
        [Description("Don't try to equip items over already equipped heirlooms.")]
        public bool IgnoreHeirlooms { get; set; }

        [Setting]
        [DefaultValue(30)]
        [Category("General")]
        [DisplayName("Pulse Frequency")]
        [Description("How many seconds between each pulse, just leave this at 30 if you don't know what it is.")]
        public int PulseFrequency { get; set; }

        [Category("General")]
        [DisplayName("Weightset Name")]
        [Description("Your current weightset name. If this name is blank (which it is by default) the plugin will attempt to update weights from wowhead on first run.")]
        public string WeightSetName
        {
            get
            {
                return WeightSet_Current.Name;
            }
            set
            {
                WeightSet_Current = new WeightSet(value, WeightSet_Current.Weights);
                BlacklistedInventoryItems.Clear();
                NextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
            }
        }

        [Setting]
        [Category("General")]
        [DisplayName("Use PVP")]
        [Description("Use different settings for PVP. This will switch to a PVP weightset and settings file when inside a battleground.")]
        [DefaultValue(false)]
        public bool UsePVP { get; set; }

        #endregion

        #region category: loot rolling

        [Setting]
        [Category("Loot Rolling")]
        [DisplayName("Roll On Loot")]
        [Description("Rolls for loot taking into account the current weight set.")]
        [DefaultValue(false)]
        public bool RollOnLoot { get; set; }

        [Setting]
        [Category("Loot Rolling")]
        [DisplayName("Need Epic BOE")]
        [Description("Rolls need on epic BOE items.")]
        [DefaultValue(false)]
        public bool RollEpicBOENeed { get; set; }

        [Setting]
        [Category("Loot Rolling")]
        [DisplayName("Need List")]
        [Description("A comma seperated list of item names (case insensitive), regular expressions or ids to roll need on.")]
        [DefaultValue("")]
        public string RollNeedList { get; set; }

        [Setting]
        [DefaultValue(false)]
        [Category("Loot Rolling")]
        [DisplayName("Ignore Level")]
        [Description("Ignores the level requirement when taking into account if an item is equippable.")]
        public bool RollIgnoreLevel { get; set; }

        [Setting]
        [DefaultValue(2)]
        [Category("Loot Rolling")]
        [DisplayName("Level Difference")]
        [Description("Only rolls on items where the required level is less than or equal this number higher than our level.")]
        public int RollIgnoreLevelDiff { get; set; }

        #endregion

        #region category: character

        // stops lua spam in propertygrid
        private string _currentSpec = null;

        [Category("Character")]
        [DisplayName("Current Spec")]
        [Description("Your current spec.")]
        public string CharSpec
        {
            get
            {
                return _currentSpec ?? (_currentSpec = EquipMe.GetSpecName());
            }
        }

        [Category("Character")]
        [DisplayName("Current Class")]
        [Description("Your current class.")]
        public string CharClass
        {
            get
            {
                return StyxWoW.Me.Class.ToString();
            }
        }

        [Setting]
        [DefaultValue(WoWItemArmorClass.None)]
        [Category("Character")]
        [DisplayName("Only Equip Armour")]
        [Description("Will only try to equip armour of this type (None = equips all item types available for your class).")]
        public WoWItemArmorClass OnlyEquipArmourType { get; set; }

        [Setting]
        [DefaultValue("")]
        [Category("Character")]
        [DisplayName("Main Hand Type")]
        [Description("Will only try to equip weapons of this type into the mainhand slot (none = equips all weapon types available for your class). Available values: \n" +
            "None = -1 \n" +
            "Axe = 0 \n" +
            "AxeTwoHand = 1 \n" +
            "Bow = 2 \n" +
            "Gun = 3 \n" +
            "Mace = 4 \n" +
            "MaceTwoHand = 5 \n" +
            "Polearm = 6 \n" +
            "Sword = 7 \n" +
            "SwordTwoHand = 8 \n" +
            "Staff = 10 \n" +
            "Exotic = 11 \n" +
            "ExoticTwoHand = 12 \n" +
            "Fist = 13 \n" +
            "Misc = 14 \n" +
            "Dagger = 15 \n" +
            "Thrown = 16 \n" +
            "Spear = 17 \n" +
            "Crossbow = 18 \n" +
            "Wand = 19 \n" +
            "FishingPole = 20")]
        public string WeaponMainHand { get; set; }

        [Setting]
        [DefaultValue("")]
        [Category("Character")]
        [DisplayName("Off Hand Type")]
        [Description("Will only try to equip weapons of this type into the offhand slot (none = equips all weapon types available for your class). Available values: \n" +
            "None = -1 \n" +
            "Axe = 0 \n" +
            "AxeTwoHand = 1 \n" +
            "Bow = 2 \n" +
            "Gun = 3 \n" +
            "Mace = 4 \n" +
            "MaceTwoHand = 5 \n" +
            "Polearm = 6 \n" +
            "Sword = 7 \n" +
            "SwordTwoHand = 8 \n" +
            "Staff = 10 \n" +
            "Exotic = 11 \n" +
            "ExoticTwoHand = 12 \n" +
            "Fist = 13 \n" +
            "Misc = 14 \n" +
            "Dagger = 15 \n" +
            "Thrown = 16 \n" +
            "Spear = 17 \n" +
            "Crossbow = 18 \n" +
            "Wand = 19 \n" +
            "FishingPole = 20")]
        public string WeaponOffHand { get; set; }

        [Setting]
        [DefaultValue("")]
        [Category("Character")]
        [DisplayName("Ranged Type")]
        [Description("Will only try to equip weapons of this type into the ranged slot (none = equips all weapon types available for your class). Available values: \n" +
            "None = -1 \n" +
            "Axe = 0 \n" +
            "AxeTwoHand = 1 \n" +
            "Bow = 2 \n" +
            "Gun = 3 \n" +
            "Mace = 4 \n" +
            "MaceTwoHand = 5 \n" +
            "Polearm = 6 \n" +
            "Sword = 7 \n" +
            "SwordTwoHand = 8 \n" +
            "Staff = 10 \n" +
            "Exotic = 11 \n" +
            "ExoticTwoHand = 12 \n" +
            "Fist = 13 \n" +
            "Misc = 14 \n" +
            "Dagger = 15 \n" +
            "Thrown = 16 \n" +
            "Spear = 17 \n" +
            "Crossbow = 18 \n" +
            "Wand = 19 \n" +
            "FishingPole = 20")]
        public string WeaponRanged { get; set; }

        #endregion

        #endregion

    }
}