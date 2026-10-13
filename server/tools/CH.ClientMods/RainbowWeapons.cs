using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace CHPrivateMods
{
    /// <summary>
    /// The "rainbow" effect: when a character model finishes loading (CMD5_Model.checkDownloadComplete, patch P12) and
    /// its visible weapon is a rainbow item, every weapon mesh gets a <see cref="RainbowCycler"/>. Works for the player,
    /// other players and the inventory/examine models alike.
    /// </summary>
    public static class RainbowWeapons
    {
        public static void OnModelLoaded(object model)
        {
            try
            {
                if (model == null) return;
                var ids = R.Get(model, "m_modelIDs");
                var visible = R.Get(ids, "m_visibleEquipmentTemplates") as IDictionary;
                if (visible == null) return;
                var rainbow = false;
                foreach (DictionaryEntry kv in visible)
                {
                    if (R.EnumName(kv.Key) != "Weapon" || kv.Value == null) continue;
                    var id = R.Get(kv.Value, "ID");
                    rainbow = id is int templateId && CustomItems.IsRainbow(templateId);
                }
                if (!rainbow) return;
                var components = R.Get(model, "m_modelComponents") as IList;
                if (components == null) return;
                var index = 0;
                foreach (var component in components)
                {
                    if (R.EnumName(R.Get(component, "m_slotType")) != "Weapon") continue;
                    if (!(R.Get(component, "m_gameObject") is GameObject go) || go.GetComponent<RainbowCycler>() != null) continue;
                    var cycler = go.AddComponent<RainbowCycler>();
                    cycler.Component = component;
                    cycler.Offset = 0.15f * index++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CHPrivateMods] rainbow not applied: " + ex);
            }
        }
    }

    /// <summary>
    /// Cycles one weapon mesh through the hues. The body goes through the game's own PrimaryColour (so fades and
    /// transparency keep working); the additive glow mesh gets its _TintColor.
    /// </summary>
    public sealed class RainbowCycler : MonoBehaviour
    {
        public object Component;
        public float Offset;
        private PropertyInfo _primary;
        private FieldInfo _material;
        private bool _effect;

        private void Start()
        {
            if (Component == null) return;
            var type = Component.GetType();
            _primary = R.Property(type, "PrimaryColour");
            _material = R.Field(type, "m_material");
            _effect = R.Get(Component, "effectMesh") is bool b && b;
        }

        private void Update()
        {
            if (Component == null) return;
            var colour = Color.HSVToRGB((Time.time * 0.25f + Offset) % 1f, 0.8f, 1f);
            try
            {
                if (_effect)
                {
                    if (_material?.GetValue(Component) is Material m)
                    {
                        if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", colour * 0.75f);
                        else if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
                    }
                }
                else
                {
                    _primary?.SetValue(Component, colour);
                }
            }
            catch (Exception)
            {
                enabled = false; // stop quietly rather than log every frame
            }
        }
    }
}
