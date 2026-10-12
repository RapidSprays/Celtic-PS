using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace CHPrivateMods
{
    /// <summary>
    /// Our own items (server/content/custom-items.json, written next to this DLL as CHPrivateMods.items.txt by
    /// CH.Patcher). Each is a copy of an existing client ItemTemplate under a new id and name, added to the client's
    /// template dictionary when CItemManager.Init runs (patch P11), so the client can show, equip and draw it.
    /// </summary>
    public static class CustomItems
    {
        internal const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal sealed class Entry
        {
            public int Id;
            public int Base;
            public string Effect;
            public string Name;
            public string Description;
        }

        private static List<Entry> _entries;
        private static readonly HashSet<int> Rainbow = new HashSet<int>();

        internal static bool IsRainbow(int templateId)
        {
            Load();
            return Rainbow.Contains(templateId);
        }

        /// <summary>Called at the start of CItemManager.Init(BalanceClient) (patch P11).</summary>
        public static void Register(object balanceClient)
        {
            try
            {
                Load();
                if (balanceClient == null || _entries.Count == 0) return;
                var templates = balanceClient.GetType().GetField("ItemTemplates", Any)?.GetValue(balanceClient) as IDictionary;
                if (templates == null) return;
                var clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (var e in _entries)
                {
                    if (templates.Contains(e.Id) || !templates.Contains(e.Base)) continue;
                    var source = templates[e.Base];
                    var copy = clone.Invoke(source, null);
                    var type = copy.GetType();
                    type.GetField("ID", Any)?.SetValue(copy, e.Id);
                    type.GetField("Name", Any)?.SetValue(copy, e.Name);
                    if (!string.IsNullOrEmpty(e.Description)) type.GetField("Description", Any)?.SetValue(copy, e.Description);
                    templates[e.Id] = copy;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CHPrivateMods] custom items not added: " + ex);
            }
        }

        /// <summary>Lines of <c>id|base|effect|name|description</c>.</summary>
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
                    var p = line.Split('|');
                    if (p.Length < 4 || !int.TryParse(p[0], out var id) || !int.TryParse(p[1], out var baseId)) continue;
                    var entry = new Entry { Id = id, Base = baseId, Effect = p[2], Name = p[3], Description = p.Length > 4 ? p[4] : "" };
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
