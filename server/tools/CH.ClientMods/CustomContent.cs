using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace CHPrivateMods
{
    /// <summary>
    /// The owner's own monsters, quests and NPC lines (server/content/custom-monsters.json and custom-quests.json, written
    /// next to this DLL as CHPrivateMods.extras.txt by scripts/install-custom-gear.py), so the game can draw and describe
    /// what the server adds: Gilthral the Gladiator and Segorax's "Spar with Gilthral". Lines:
    /// <c>MOB|id|base|name</c>, <c>QUEST|id|base|from=to;…</c> and <c>CONV|file|baseQuest|quest|from=to;…</c>
    /// (and <c>TINT|id|#RRGGBB</c>, <see cref="MobTint"/>; <c>LOOK|id|petPrefab</c>, <see cref="MobPetBody"/>).
    /// Monsters and quests are copies of their base templates, added when CItemManager.Init runs (patch P11); a CONV
    /// copies every part of that conversation about the base quest (patch P17).
    /// </summary>
    public static class CustomContent
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class Swap
        {
            public int Id, Base;
            public string Name, File;
            public List<KeyValuePair<string, string>> Pairs = new List<KeyValuePair<string, string>>();

            public string Apply(string text)
            {
                if (string.IsNullOrEmpty(text)) return text;
                foreach (var p in Pairs) text = text.Replace(p.Key, p.Value);
                return Regex.Replace(text, @"\b" + Base + @"\b", Id.ToString());
            }

            public bool Mentions(string text) => !string.IsNullOrEmpty(text) && Regex.IsMatch(text, @"\b" + Base + @"\b");
        }

        private static List<Swap> _mobs, _quests, _convs, _skills, _effects;

        private static void Load()
        {
            if (_mobs != null) return;
            _mobs = new List<Swap>();
            _quests = new List<Swap>();
            _convs = new List<Swap>();
            _skills = new List<Swap>();
            _effects = new List<Swap>();
            try
            {
                var file = Path.Combine(Path.GetDirectoryName(typeof(CustomContent).Assembly.Location) ?? ".", "CHPrivateMods.extras.txt");
                if (!System.IO.File.Exists(file)) return;
                foreach (var line in System.IO.File.ReadAllLines(file))
                {
                    var f = line.Split('|');
                    if (f.Length == 3 && f[0] == "TINT" && int.TryParse(f[1], out var tinted)) MobTint.Add(tinted, f[2]); // a monster's colour (MobTint.cs)
                    if (f.Length == 3 && f[0] == "LOOK" && int.TryParse(f[1], out var looked)) MobPetBody.Add(looked, f[2]); // drawn as a pet (MobPetBody.cs)
                    if (f.Length < 4) continue;
                    var s = new Swap();
                    switch (f[0])
                    {
                        case "MOB":
                            if (!int.TryParse(f[1], out s.Id) || !int.TryParse(f[2], out s.Base)) continue;
                            s.Name = f[3];
                            _mobs.Add(s);
                            break;
                        case "QUEST":
                            if (!int.TryParse(f[1], out s.Id) || !int.TryParse(f[2], out s.Base)) continue;
                            Pairs(s, f[3]);
                            _quests.Add(s);
                            break;
                        case "SKILL": // a boss ability's skill: a copy of an existing one under its own name
                        case "EFFECT": // and its status effect (the icon)
                            if (!int.TryParse(f[1], out s.Id) || !int.TryParse(f[2], out s.Base)) continue;
                            s.Name = f[3];
                            (f[0] == "SKILL" ? _skills : _effects).Add(s);
                            break;
                        case "CONV":
                            if (f.Length < 5 || !int.TryParse(f[2], out s.Base) || !int.TryParse(f[3], out s.Id)) continue;
                            s.File = f[1];
                            Pairs(s, f[4]);
                            _convs.Add(s);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] custom content list unreadable: " + e.Message);
            }
        }

        private static void Pairs(Swap s, string spec)
        {
            foreach (var pair in spec.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) s.Pairs.Add(new KeyValuePair<string, string>(pair.Substring(0, eq), pair.Substring(eq + 1)));
            }
        }

        private static object Value(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            return t.GetField(name, Any)?.GetValue(o) ?? t.GetProperty(name, Any)?.GetValue(o, null);
        }

        private static void Set(object o, string name, object value)
        {
            var t = o.GetType();
            var f = t.GetField(name, Any);
            if (f != null) f.SetValue(o, value);
            else t.GetProperty(name, Any)?.SetValue(o, value, null);
        }

        private static object Copy(object o) => Clone.Invoke(o, null);

        /// <summary>A string list, copied with the swap applied to each entry.</summary>
        private static IList Strings(IList list, Swap s)
        {
            if (list == null) return null;
            var copy = (IList)Activator.CreateInstance(list.GetType());
            foreach (var x in list) copy.Add(x is string str ? s.Apply(str) : x);
            return copy;
        }

        /// <summary>Called from CustomItems.Register (patch P11, CItemManager.Init): the monster and quest copies.</summary>
        public static void Register(object balanceClient)
        {
            try
            {
                Load();
                if (balanceClient == null) return;
                if (R.Get(balanceClient, "MobTemplates") is IDictionary mobs)
                    foreach (var m in _mobs)
                    {
                        if (mobs.Contains(m.Id) || !mobs.Contains(m.Base)) continue;
                        var copy = Copy(mobs[m.Base]);
                        Set(copy, "Id", m.Id);
                        Set(copy, "Name", m.Name);
                        mobs[m.Id] = copy;
                    }
                foreach (var (field, list) in new[] { ("SkillTemplates", _skills), ("StatusEffects", _effects) })
                    if (R.Get(balanceClient, field) is IDictionary templates)
                        foreach (var x in list)
                        {
                            if (templates.Contains(x.Id) || !templates.Contains(x.Base)) continue;
                            var copy = Copy(templates[x.Base]);
                            foreach (var idField in new[] { "Id", "SkillId", "ID" })
                                if (copy.GetType().GetField(idField, Any) is { } fi && fi.FieldType == typeof(int)) fi.SetValue(copy, x.Id);
                            Set(copy, "Name", x.Name);
                            templates[x.Id] = copy;
                        }
                if (R.Get(balanceClient, "QuestTemplates") is IDictionary quests)
                    foreach (var q in _quests)
                    {
                        if (quests.Contains(q.Id) || !quests.Contains(q.Base)) continue;
                        var copy = Copy(quests[q.Base]);
                        Set(copy, "Id", q.Id);
                        Set(copy, "Name", q.Apply(Value(copy, "Name") as string));
                        Set(copy, "Text", q.Apply(Value(copy, "Text") as string));
                        if (Value(copy, "Stages") is IList stages)
                        {
                            var list = (IList)Activator.CreateInstance(stages.GetType());
                            foreach (var st in stages)
                            {
                                var sc = Copy(st);
                                Set(sc, "StageText", q.Apply(Value(sc, "StageText") as string));
                                list.Add(sc);
                            }
                            Set(copy, "Stages", list);
                        }
                        quests[q.Id] = copy;
                    }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] custom monsters/quests not added: " + e.Message);
            }
        }

        /// <summary>
        /// Called from ConversationExtras.Add (patch P17): for a CONV entry, every start block and choice about the base
        /// quest is copied for the new one just before the original (so the new quest is checked first), and every line
        /// whose tag the swap changes (OPEN_EXTREME → OPEN_LEGEND…) is copied under its new tag.
        /// </summary>
        public static void AddToConversation(string convName, object conversation)
        {
            try
            {
                Load();
                foreach (var s in _convs)
                {
                    if (s.File != convName) continue;
                    var type = conversation.GetType();
                    var starts = (R.Field(type, "StartPoints")?.GetValue(conversation)) as IList;
                    var lines = (R.Field(type, "ConvoLines")?.GetValue(conversation)) as IList;
                    if (starts == null || lines == null) return;
                    var marker = "QUEST_ADD " + s.Id;
                    foreach (var l in lines)
                        if (Value(l, "options") is IList opts)
                            foreach (var o in opts)
                                if (Value(o, "choiceActions") is IList acts && acts.Contains(marker)) return; // already added
                    for (var i = starts.Count - 1; i >= 0; i--)
                    {
                        var st = starts[i];
                        var conds = Value(st, "startConditions") as IList;
                        var about = false;
                        if (conds != null) foreach (var c in conds) about |= s.Mentions(c as string);
                        if (!about) continue;
                        var copy = Copy(st);
                        Set(copy, "startConditions", Strings(conds, s));
                        Set(copy, "startGoto", s.Apply(Value(st, "startGoto") as string));
                        starts.Insert(i, copy);
                    }
                    var newLines = new List<object>();
                    foreach (var l in lines)
                    {
                        var tag = Value(l, "lineTag") as string;
                        var options = Value(l, "options") as IList;
                        if (s.Apply(tag) != tag)
                        {
                            var copy = Copy(l);
                            Set(copy, "lineTag", s.Apply(tag));
                            Set(copy, "lineText", s.Apply(Value(l, "lineText") as string));
                            if (options != null)
                            {
                                var list = (IList)Activator.CreateInstance(options.GetType());
                                foreach (var o in options) list.Add(Choice(o, s));
                                Set(copy, "options", list);
                            }
                            newLines.Add(copy);
                        }
                        else if (options != null)
                            for (var i = options.Count - 1; i >= 0; i--)
                            {
                                var o = options[i];
                                var about = false;
                                if (Value(o, "choiceConditions") is IList cc) foreach (var c in cc) about |= s.Mentions(c as string);
                                if (Value(o, "choiceActions") is IList ca) foreach (var a in ca) about |= s.Mentions(a as string);
                                if (about) options.Insert(i, Choice(o, s));
                            }
                    }
                    foreach (var l in newLines) lines.Add(l);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] custom conversation lines not added: " + e.Message);
            }
        }

        private static object Choice(object o, Swap s)
        {
            var copy = Copy(o);
            Set(copy, "choiceText", s.Apply(Value(o, "choiceText") as string));
            Set(copy, "gotoTag", s.Apply(Value(o, "gotoTag") as string));
            Set(copy, "choiceConditions", Strings(Value(o, "choiceConditions") as IList, s));
            Set(copy, "choiceActions", Strings(Value(o, "choiceActions") as IList, s));
            return copy;
        }
    }
}
