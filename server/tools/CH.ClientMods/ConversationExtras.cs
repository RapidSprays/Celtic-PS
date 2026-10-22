using System;
using System.Collections;
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
}
