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

        /// <summary>
        /// Returns a settings path updated with current deets
        /// </summary>
        /// <param name="weights">if it's a weights file or otherwise just a settings file</param>
        /// <returns>path to settings</returns>
        public string GetSettingsPath(bool weights)
        {
            return Logging.ApplicationPath + "\\Settings\\EquipMe\\EquipMe_" + StyxWoW.Me.Name + "_" + EquipMe.GetSpecName() + (UsePVP && Styx.Logic.Battlegrounds.IsInsideBattleground ? "_PVP" : "") + "_" + (weights ? "Weights" : "Settings") + ".xml";
        }

        /// <summary>
        /// Constructor, load the settings
        /// </summary>
        public EquipMeSettings()
            : base(Logging.ApplicationPath + "\\Settings\\") // take that you shitty default constructor
        {
            LoadSettings();
        }

        /// <summary>
        /// (re)loads settings
        /// </summary>
        public void LoadSettings()
        {
            _currentSpec = null;
            _blacklistedItems.Clear();
            _nextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
            //
            try
            {
                base.LoadFromXML(XElement.Load(GetSettingsPath(false)));
            }
            catch (Exception) { }
            EquipMe.Log("Loading weights from: {0}", GetSettingsPath(true));
            var newset = EquipMe.LoadWeightSetFromXML(GetSettingsPath(true));
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
            _blacklistedItems.Clear();
            _nextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
            //
            base.SaveToFile(GetSettingsPath(false));
            XElement saveElm = File.Exists(GetSettingsPath(true)) ? XElement.Load(GetSettingsPath(true)) : new XElement("WeightSet");
            saveElm.SetAttributeValue("Name", WeightSet_Current.Name);
            foreach (KeyValuePair<Stat, float> setval in WeightSet_Current.Weights)
            {
                saveElm.SetElementValue(setval.Key.ToString(), setval.Value);
            }
            saveElm.Save(GetSettingsPath(true));
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
        public List<ulong> _blacklistedItems = new List<ulong>();

        /// <summary>
        /// Determines a point of time in the future when the next Pulse() method should run
        /// </summary>
        public DateTime _nextPulse = DateTime.Now;

        #region weightsets

        /// <summary>
        /// used to construct lowbie weight set
        /// </summary>
        private static Dictionary<string, float> _lowbieStats = new Dictionary<string, float>() 
        {
            { Stat.Stamina.ToString(), 1f },
            { Stat.Intellect.ToString(), 1f },
            { Stat.Strength.ToString(), 1f },
            { Stat.Agility.ToString(), 1f },
            { Stat.Armor.ToString(), 1f },
        };

        /// <summary>
        /// A blank weight set, name = "blank" and all stats = 0
        /// </summary>
        public static WeightSet WeightSet_Blank = new WeightSet("blank", Enum.GetNames(typeof(Stat)).Where(name => name.ToLower() != "none").ToDictionary(o => (Stat)Enum.Parse(typeof(Stat), o), k => 0f));

        /// <summary>
        /// A lowbie weight set, name = "lowbie" and all stats 0 except as set in _lowbieStats
        /// </summary>
        public static WeightSet WeightSet_Lowbie = new WeightSet("lowbie", Enum.GetNames(typeof(Stat)).Where(name => name.ToLower() != "none").ToDictionary(o => (Stat)Enum.Parse(typeof(Stat), o), k => (_lowbieStats.ContainsKey(k) ? _lowbieStats[k] : 0f)));

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
        [DisplayName("Ingore Epic BOE")]
        [Description("Doesn't try to equip epic (purple) bind on equip items.")]
        public bool IngoreEpicBOE { get; set; }

        [Setting]
        [DefaultValue(false)]
        [Category("General")]
        [DisplayName("Ingore Rare BOE")]
        [Description("Doesn't try to equip rare (blue) bind on equip items.")]
        public bool IgnoreRareBOE { get; set; }

        [Setting]
        [DefaultValue(true)]
        [Category("General")]
        [DisplayName("Ignore Heirlooms")]
        [Description("Doesn't try to equip items over already equipped heirlooms.")]
        public bool IgnoreHeirlooms { get; set; }

        [Setting]
        [DefaultValue(WoWItemArmorClass.None)]
        [Category("General")]
        [DisplayName("Only Equip Armour")]
        [Description("Will only try to equip armour of this type.")]
        public WoWItemArmorClass OnlyEquipArmourType { get; set; }

        [Setting]
        [DefaultValue(30)]
        [Category("General")]
        [DisplayName("Pulse Frequency")]
        [Description("How many seconds between each pulse.")]
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
                _blacklistedItems.Clear();
                _nextPulse = DateTime.Now + TimeSpan.FromSeconds(1);
            }
        }

        [Setting]
        [Category("General")]
        [DisplayName("Use PVP")]
        [Description("Use different settings for PVP weights.")]
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
        [Description("A comma seperated list of item names (case insensitive) or ids to roll need on.")]
        [DefaultValue("")]
        public string RollNeedList { get; set; }

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

        #endregion

        #endregion

    }
}
