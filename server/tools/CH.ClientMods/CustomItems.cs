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
        }

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

        /// <summary>Lines of <c>id|base|effect|name|description|stats|modelBase|modelId|particles</c>.</summary>
        private static void Load()
        {
            if (_entries != null) return;
            _entries = new List<Entry>();
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
