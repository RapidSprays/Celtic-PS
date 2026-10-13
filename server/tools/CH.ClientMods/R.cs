using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace CHPrivateMods
{
    /// <summary>
    /// Reflection by the game's readable (Mac build) names. On the Windows build, whose Assembly-CSharp is obfuscated,
    /// CH.Patcher writes CHPrivateMods.names.txt next to this DLL ("T|macType|winType", "M|macType::member[/arity]|winName";
    /// docs/windows-names.md) and every lookup goes through it. Without that file names are used as they are.
    /// </summary>
    internal static class R
    {
        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static Dictionary<string, string> _types, _typesBack, _members;

        private static void Load()
        {
            if (_members != null) return;
            _types = new Dictionary<string, string>();
            _typesBack = new Dictionary<string, string>();
            _members = new Dictionary<string, string>();
            try
            {
                var file = Path.Combine(Path.GetDirectoryName(typeof(R).Assembly.Location) ?? "", "CHPrivateMods.names.txt");
                if (!File.Exists(file)) return;
                foreach (var line in File.ReadAllLines(file))
                {
                    var p = line.Split('|');
                    if (p.Length != 3) continue;
                    if (p[0] == "T")
                    {
                        _types[p[1]] = p[2];
                        _typesBack[p[2]] = p[1];
                    }
                    else if (p[0] == "M") _members[p[1]] = p[2];
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] names not loaded: " + e);
            }
        }

        private static string Cecil(Type t) => (t.FullName ?? t.Name).Replace('+', '/');

        /// <summary>A runtime type's readable name.</summary>
        public static string MacName(Type t)
        {
            Load();
            var name = Cecil(t);
            return _typesBack.TryGetValue(name, out var mac) ? mac : name;
        }

        private static string Member(Type declaring, string name, int arity)
        {
            Load();
            var key = MacName(declaring) + "::" + name + (arity >= 0 ? "/" + arity : "");
            return _members.TryGetValue(key, out var win) ? win : name;
        }

        private static IEnumerable<Type> Hierarchy(Type t)
        {
            for (var c = t; c != null; c = c.BaseType) yield return c;
        }

        /// <summary>A game type by its readable full name (nested types as Outer/Inner).</summary>
        public static Type GameType(string macFullName)
        {
            Load();
            var name = _types.TryGetValue(macFullName, out var win) ? win : macFullName;
            return System.Type.GetType(name.Replace('/', '+') + ", Assembly-CSharp");
        }

        public static FieldInfo Field(Type t, string name) =>
            t == null ? null : Hierarchy(t).Select(c => c.GetField(Member(c, name, -1), Declared)).FirstOrDefault(f => f != null);

        public static PropertyInfo Property(Type t, string name) =>
            t == null ? null : Hierarchy(t).Select(c => c.GetProperty(Member(c, name, -1), Declared)).FirstOrDefault(p => p != null);

        public static MethodInfo Method(Type t, string name, int parameters) =>
            t == null ? null : Hierarchy(t).SelectMany(c => c.GetMethods(Declared).Where(m => m.Name == Member(c, name, parameters) && m.GetParameters().Length == parameters))
                .FirstOrDefault();

        public static object Get(object o, string field) => o == null ? null : Field(o.GetType(), field)?.GetValue(o);

        /// <summary>An enum value by its readable name.</summary>
        public static object Enum(Type enumType, string name) => System.Enum.Parse(enumType, Member(enumType, name, -1));

        /// <summary>An enum value's readable name.</summary>
        public static string EnumName(object value)
        {
            if (value == null) return null;
            Load();
            var type = value.GetType();
            var win = value.ToString();
            var prefix = MacName(type) + "::";
            foreach (var kv in _members)
                if (kv.Value == win && kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Key.IndexOf('/') < 0) return kv.Key.Substring(prefix.Length);
            return win;
        }
    }
}
