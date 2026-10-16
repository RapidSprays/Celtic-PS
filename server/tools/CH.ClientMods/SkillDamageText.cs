using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace CHPrivateMods
{
    /// <summary>
    /// Skill descriptions show the skill's own damage first and the total with gear in brackets, "4668 (7198)" (owner's
    /// request, 2026-10-16). The client's damage number (CSkillTemplateManager.GetModifiedDamage) is the rank formula plus
    /// every equipped item's SkillBonuses for the skill; this takes those bonuses back off for the first number. Runs on
    /// the description CSkillTemplateManager.GetDescription returns (patch P16); any failure leaves the text as it was.
    /// </summary>
    public static class SkillDamageText
    {
        // A coloured number as the description writes it: Unity "<color=…>N</color>" or NGUI "[RRGGBB]N[-]".
        private static readonly Regex Coloured = new Regex(@"(<color=[^>]+>|\[[0-9A-Fa-f]{6}\])(\d+)(</color>|\[-\])");

        /// <summary>Called with the finished description, the game and the skill id (patch P16).</summary>
        public static string Show(string text, object game, int skillId)
        {
            try
            {
                if (string.IsNullOrEmpty(text) || game == null) return text;
                var bonus = GearBonus(game, skillId);
                if (bonus <= 0) return text;
                var done = false;
                return Coloured.Replace(text, m =>
                {
                    if (done || !int.TryParse(m.Groups[2].Value, out var total) || total < bonus) return m.Value;
                    done = true;
                    return m.Groups[1].Value + (total - bonus) + " (" + total + ")" + m.Groups[3].Value;
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] skill damage text: " + e.Message);
                return text;
            }
        }

        /// <summary>The sum of the equipped items' SkillBonuses for this skill, as the client adds them.</summary>
        private static int GearBonus(object game, int skillId)
        {
            var inventory = R.Get(game, "m_playersInvent");
            if (!(R.Get(inventory, "m_EquipedItems") is IEnumerable items)) return 0;
            var total = 0;
            foreach (var item in items)
            {
                var template = item == null ? null : R.Get(item, "m_itemTemplate");
                if (!(Value(template, "SkillBonuses") is IEnumerable bonuses)) continue;
                foreach (var b in bonuses)
                    if (b != null && Convert.ToInt32(Value(b, "SkillType")) == skillId) total += Convert.ToInt32(Value(b, "Amount"));
            }
            return total;
        }

        /// <summary>A property or field of the game's data classes (Balance.Model keeps its names on Windows too).</summary>
        private static object Value(object o, string name)
        {
            if (o == null) return null;
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = o.GetType();
            return type.GetProperty(name, any)?.GetValue(o) ?? type.GetField(name, any)?.GetValue(o);
        }
    }
}
