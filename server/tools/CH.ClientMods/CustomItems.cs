using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace CHPrivateMods
{
    /// <summary>
    /// Our own items (server/content/custom-items.json, written next to this DLL as CHPrivateMods.items.txt by
    /// CH.Patcher). Each is a copy of an existing client ItemTemplate under a new id and name, optionally with another
    /// item's stats (<c>stats</c>) and its own weapon model: a copy of a model plus fashion particle effects.
    /// Models are added when CModelIDTemplateManager.Init runs (patch P13), items when CItemManager.Init runs (P11).
    /// </summary>
    public static class CustomItems
    {
        internal const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Combat stats and requirements copied from the <c>stats</c> item.</summary>
        private static readonly string[] StatFields =
        {
            "DamageTypes", "BonusTypes", "RequirementTypes", "ClassTypes", "PrimaryStatBonuses", "AbilityBonuses", "AvoidanceBonuses",
            "SkillBonuses", "Speed", "Range", "Armour", "Weight", "ProcSkillID", "ProcSkillLevel", "ProcSkillChance", "EquipSkillID", "StarLevel",
        };

        internal sealed class Particle
        {
            public int Effect;
            public string Bone;
            public Vector3 Offset;
            public float Scale;
        }

        internal sealed class Entry
        {
            public int Id;
            public int Base;
            public string Effect;
            public string Name;
            public string Description;
            public int Stats = -1;
            public int ModelBase = -1;
            public int ModelId = -1;
            public List<Particle> Particles = new List<Particle>();

            /// <summary>Custom gear (CHPrivateMods.gear.txt): template fields to set, "Field=value" or "Field=key:amount,key:amount".</summary>
            public List<KeyValuePair<string, string>> Overrides = new List<KeyValuePair<string, string>>();
        }

        /// <summary>The member each bonus list's entries are keyed by (the rest is "Amount").</summary>
        private static readonly Dictionary<string, string> ListKeys = new Dictionary<string, string>
        {
            { "BonusTypes", "BonusType" }, { "DamageTypes", "DamageType" }, { "PrimaryStatBonuses", "PrimaryStatType" },
            { "AbilityBonuses", "AbilityType" }, { "AvoidanceBonuses", "AbilityType" }, { "SkillBonuses", "SkillType" },
            { "RequirementTypes", "RequirementType" },
        };

        private static List<Entry> _entries;
        private static readonly HashSet<int> Rainbow = new HashSet<int>();
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool IsRainbow(int templateId)
        {
            Load();
            return Rainbow.Contains(templateId);
        }

        /// <summary>Called at the start of CItemManager.Init(BalanceClient) (patch P11). Also extends the skill rank tables.</summary>
        public static void Register(object balanceClient)
        {
            SkillRanks.Extend(balanceClient);
            CustomContent.Register(balanceClient); // the owner's monsters and quests (CHPrivateMods.extras.txt)
            try
            {
                Load();
                if (balanceClient == null || _entries.Count == 0) return;
                var templates = R.Get(balanceClient, "ItemTemplates") as IDictionary;
                if (templates == null) return;
                foreach (var e in _entries)
                {
                    if (templates.Contains(e.Id) || !templates.Contains(e.Base)) continue;
                    var copy = Clone.Invoke(templates[e.Base], null);
                    var type = copy.GetType();
                    if (e.Stats >= 0 && templates.Contains(e.Stats))
                    {
                        var donor = templates[e.Stats];
                        foreach (var name in StatFields)
                        {
                            var field = R.Field(type, name);
                            if (field != null) field.SetValue(copy, field.GetValue(donor));
                        }
                    }
                    R.Field(type, "ID")?.SetValue(copy, e.Id);
                    R.Field(type, "Name")?.SetValue(copy, e.Name);
                    if (!string.IsNullOrEmpty(e.Description)) R.Field(type, "Description")?.SetValue(copy, e.Description);
                    if (e.ModelId >= 0) R.Field(type, "ModelID")?.SetValue(copy, e.ModelId);
                    foreach (var o in e.Overrides)
                    {
                        try { SetOverride(copy, type, o.Key, o.Value); }
                        catch (Exception ex) { Debug.LogWarning("[CHPrivateMods] custom gear " + e.Id + " " + o.Key + ": " + ex.Message); }
                    }
                    templates[e.Id] = copy;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CHPrivateMods] custom items not added: " + ex);
            }
        }

        /// <summary>
        /// Called at the start of CModelIDTemplateManager.Init(BalanceClient) (patch P13): a copy of each custom item's
        /// base weapon model under its own id, with its particle effects (built like the game's fashion particles).
        /// </summary>
        public static void RegisterModels(object balanceClient)
        {
            try
            {
                Load();
                if (balanceClient == null) return;
                var bySlot = R.Get(balanceClient, "EquipmentModelIDTemplates") as IDictionary;
                if (bySlot == null) return;
                IDictionary weapons = null;
                foreach (DictionaryEntry kv in bySlot)
                    if (kv.Key.ToString() == "Weapon") weapons = kv.Value as IDictionary;
                if (weapons == null) return;
                var prototype = FindParticlePrototype(weapons);
                foreach (var e in _entries)
                {
                    if (e.ModelId < 0 || weapons.Contains(e.ModelId) || !weapons.Contains(e.ModelBase)) continue;
                    var model = Clone.Invoke(weapons[e.ModelBase], null);
                    var type = model.GetType();
                    R.Field(type, "Name")?.SetValue(model, e.Name);
                    var listField = R.Field(type, "ParticleComponents");
                    if (listField != null && prototype != null && e.Particles.Count > 0)
                    {
                        var list = (IList)Activator.CreateInstance(listField.FieldType);
                        if (listField.GetValue(model) is IList existing)
                            foreach (var p in existing) list.Add(p);
                        foreach (var p in e.Particles) list.Add(MakeParticle(prototype, p));
                        listField.SetValue(model, list);
                    }
                    weapons[e.ModelId] = model;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CHPrivateMods] custom models not added: " + ex);
            }
        }

        /// <summary>Any weapon model's particle component, to copy (its class has no parameterless constructor).</summary>
        private static object FindParticlePrototype(IDictionary weapons)
        {
            foreach (DictionaryEntry kv in weapons)
            {
                var list = R.Get(kv.Value, "ParticleComponents") as IList;
                if (list != null && list.Count > 0) return list[0];
            }
            return null;
        }

        private static object MakeParticle(object prototype, Particle p)
        {
            var copy = Clone.Invoke(prototype, null);
            var type = copy.GetType();
            void Set(string name, object value) => R.Field(type, name)?.SetValue(copy, value);
            object EnumValue(string field, string name)
            {
                var t = R.Field(type, field)?.FieldType;
                return t != null && t.IsEnum ? R.Enum(t, name) : null;
            }
            Set("ParticleEffectId", p.Effect);
            Set("Scale", p.Scale);
            Set("AttachBone1", EnumValue("AttachBone1", p.Bone));
            Set("AttachBone1Offset", p.Offset);
            Set("AttachBone2", EnumValue("AttachBone2", "None"));
            Set("AttachBone2Offset", Vector3.zero);
            Set("OrientateToBones", false);
            Set("Slot", EnumValue("Slot", "Weapon"));
            Set("Gender", EnumValue("Gender", "Gender_BOTH"));
            Set("ColorId", -1);
            return copy;
        }

        private static object Value(Type t, string text)
        {
            if (t.IsEnum) return int.TryParse(text, out var n) ? System.Enum.ToObject(t, n) : R.Enum(t, text);
            if (t == typeof(string)) return text;
            if (t == typeof(bool)) return text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
            return Convert.ChangeType(text, t, CultureInfo.InvariantCulture);
        }

        private static void SetMember(object o, string name, string text)
        {
            var t = o.GetType();
            var f = R.Field(t, name);
            if (f != null) { f.SetValue(o, Value(f.FieldType, text)); return; }
            var p = R.Property(t, name);
            if (p != null && p.CanWrite) p.SetValue(o, Value(p.PropertyType, text));
        }

        private static object NewElement(Type t)
        {
            try { return Activator.CreateInstance(t, true); }
            catch { return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t); }
        }

        /// <summary>Sets a template field: a number, or a list (bonuses, damage, requirements, classes) rebuilt from the text.</summary>
        private static void SetOverride(object item, Type type, string field, string value)
        {
            var f = R.Field(type, field);
            if (f == null) return;
            var ft = f.FieldType;
            var elementType = ft.IsArray ? ft.GetElementType() : ft.IsGenericType ? ft.GetGenericArguments()[0] : null;
            if (elementType == null || ft == typeof(string)) { f.SetValue(item, Value(ft, value)); return; }
            var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var elements = new List<object>();
            foreach (var part in parts)
            {
                if (ListKeys.TryGetValue(field, out var key))
                {
                    var kv = part.Split(':');
                    if (kv.Length != 2) continue;
                    var el = NewElement(elementType);
                    SetMember(el, key, kv[0]);
                    SetMember(el, "Amount", kv[1]);
                    elements.Add(el);
                }
                else elements.Add(Value(elementType, part)); // ClassTypes
            }
            if (ft.IsArray)
            {
                var array = Array.CreateInstance(elementType, elements.Count);
                for (var i = 0; i < elements.Count; i++) array.SetValue(elements[i], i);
                f.SetValue(item, array);
            }
            else
            {
                var list = (IList)Activator.CreateInstance(ft);
                foreach (var el in elements) list.Add(el);
                f.SetValue(item, list);
            }
        }

        /// <summary>
        /// Lines of <c>id|base|effect|name|description|stats|modelBase|modelId|particles</c> (CHPrivateMods.items.txt), then
        /// custom gear lines <c>id|look|name|description|Field=value;…</c> (CHPrivateMods.gear.txt, scripts/install-custom-gear.py).
        /// </summary>
        private static void Load()
        {
            if (_entries != null) return;
            _entries = new List<Entry>();
            try
            {
                var gearFile = Path.Combine(Path.GetDirectoryName(typeof(CustomItems).Assembly.Location) ?? ".", "CHPrivateMods.gear.txt");
                if (File.Exists(gearFile))
                    foreach (var line in File.ReadAllLines(gearFile))
                    {
                        var f = line.Split('|');
                        if (f.Length < 5 || !int.TryParse(f[0], out var id) || !int.TryParse(f[1], out var look)) continue;
                        var entry = new Entry { Id = id, Base = look, Effect = "", Name = f[2], Description = f[3] };
                        foreach (var spec in f[4].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var eq = spec.IndexOf('=');
                            if (eq > 0) entry.Overrides.Add(new KeyValuePair<string, string>(spec.Substring(0, eq), spec.Substring(eq + 1)));
                        }
                        _entries.Add(entry);
                    }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CHPrivateMods] custom gear list unreadable: " + ex);
            }
            try
            {
                var dir = Path.GetDirectoryName(typeof(CustomItems).Assembly.Location);
                var file = Path.Combine(dir ?? ".", "CHPrivateMods.items.txt");
                if (!File.Exists(file)) return;
                foreach (var line in File.ReadAllLines(file))
                {
                    var f = line.Split('|');
                    if (f.Length < 4 || !int.TryParse(f[0], out var id) || !int.TryParse(f[1], out var baseId)) continue;
                    var entry = new Entry { Id = id, Base = baseId, Effect = f[2], Name = f[3], Description = f.Length > 4 ? f[4] : "" };
                    if (f.Length > 5 && int.TryParse(f[5], out var stats)) entry.Stats = stats;
                    if (f.Length > 7 && int.TryParse(f[6], out var modelBase) && int.TryParse(f[7], out var modelId))
                    {
                        entry.ModelBase = modelBase;
                        entry.ModelId = modelId;
                    }
                    if (f.Length > 8)
                        foreach (var spec in f[8].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var p = spec.Split(':');
                            var o = p.Length > 2 ? p[2].Split(',') : new string[0];
                            if (p.Length < 4 || o.Length < 3) continue;
                            float N(string s) => float.Parse(s, CultureInfo.InvariantCulture);
                            entry.Particles.Add(new Particle { Effect = int.Parse(p[0]), Bone = p[1], Offset = new Vector3(N(o[0]), N(o[1]), N(o[2])), Scale = N(p[3]) });
                        }
                    _entries.Add(entry);
                    if (entry.Effect == "rainbow") Rainbow.Add(id);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CHPrivateMods] custom item list unreadable: " + ex);
            }
        }
    }
}
