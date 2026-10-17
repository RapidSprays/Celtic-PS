using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CHPrivateMods
{
    /// <summary>
    /// Skills whose cast takes the weapon's attack delay on top of their own cast time (owner's rule, 2026-10-16: Shatter,
    /// Double Attack, Barbed Shot, Rend). The skill bar's casting circle shows that whole time, as the server casts them
    /// (server/content/skill-overrides.json "castWithWeaponDelay"; keep the two lists the same). Called with the skill and
    /// the client's own casting time from CSkill.getCastingTime (patch P18); any failure keeps the client's time.
    /// </summary>
    public static class WeaponDelayCast
    {
        // Until the server sends its list (message 31002 at log-in, from skill-overrides.json): the owner's four.
        private static HashSet<int> Skills = new HashSet<int> { 215, 212, 52, 18 };

        /// <summary>The server's weapon-delay skills (skill-overrides.json "castWithWeaponDelay").</summary>
        public static void SetSkills(IEnumerable<int> ids)
        {
            Skills = new HashSet<int>(ids);
            Log("server list: " + string.Join(", ", Skills));
        }

        private static PropertyInfo _player;
        private static FieldInfo _playerField;
        private static MethodInfo _attackSpeed, _template;
        private static int _logged;

        /// <summary>The first few results and every distinct failure go to CHPrivateMods.log beside the mod (the game itself logs nothing).</summary>
        private static void Log(string line)
        {
            if (_logged >= 40) return;
            _logged++;
            try
            {
                var file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(WeaponDelayCast).Assembly.Location) ?? "", "CHPrivateMods.log");
                System.IO.File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss.fff") + " weapon delay cast: " + line + Environment.NewLine);
            }
            catch
            {
                // logging must never break the skill bar
            }
        }

        public static float CastingTime(object skill, float clientTime)
        {
            try
            {
                if (skill == null) return clientTime;
                var id = SkillId(skill);
                if (id < 0 && _logged < 5) Log("could not read the skill id from " + skill.GetType().FullName);
                if (!Skills.Contains(id)) return clientTime;
                var player = Player();
                if (player == null) { Log("skill " + id + ": no player found"); return clientTime; }
                _attackSpeed = _attackSpeed ?? R.Method(player.GetType(), "GetAttackSpeed", 0);
                if (_attackSpeed == null) { Log("skill " + id + ": GetAttackSpeed not found on " + player.GetType().FullName); return clientTime; }
                var delayMs = Convert.ToSingle(_attackSpeed.Invoke(player, null));
                // The client's time already holds 35% of the weapon delay for skills it marks as using the weapon; take its
                // template's own cast time and add the whole delay instead.
                var own = OwnCastTime(skill, clientTime);
                var result = own + delayMs / 1000f;
                if (_logged < 20) Log("skill " + id + ": client " + clientTime.ToString("F2") + " s, own cast " + own.ToString("F2") + " s, weapon delay " + delayMs + " ms -> " + result.ToString("F2") + " s");
                return result;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] weapon delay cast: " + e.Message);
                Log("error: " + e);
                return clientTime;
            }
        }

        private static int SkillId(object skill)
        {
            _template = _template ?? R.Method(skill.GetType(), "GetTemplate", 0);
            var template = _template?.Invoke(skill, null);
            if (template == null) return -1;
            var t = template.GetType();
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            // Windows: a SkillId field of the game's SkillType enum; the Mac build: GetSkillID().
            var value = t.GetField("SkillId", any)?.GetValue(template)
                        ?? t.GetProperty("SkillId", any)?.GetValue(template, null)
                        ?? R.Method(t, "GetSkillID", 0)?.Invoke(template, null)
                        ?? R.Field(t, "m_skillID")?.GetValue(template);
            return value == null ? -1 : Convert.ToInt32(value);
        }

        private static object Player()
        {
            var game = R.GameType("CGame");
            if (game == null) return null;
            _player = _player ?? R.Property(game, "ThePlayer");
            if (_player != null) return _player.GetValue(null, null);
            _playerField = _playerField ?? R.Field(game, "ThePlayer");
            return _playerField?.GetValue(null);
        }

        /// <summary>The client's casting time without the weapon share it adds itself (m_weapon_delay).</summary>
        private static float OwnCastTime(object skill, float clientTime)
        {
            var share = R.Field(skill.GetType(), "m_weapon_delay")?.GetValue(skill);
            return share == null ? clientTime : clientTime - Convert.ToSingle(share);
        }
    }
}
