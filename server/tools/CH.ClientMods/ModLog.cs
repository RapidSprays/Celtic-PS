using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CHPrivateMods
{
    /// <summary>Lines in CHPrivateMods.log beside the mod (the game itself logs nothing); each distinct line once.</summary>
    internal static class ModLog
    {
        private static readonly HashSet<string> Said = new HashSet<string>();

        internal static void Once(string line)
        {
            if (Said.Count > 200 || !Said.Add(line)) return;
            try
            {
                var file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(ModLog).Assembly.Location) ?? "", "CHPrivateMods.log");
                System.IO.File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// The monsters drawn near the player (CGame.m_theMobs, CNonPlayerCharacter) as (template id, its model). Their
        /// template is on the CBasicNPC each one is made from (m_basicNPC.m_templateNumber).
        /// </summary>
        internal static IEnumerable<(int Template, object Model)> NearMobs()
        {
            var controller = R.Field(R.GameType("CController"), "instance")?.GetValue(null);
            var game = R.Get(controller, "m_game");
            if (game == null) yield break;
            if (!(R.Get(game, "m_theMobs") is IList mobs))
            {
                Once("monster list (CGame.m_theMobs) not found");
                yield break;
            }
            foreach (var mob in mobs)
            {
                if (mob == null || !(R.Get(R.Get(mob, "m_basicNPC"), "m_templateNumber") is int id)) continue;
                yield return (id, R.Get(mob, "m_modelBase"));
            }
        }
    }
}
