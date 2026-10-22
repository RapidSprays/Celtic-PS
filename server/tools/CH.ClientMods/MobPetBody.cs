using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CHPrivateMods
{
    /// <summary>
    /// Monsters drawn as a pet (custom-monsters.json "petLook", written to CHPrivateMods.extras.txt as
    /// <c>LOOK|mobTemplateId|petPrefab</c> by scripts/install-custom-gear.py): a chicken boss. Twice a second every nearby
    /// monster (CGame.m_theMobs) of a listed template gets the pet's model (ResourceLocator.LoadPetPrefab, the game's own
    /// pets) under its model's root, so it moves, turns and sizes with it; the monster's own body is hidden. The pet's
    /// clips (its CompanionAnimationHelper) play run while it moves, attack while it fights standing still, idle otherwise,
    /// and it tips over when it dies. The name, selection and clicking stay the monster's.
    /// </summary>
    public sealed class MobPetBody : MonoBehaviour
    {
        private static readonly Dictionary<int, string> Looks = new Dictionary<int, string>();
        private static readonly Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();
        private static MobPetBody _manager;
        private float _next;

        // on each pet body
        private object _npc, _model;
        private Transform _primary;
        private Animation _animation;
        private AnimationClip _run, _idle, _attack;
        private Quaternion _fix = Quaternion.identity;
        private Vector3 _baseScale = Vector3.one;
        private Vector3 _lastPosition;
        private float _attackUntil, _nextAttack;
        private bool _dead;

        /// <summary>A LOOK line from the extras file.</summary>
        internal static void Add(int templateId, string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return;
            Looks[templateId] = prefab.Trim();
            ModLog.Once($"pet look: monster {templateId} listed as {prefab.Trim()}");
            if (_manager != null) return;
            try
            {
                var go = new GameObject("CHMobPetBodies");
                DontDestroyOnLoad(go);
                _manager = go.AddComponent<MobPetBody>();
            }
            catch (Exception e)
            {
                ModLog.Once("pet look: couldn't start: " + e.Message);
            }
        }

        // Pets whose colours and sizes are one model (PlayerCompanion's load): pet_chicken_black_6 is pet_chicken_master
        // set to colour "black", tier "6" (CompanionPrefabHelper.SetAsColor / SetAsTier).
        private static readonly string[] Masters = { "bear", "rabbit", "wardog", "wolf", "boar", "spider", "chicken", "competition", "top10competition", "eagle" };

        private static GameObject Load(string name)
        {
            var load = R.Method(R.GameType("ResourceLocator"), "LoadPetPrefab", 1);
            return load?.Invoke(null, new object[] { name }) as GameObject;
        }

        private static GameObject Prefab(string name)
        {
            if (Prefabs.TryGetValue(name, out var p) && p != null) return p;
            var parts = name.Split('_');
            if (parts.Length == 4 && Array.IndexOf(Masters, parts[1]) >= 0)
            {
                p = Load("pet_" + parts[1] + "_master");
                if (p != null && p.GetComponent(R.GameType("CelticHeroes.Companions.CompanionPrefabHelper")) is Component helper)
                {
                    helper.GetType().GetMethod("SetAsColor")?.Invoke(helper, new object[] { parts[2] });
                    helper.GetType().GetMethod("SetAsTier")?.Invoke(helper, new object[] { parts[3] });
                }
                else if (p != null) ModLog.Once($"pet look: {name}: no colour/size helper on the model");
            }
            else if (name.StartsWith("pet_phoenix") || name.StartsWith("pet_dragon"))
            {
                var species = name.StartsWith("pet_phoenix") ? "pet_phoenix_master_" : "pet_dragon_master_";
                var colour = new[] { "orange", "red", "purple", "blue", "black", "green" }.FirstOrDefault(c => name.Contains(c));
                p = colour == null ? null : Load(species + colour + (name.Contains("spirit") ? "_spirit" : ""));
            }
            if (p == null) p = Load(name);
            if (p != null) Prefabs[name] = p;
            return p;
        }

        private void Update()
        {
            if (_npc != null) Animate();
            else if (this == _manager && Time.unscaledTime >= _next)
            {
                _next = Time.unscaledTime + 0.5f;
                Scan();
            }
        }

        private static void Scan()
        {
            try
            {
                var controller = R.Field(R.GameType("CController"), "instance")?.GetValue(null);
                if (!(R.Get(R.Get(controller, "m_game"), "m_theMobs") is IList mobs)) return;
                foreach (var mob in mobs)
                {
                    var npc = R.Get(mob, "m_basicNPC");
                    if (npc == null || !(R.Get(npc, "m_templateNumber") is int id) || !Looks.TryGetValue(id, out var look)) continue;
                    var model = R.Get(mob, "m_modelBase");
                    if (!(R.Get(model, "m_rootObjectTransform") is Transform root) || !(R.Get(model, "m_primaryObject") is GameObject primary))
                    {
                        ModLog.Once($"pet look: {id}'s model isn't loaded yet");
                        continue;
                    }
                    if (root.GetComponentInChildren<MobPetBody>(true) == null) Attach(npc, model, root, primary.transform, look, id);
                }
            }
            catch (Exception e)
            {
                ModLog.Once("monster pet looks stopped: " + e);
                _manager.enabled = false;
            }
        }

        private static void Attach(object npc, object model, Transform root, Transform primary, string look, int id)
        {
            GameObject prefab;
            try { prefab = Prefab(look); }
            catch (Exception e)
            {
                ModLog.Once($"pet look: {look} didn't load: {(e.InnerException ?? e).Message}");
                return;
            }
            if (prefab == null)
            {
                ModLog.Once($"pet look: {look} not loaded yet");
                return; // still loading (or no such pet): next scan
            }
            var parts = look.Split('_');
            if (parts.Length == 4 && prefab.GetComponent(R.GameType("CelticHeroes.Companions.CompanionPrefabHelper")) is Component ph)
            {   // set again: the cached model is shared by every pet look of that species
                ph.GetType().GetMethod("SetAsColor")?.Invoke(ph, new object[] { parts[2] });
                ph.GetType().GetMethod("SetAsTier")?.Invoke(ph, new object[] { parts[3] });
            }
            var body = Instantiate(prefab, root, false);
            body.name = "CHPetBody " + look;
            body.transform.localPosition = Vector3.zero;
            var me0 = body.transform.localScale; // the pet's own size for its tier
            foreach (var t in body.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = root.gameObject.layer; // the monster's camera layer
            var helperType = R.GameType("CelticHeroes.Companions.CompanionAnimationHelper");
            var helper = helperType == null ? null : body.GetComponentInChildren(helperType, true);
            foreach (var mb in body.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false; // the pet's own logic follows a player
            foreach (var c in body.GetComponentsInChildren<Collider>(true)) c.enabled = false;   // clicks go to the monster
            var me = body.AddComponent<MobPetBody>();
            me._npc = npc;
            me._model = model;
            me._primary = primary;
            me._lastPosition = root.position;
            me._baseScale = me0;
            if (helper != null)
            {
                me._animation = R.Get(helper, "animation") as Animation;
                me._run = R.Get(helper, "runAnimation") as AnimationClip;
                me._idle = R.Get(helper, "IdleAnimationClip") as AnimationClip;
                me._attack = R.Get(helper, "attackAnimationClip") as AnimationClip;
                if (R.Get(helper, "OrientationFix") is Vector3 fix) me._fix = Quaternion.Euler(fix);
            }
            if (me._animation == null) me._animation = body.GetComponentInChildren<Animation>(true);
            if (me._animation != null) me._animation.enabled = true;
            ModLog.Once($"pet look: {id} size {(R.Get(npc, "m_scale") is float s0 ? s0 : -1)}, pet scale {me0}, root scale {root.lossyScale}");
            ModLog.Once($"pet look: {id} drawn as {look} (animations: {(me._animation != null ? "yes" : "none")}, run {me._run?.name}, idle {me._idle?.name}, attack {me._attack?.name})");
            if (MobTint.TryGet(id, out var tint))
                foreach (var r in body.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.materials)
                        if (m.HasProperty("_Color")) m.color = tint;
        }

        private void Animate()
        {
            try
            {
                if (_primary == null) { Destroy(gameObject); return; } // the model was rebuilt: the next scan attaches a new one
                foreach (var r in _primary.GetComponentsInChildren<Renderer>(true))
                    if (r.enabled && !r.transform.IsChildOf(transform)) r.enabled = false; // the monster's own body
                transform.rotation = _primary.rotation * _fix;
                // The size the server sends (custom-monsters.json "scale") is put on the monster's own body; the pet takes it
                // from CBasicNPC.m_scale instead, whatever the monster's root is scaled by.
                var size = R.Get(_npc, "m_scale") is float sc && sc > 0 ? sc : 1f;
                var parent = transform.parent.lossyScale;
                transform.localScale = new Vector3(_baseScale.x * size / Mathf.Max(0.0001f, parent.x), _baseScale.y * size / Mathf.Max(0.0001f, parent.y),
                    _baseScale.z * size / Mathf.Max(0.0001f, parent.z));
                if (_dead) return;
                if (R.Get(_npc, "m_currentHealth") is int hp && hp <= 0 || R.Get(_npc, "m_hitpoints") is int h2 && h2 <= 0)
                {
                    _dead = true;
                    if (_animation != null) _animation.Stop();
                    transform.rotation = _primary.rotation * _fix * Quaternion.Euler(0, 0, 90); // on its side
                    return;
                }
                var position = transform.parent.position;
                var moving = (position - _lastPosition).sqrMagnitude > 0.0004f;
                _lastPosition = position;
                var fighting = R.Get(_npc, "m_inCombat") is bool c && c;
                if (!moving && fighting && Time.time >= _nextAttack && _attack != null)
                {
                    _nextAttack = Time.time + Mathf.Max(1.5f, _attack.length + 0.5f);
                    _attackUntil = Time.time + _attack.length;
                    Play(_attack, 0.1f, true);
                }
                else if (Time.time >= _attackUntil) Play(moving ? _run : _idle, 0.2f, false);
            }
            catch (Exception e)
            {
                ModLog.Once("pet look: animating stopped: " + e.Message);
                enabled = false;
            }
        }

        private void Play(AnimationClip clip, float fade, bool restart)
        {
            if (_animation == null || clip == null) return;
            if (_animation[clip.name] == null) _animation.AddClip(clip, clip.name);
            if (!restart && _animation.IsPlaying(clip.name)) return;
            _animation[clip.name].wrapMode = clip == _attack ? WrapMode.Once : WrapMode.Loop;
            if (restart) _animation.Stop(clip.name);
            _animation.CrossFade(clip.name, fade);
        }
    }
}
