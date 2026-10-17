using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CHPrivateMods
{
    /// <summary>
    /// The aggro meter: a small box on the left of the screen with the threat list of the monster you're fighting, as the
    /// server sends it (private message 31003, GameServer.Aggro.cs; read through PassiveTree.OnMessage, patch P14). The
    /// monster's target is marked in red, you in gold. ".aggro off" in chat hides it.
    /// </summary>
    public sealed class AggroMeter : MonoBehaviour
    {
        internal struct Row
        {
            public string Name;
            public int Threat;
            public bool IsTarget, IsYou;
        }

        private const int MaxRows = 8;
        private const float Width = 300f, RowHeight = 26f, TitleHeight = 30f;
        private static readonly Color Back = new Color(0.04f, 0.04f, 0.06f, 0.72f);
        private static readonly Color BarOther = new Color(0.35f, 0.45f, 0.65f, 0.85f);
        private static readonly Color BarYou = new Color(0.95f, 0.72f, 0.2f, 0.9f);
        private static readonly Color BarTarget = new Color(0.85f, 0.25f, 0.2f, 0.9f);

        private static AggroMeter _instance;

        private RectTransform _panel;
        private TextMeshProUGUI _title;
        private readonly List<(RectTransform Bar, Image BarImage, TextMeshProUGUI Name, TextMeshProUGUI Value, GameObject Root)> _rows =
            new List<(RectTransform, Image, TextMeshProUGUI, TextMeshProUGUI, GameObject)>();

        /// <summary>Message 31003: byte shown; string monster; varint n × (string name, varint threat, byte flags).</summary>
        internal static void Read(Func<byte> readByte, Func<string> readString, Func<int> readInt)
        {
            if (readByte() == 0)
            {
                if (_instance != null) _instance.gameObject.SetActive(false);
                return;
            }
            var monster = readString();
            var rows = new List<Row>();
            for (int i = 0, n = readInt(); i < n; i++)
            {
                var row = new Row { Name = readString(), Threat = readInt() };
                var flags = readByte();
                row.IsTarget = (flags & 1) != 0;
                row.IsYou = (flags & 2) != 0;
                rows.Add(row);
            }
            if (_instance == null)
            {
                var root = new GameObject("CHAggroMeter", typeof(RectTransform));
                DontDestroyOnLoad(root);
                _instance = root.AddComponent<AggroMeter>();
                _instance.Build();
            }
            _instance.gameObject.SetActive(true);
            _instance.Show(monster, rows);
        }

        private void Build()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 31000; // under the passive tree panel (32000)
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            var anyText = FindAnyObjectByType<TMP_Text>();
            var font = anyText != null ? anyText.font : null;

            var panelGo = new GameObject("Panel", typeof(RectTransform));
            panelGo.transform.SetParent(transform, false);
            var back = panelGo.AddComponent<Image>();
            back.color = Back;
            back.raycastTarget = false;
            _panel = (RectTransform)panelGo.transform;
            _panel.anchorMin = _panel.anchorMax = new Vector2(0f, 0.5f);
            _panel.pivot = new Vector2(0f, 0.5f);
            _panel.anchoredPosition = new Vector2(16f, 60f);

            _title = Text("Title", _panel, font, 20, TextAlignmentOptions.Left);
            var tr = _title.rectTransform;
            tr.anchorMin = new Vector2(0, 1);
            tr.anchorMax = new Vector2(1, 1);
            tr.pivot = new Vector2(0.5f, 1);
            tr.offsetMin = new Vector2(10, -TitleHeight);
            tr.offsetMax = new Vector2(-10, -4);
            _title.color = new Color(0.92f, 0.92f, 0.95f);

            for (var i = 0; i < MaxRows; i++)
            {
                var rowGo = new GameObject("Row" + i, typeof(RectTransform));
                rowGo.transform.SetParent(_panel, false);
                var rr = (RectTransform)rowGo.transform;
                rr.anchorMin = new Vector2(0, 1);
                rr.anchorMax = new Vector2(1, 1);
                rr.pivot = new Vector2(0.5f, 1);
                rr.offsetMin = new Vector2(8, -(TitleHeight + 4 + (i + 1) * RowHeight) + 2);
                rr.offsetMax = new Vector2(-8, -(TitleHeight + 4 + i * RowHeight));

                var barGo = new GameObject("Bar", typeof(RectTransform));
                barGo.transform.SetParent(rr, false);
                var bar = barGo.AddComponent<Image>();
                bar.raycastTarget = false;
                var br = (RectTransform)barGo.transform;
                br.anchorMin = new Vector2(0, 0);
                br.anchorMax = new Vector2(1, 1);
                br.pivot = new Vector2(0, 0.5f);
                br.offsetMin = br.offsetMax = Vector2.zero;

                var name = Text("Name", rr, font, 17, TextAlignmentOptions.Left);
                Fill(name.rectTransform, 6, -70);
                var value = Text("Value", rr, font, 17, TextAlignmentOptions.Right);
                Fill(value.rectTransform, 0, -6);
                _rows.Add((br, bar, name, value, rowGo));
            }
        }

        private void Show(string monster, List<Row> rows)
        {
            _title.text = monster;
            var top = 1;
            foreach (var r in rows) top = Math.Max(top, r.Threat);
            for (var i = 0; i < _rows.Count; i++)
            {
                var (bar, image, name, value, root) = _rows[i];
                if (i >= rows.Count)
                {
                    root.SetActive(false);
                    continue;
                }
                var row = rows[i];
                root.SetActive(true);
                bar.anchorMax = new Vector2(Mathf.Clamp01(row.Threat / (float)top), 1);
                image.color = row.IsTarget ? BarTarget : row.IsYou ? BarYou : BarOther;
                name.text = (row.IsTarget ? "> " : "") + row.Name;
                value.text = top > 0 ? $"{row.Threat:N0}  {Mathf.RoundToInt(100f * row.Threat / top)}%" : "0";
            }
            _panel.sizeDelta = new Vector2(Width, TitleHeight + 10 + Math.Max(1, rows.Count) * RowHeight);
        }

        private static TextMeshProUGUI Text(string name, Transform parent, TMP_FontAsset font, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.fontSize = size;
            t.alignment = align;
            t.raycastTarget = false;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.color = Color.white;
            return t;
        }

        private static void Fill(RectTransform r, float left, float right)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(left, 0);
            r.offsetMax = new Vector2(right, 0);
        }
    }
}
