//!CompilerOption:AddRef:System.Design.dll

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Forms;
using Styx.Helpers;
using Styx.Logic.Inventory;

namespace EquipMe
{
    public partial class EquipMeGui : Form
    {
        public EquipMeGui()
        {
            InitializeComponent();
        }

        private void EquipMeGui_Load(object sender, EventArgs e)
        {
            UpdatePropertyGrids();
        }

        private void EquipMeGui_FormClosing(object sender, FormClosingEventArgs e)
        {
            EquipMeSettings.Instance.SaveSettings();
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            UpdatePropertyGrids();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            EquipMe.Log("Updating weights from wowhead");
            EquipMe.UpdateWowhead();
            UpdatePropertyGrids();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            EquipMe.Log("Reloading settings from file");
            EquipMeSettings.Instance.LoadSettings();
            UpdatePropertyGrids();
        }

        private void button3_Click(object sender, EventArgs e)
        {
            try
            {
                var ofd = new OpenFileDialog();
                ofd.InitialDirectory = Logging.ApplicationPath;
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    EquipMe.Log("Loading new weightset data from: {0}", ofd.FileName);
                    var loadedset = EquipMe.LoadWeightSetFromXML(ofd.FileName);
                    if (loadedset == null)
                    {
                        return;
                    }
                    EquipMeSettings.Instance.WeightSet_Current = loadedset;
                }
            }
            catch (Exception ex)
            {
                EquipMe.Log("Error loading weighset, exception\n{0}", ex);
            }
        }

        void UpdatePropertyGrids()
        {
            propertyGrid1.SelectedObject = EquipMeSettings.Instance;
            propertyGrid2.SelectedObject = new DictionaryPropertyGridAdapter<Stat, float>(EquipMeSettings.Instance.WeightSet_Current.Weights, checkBox1.Checked);
        }

        #region DictionaryPropertyGridAdapter

        // used to display weightset dictionary in propertygrid, don't touch!

        class DictionaryPropertyGridAdapter<TKey, TValue> : ICustomTypeDescriptor
        {
            IDictionary<TKey, TValue> _dictionary;
            bool _show0;

            public DictionaryPropertyGridAdapter(IDictionary<TKey, TValue> d, bool b)
            {
                _dictionary = d;
                _show0 = b;
            }

            public string GetComponentName()
            {
                return TypeDescriptor.GetComponentName(this, true);
            }

            public EventDescriptor GetDefaultEvent()
            {
                return TypeDescriptor.GetDefaultEvent(this, true);
            }

            public string GetClassName()
            {
                return TypeDescriptor.GetClassName(this, true);
            }

            public EventDescriptorCollection GetEvents(Attribute[] attributes)
            {
                return TypeDescriptor.GetEvents(this, attributes, true);
            }

            EventDescriptorCollection System.ComponentModel.ICustomTypeDescriptor.GetEvents()
            {
                return TypeDescriptor.GetEvents(this, true);
            }

            public TypeConverter GetConverter()
            {
                return TypeDescriptor.GetConverter(this, true);
            }

            public object GetPropertyOwner(PropertyDescriptor pd)
            {
                return _dictionary;
            }

            public AttributeCollection GetAttributes()
            {
                return TypeDescriptor.GetAttributes(this, true);
            }

            public object GetEditor(Type editorBaseType)
            {
                return TypeDescriptor.GetEditor(this, editorBaseType, true);
            }

            public PropertyDescriptor GetDefaultProperty()
            {
                return null;
            }

            PropertyDescriptorCollection
                System.ComponentModel.ICustomTypeDescriptor.GetProperties()
            {
                return ((ICustomTypeDescriptor)this).GetProperties(new Attribute[0]);
            }

            public PropertyDescriptorCollection GetProperties(Attribute[] attributes)
            {
                ArrayList properties = new ArrayList();
                foreach (KeyValuePair<TKey, TValue> e in _dictionary)
                {
                    if (!_show0 && EquipMe.ToFloat(_dictionary[e.Key].ToString()) == 0)
                    {
                        continue;
                    }
                    properties.Add(new DictionaryPropertyDescriptor<TKey, TValue>(_dictionary, e.Key));
                }

                PropertyDescriptor[] props =
                    (PropertyDescriptor[])properties.ToArray(typeof(PropertyDescriptor));

                return new PropertyDescriptorCollection(props);
            }
        }

        class DictionaryPropertyDescriptor<TKey, TValue> : PropertyDescriptor
        {
            IDictionary<TKey, TValue> _dictionary;
            TKey _key;

            public override string Category
            {
                get
                {
                    return "Weightset";
                }
            }

            internal DictionaryPropertyDescriptor(IDictionary<TKey, TValue> d, TKey key)
                : base(key.ToString(), null)
            {
                _dictionary = d;
                _key = key;
            }

            public override Type PropertyType
            {
                get { return _dictionary[_key].GetType(); }
            }

            public override void SetValue(object component, object value)
            {
                _dictionary[_key] = (TValue)value;
            }

            public override object GetValue(object component)
            {
                return _dictionary[_key];
            }

            public override bool IsReadOnly
            {
                get { return false; }
            }

            public override Type ComponentType
            {
                get { return null; }
            }

            public override bool CanResetValue(object component)
            {
                return false;
            }

            public override void ResetValue(object component)
            {
            }

            public override bool ShouldSerializeValue(object component)
            {
                return false;
            }
        }

        #endregion

    }
}
