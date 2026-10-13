using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace CHPrivateMods
{
    /// <summary>
    /// No rank caps on player skills (owner's rule). Each player skill's level table (and PvP table) is extended to
    /// <see cref="Ranks"/> ranks and every rank's minimum character level is cleared, so the skill popup's Upgrade and
    /// Max buttons go past the old maximum and Levels[rank] never runs off the end. Must match the server's
    /// CH.Server.Game.SkillLevels exactly. Runs from CustomItems.Register (patch P11), on the shared BalanceClient
    /// templates that CSkillTemplateManager also holds.
    /// </summary>
    public static class SkillRanks
    {
        private const int Ranks = 260;
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Extend(object balanceClient)
        {
            try
            {
                var templates = R.Get(balanceClient, "SkillTemplates") as IDictionary;
                if (templates == null) return;
                foreach (var template in templates.Values)
                {
                    var type = template.GetType();
                    var ability = (int)R.Field(type, "AbilityId").GetValue(template);
                    var levels = R.Field(type, "Levels").GetValue(template) as IList;
                    if (ability < 0 || levels == null || levels.Count < 2) continue;
                    ExtendList(levels);
                    if (R.Field(type, "PvpLevels")?.GetValue(template) is IList pvp && pvp.Count >= 2) ExtendList(pvp);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CHPrivateMods] skill ranks not extended: " + ex);
            }
        }

        private static void ExtendList(IList levels)
        {
            if (levels.Count >= Ranks) return;
            var type = levels[0].GetType();
            var minLevel = R.Field(type, "MinLevel");
            var templateLevel = R.Field(type, "TemplateLevel");
            var initial = R.Field(type, "InitialDamage");
            var baseDamage = R.Field(type, "BaseDamage");
            foreach (var level in levels) minLevel.SetValue(level, 0);
            var last = levels[levels.Count - 1];
            var prev = levels[levels.Count - 2];
            // Server levels hold floats; round the same way so damage numbers match.
            var lastInitial = (double)(float)(double)initial.GetValue(last);
            var lastBase = (double)(float)(double)baseDamage.GetValue(last);
            var initialStep = (float)lastInitial - (float)(double)initial.GetValue(prev);
            var baseStep = (float)lastBase - (float)(double)baseDamage.GetValue(prev);
            var realCount = levels.Count;
            for (var rank = realCount; rank < Ranks; rank++)
            {
                var n = rank - (realCount - 1);
                var copy = Clone.Invoke(last, null);
                templateLevel.SetValue(copy, rank);
                initial.SetValue(copy, (double)((float)lastInitial + initialStep * n));
                baseDamage.SetValue(copy, (double)((float)lastBase + baseStep * n));
                levels.Add(copy);
            }
        }
    }
}
