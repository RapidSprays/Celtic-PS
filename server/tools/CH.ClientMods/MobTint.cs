using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CHPrivateMods
{
    /// <summary>
    /// The owner's monster colours (custom-monsters.json and monster-overrides.json "colour", written to
    /// CHPrivateMods.extras.txt as <c>TINT|mobTemplateId|#RRGGBB</c> by scripts/install-custom-gear.py). Twice a second
    /// every nearby monster (CGame.m_theMobs) of a listed template gets that tint on its model (CMD5_Model.TintColour,
    /// the game's own tint: each part's colour times this), set again each time so parts loaded later and
    /// the game's own resets get it too.
    /// </summary>
    public sealed class MobTint : MonoBehaviour
    {
        private static readonly Dictionary<int, Color> Tints = new Dictionary<int, Color>();
        private static MobTint _instance;
        private float _next;

        internal static bool TryGet(int templateId, out Color tint) => Tints.TryGetValue(templateId, out tint);

        /// <summary>A TINT line from the extras file.</summary>
        internal static void Add(int templateId, string colour)
        {
            if (!ColorUtility.TryParseHtmlString(colour.StartsWith("#") ? colour : "#" + colour, out var c)) return;
            Tints[templateId] = c;
            if (_instance != null) return;
            try
            {
                var go = new GameObject("CHMobTint");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<MobTint>();
            }
            catch (Exception e)
            {
                ModLog.Once("monster colour: couldn't start: " + e.Message);
            }
        }

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            try
            {
                foreach (var (id, model) in ModLog.NearMobs())
                {
                    if (model == null || !Tints.TryGetValue(id, out var tint)) continue;
                    var property = R.Property(model.GetType(), "TintColour");
                    if (property == null) { ModLog.Once("monster colour: no TintColour on the model"); continue; }
                    try
                    {
                        property.SetValue(model, tint, null); // every time: parts that load later get it too
                        ModLog.Once($"monster colour: {id} tinted {ColorUtility.ToHtmlStringRGB(tint)}");
                    }
                    catch (Exception e)
                    {
                        ModLog.Once("monster colour: " + (e.InnerException ?? e).Message); // a model still loading: next time
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.Once("monster colours stopped: " + e);
                enabled = false;
            }
        }
    }
}
