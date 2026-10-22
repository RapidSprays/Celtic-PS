using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CHPrivateMods
{
    /// <summary>
    /// Extra choices in NPC conversations, which the client otherwise only reads from its own conversation files (owner's
    /// request, 2026-10-16): Sedric the Pet Vendor and Remus the Mount Vendor open with a choice that opens a gold shop
    /// selling only their tokens (server/content/shop-extras.json: shops 9100 and 9101). Called with the conversation file
    /// name and the parsed conversation after StaticConversation.LoadFromFile succeeds (patch P17); any failure leaves the
    /// conversation as it was.
    /// </summary>
    public static class ConversationExtras
    {
        public static void Add(string convName, object conversation)
        {
            CustomContent.AddToConversation(convName, conversation); // the owner's quests' NPC lines (CHPrivateMods.extras.txt)
            if (CustomContent.Trader(convName) is { } trader)
            {
                MakeTrader(conversation, trader.Greeting, trader.Choices);
                return;
            }
            try
            {
                // The line the choice joins, its text, its action, and where it lands: -1 = the shop replaces the
                // conversation, else the line to go back to; beforeLast puts it above the line's last choice (Back).
                string tag = "OPEN", text, action, gotoTag = "-1";
                var beforeLast = false;
                switch (convName)
                {
                    case "cv_pet_vendor":
                        text = "Buy Pet Tokens, Cursed Coins, Phoenix Eggs, Corrupted Seeds and Magical Bells.";
                        action = "SHOP 9100";
                        break;
                    case "cv_mount_vendor":
                        text = "Buy Mount Tokens, Dark Essences and Nightmare Essences.";
                        action = "SHOP 9101";
                        break;
                    case "cv_fc_legacy_lux_trader": // Vaultkeeper Renauld's shop list (owner's request, 2026-10-20)
                        tag = "SHOP_LIST";
                        text = "Luxury Mount Items.";
                        action = "SHOP 9102";
                        gotoTag = "SHOP_LIST";
                        beforeLast = true;
                        break;
                    default:
                        return;
                }
                var lines = (R.Field(conversation.GetType(), "ConvoLines")?.GetValue(conversation)
                             ?? R.Property(conversation.GetType(), "ConvoLines")?.GetValue(conversation, null)) as IList;
                if (lines == null) return;
                foreach (var line in lines)
                {
                    if (!tag.Equals(Value(line, "lineTag") as string)) continue;
                    if (!(Value(line, "options") is IList options)) return;
                    foreach (var o in options)
                        if (text.Equals(Value(o, "choiceText") as string)) return; // already added
                    var optionType = options.GetType().GetGenericArguments()[0];
                    var option = Activator.CreateInstance(optionType);
                    Set(option, "choiceText", text);
                    Set(option, "choiceOptions", "[S]"); // the shop icon
                    Set(option, "gotoTag", gotoTag);     // -1: the shop screen replaces the conversation
                    (Value(option, "choiceActions") as IList)?.Add(action);
                    options.Insert(beforeLast ? Math.Max(0, options.Count - 1) : 0, option);
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] conversation extras: " + e.Message);
            }
        }

        /// <summary>
        /// The owner's trade NPCs (npc-extras.json "greeting" and "trades", CHPrivateMods.extras.txt TRADER lines): an
        /// unused conversation of the game's (the beta pearl ring vendor's) becomes one line, the greeting, with a choice
        /// per trade window (TOKENVENDOR id, the server's token-vendor-extras.json) and Goodbye.
        /// </summary>
        private static void MakeTrader(object conversation, string greeting, IList<(string Text, int Vendor)> choices)
        {
            try
            {
                var type = conversation.GetType();
                var starts = (R.Field(type, "StartPoints")?.GetValue(conversation)) as IList;
                var lines = (R.Field(type, "ConvoLines")?.GetValue(conversation)) as IList;
                if (lines == null || lines.Count == 0) return;
                IList options = null;
                foreach (var l in lines)
                    if (Value(l, "options") is IList o && o.Count > 0) { options = o; break; }
                if (options == null) return;
                var line = Clone(lines[0]);
                Set(line, "lineTag", "OPEN");
                Set(line, "lineText", greeting.Replace("\\n", "\n"));
                var list = (IList)Activator.CreateInstance(options.GetType());
                foreach (var (text, vendor) in choices)
                    list.Add(Choice(options[0], text, "[TVG]", "TOKENVENDOR " + vendor, "OPEN"));
                list.Add(Choice(options[0], "Goodbye.", "[X]", " ", "-1"));
                Set(line, "options", list);
                starts?.Clear(); // no start conditions: it opens on OPEN
                lines.Clear();
                lines.Add(line);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] trader conversation: " + e.Message);
            }
        }

        private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        private static object Clone(object o) => CloneMethod.Invoke(o, null);

        private static object Choice(object template, string text, string icon, string action, string gotoTag)
        {
            var c = Clone(template);
            Set(c, "choiceText", text);
            Set(c, "choiceOptions", icon);
            Set(c, "gotoTag", gotoTag);
            if (Value(template, "choiceConditions") is IList conds) Set(c, "choiceConditions", Activator.CreateInstance(conds.GetType()));
            if (Value(template, "choiceActions") is IList acts)
            {
                var a = (IList)Activator.CreateInstance(acts.GetType());
                a.Add(action);
                Set(c, "choiceActions", a);
            }
            return c;
        }

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>A field or property of the conversation data classes (Balance.Model keeps its names on Windows too).</summary>
        private static object Value(object o, string name)
        {
            if (o == null) return null;
            var type = o.GetType();
            return type.GetField(name, Any)?.GetValue(o) ?? type.GetProperty(name, Any)?.GetValue(o, null);
        }

        private static void Set(object o, string name, object value)
        {
            var type = o.GetType();
            var field = type.GetField(name, Any);
            if (field != null) field.SetValue(o, value);
            else type.GetProperty(name, Any)?.SetValue(o, value, null);
        }
    }

    /// <summary>
    /// Adds the made-up trader conversation ids to the game's conversation lookups (CController's
    /// List&lt;ConversationLookup&gt;, searched each time an NPC is clicked), each a copy of the trader file's entry under
    /// its own id. Retries every second until the game has loaded its lookups, then stops.
    /// </summary>
    public sealed class TraderLookups : MonoBehaviour
    {
        private static TraderLookups _instance;
        private float _next;

        internal static void Start()
        {
            if (_instance != null) return;
            try
            {
                var go = new GameObject("CHTraderLookups");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<TraderLookups>();
            }
            catch (Exception e)
            {
                ModLog.Once("trader: couldn't start: " + e.Message);
            }
        }

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            try
            {
                var controllerType = R.GameType("CController");
                var controller = R.Field(controllerType, "instance")?.GetValue(null);
                if (controller == null) return;
                IList lookups = null;
                foreach (var f in controllerType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (f.FieldType.IsGenericType && f.FieldType.GetGenericArguments()[0].Name == "ConversationLookup")
                        lookups = f.GetValue(controller) as IList;
                if (lookups == null || lookups.Count == 0) return; // not loaded yet
                object donor = null;
                var have = new HashSet<int>();
                foreach (var l in lookups)
                {
                    var t = l.GetType();
                    if (t.GetField("ConversationId")?.GetValue(l) is int id) have.Add(id);
                    if (CustomContent.TraderFile.Equals(t.GetField("Filename")?.GetValue(l) as string)) donor = l;
                }
                if (donor == null)
                {
                    ModLog.Once("trader: the trader conversation file isn't in the game's lookups");
                    enabled = false;
                    return;
                }
                var clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
                var added = 0;
                foreach (var id in CustomContent.TraderIds())
                {
                    if (have.Contains(id)) continue;
                    var copy = clone.Invoke(donor, null);
                    copy.GetType().GetField("ConversationId")?.SetValue(copy, id);
                    lookups.Add(copy);
                    added++;
                }
                ModLog.Once($"trader: {added} conversation id(s) added to the lookups");
                enabled = false;
            }
            catch (Exception e)
            {
                ModLog.Once("trader: lookups not added: " + e.Message);
                enabled = false;
            }
        }
    }
}
