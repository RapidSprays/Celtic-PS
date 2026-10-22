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

        // The look: these defaults until the server sends aggro-meter.json (message 31004, at log-in).
        private static int MaxRows = 8;
        private static float Width = 300f, RowHeight = 26f, TitleHeight = 30f, FontSize = 17f, TitleFontSize = 20f, X = 16f, Y = 60f;
        private static Color Back = new Color(0.04f, 0.04f, 0.06f, 0.72f);
        private static Color BarOther = new Color(0.35f, 0.45f, 0.65f, 0.85f);
        private static Color BarYou = new Color(0.95f, 0.72f, 0.2f, 0.9f);
        private static Color BarTarget = new Color(0.85f, 0.25f, 0.2f, 0.9f);
        private static Color TitleColour = new Color(0.92f, 0.92f, 0.95f);
        private static Color TextColour = Color.white;
        private static string _lastMonster;
        private static List<Row> _lastRows;

        /// <summary>Message 31004: varint n × (string key, string value) from the server's aggro-meter.json; the box is rebuilt.</summary>
        internal static void ReadStyle(Func<string> readString, Func<int> readInt)
        {
            for (int i = 0, n = readInt(); i < n; i++)
            {
                var key = readString();
                var value = readString();
                float F(float fallback) => float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;
                Color C(Color fallback) => ColorUtility.TryParseHtmlString(value.StartsWith("#") ? value : "#" + value, out var c) ? c : fallback;
                switch (key)
                {
                    case "width": Width = Mathf.Max(80, F(Width)); break;
                    case "rowHeight": RowHeight = Mathf.Max(10, F(RowHeight)); break;
                    case "titleHeight": TitleHeight = Mathf.Max(10, F(TitleHeight)); break;
                    case "fontSize": FontSize = Mathf.Max(6, F(FontSize)); break;
                    case "titleFontSize": TitleFontSize = Mathf.Max(6, F(TitleFontSize)); break;
                    case "maxRows": MaxRows = Mathf.Clamp((int)F(MaxRows), 1, 20); break;
                    case "x": X = F(X); break;
                    case "y": Y = F(Y); break;
                    case "background": Back = C(Back); break;
                    case "titleColour": TitleColour = C(TitleColour); break;
                    case "textColour": TextColour = C(TextColour); break;
                    case "barYou": BarYou = C(BarYou); break;
                    case "barTarget": BarTarget = C(BarTarget); break;
                    case "barOthers": BarOther = C(BarOther); break;
                }
            }
            if (_instance != null)
            {
                var shown = _instance.gameObject.activeSelf;
                Destroy(_instance.gameObject);
                _instance = null;
                if (shown && _lastRows != null) Show(_lastMonster, _lastRows);
            }
        }

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
            Show(monster, rows);
        }

        private static void Show(string monster, List<Row> rows)
        {
            _lastMonster = monster;
            _lastRows = rows;
            if (_instance == null)
            {
                var root = new GameObject("CHAggroMeter", typeof(RectTransform));
                DontDestroyOnLoad(root);
                _instance = root.AddComponent<AggroMeter>();
                _instance.Build();
            }
            _instance.gameObject.SetActive(true);
            _instance.Fill(monster, rows);
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
            _panel.anchoredPosition = new Vector2(X, Y);

            _title = Text("Title", _panel, font, TitleFontSize, TextAlignmentOptions.Left);
            var tr = _title.rectTransform;
            tr.anchorMin = new Vector2(0, 1);
            tr.anchorMax = new Vector2(1, 1);
            tr.pivot = new Vector2(0.5f, 1);
            tr.offsetMin = new Vector2(10, -TitleHeight);
            tr.offsetMax = new Vector2(-10, -4);
            _title.color = TitleColour;

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

                var name = Text("Name", rr, font, FontSize, TextAlignmentOptions.Left);
                Stretch(name.rectTransform, 6, -70);
                var value = Text("Value", rr, font, FontSize, TextAlignmentOptions.Right);
                Stretch(value.rectTransform, 0, -6);
                name.color = value.color = TextColour;
                _rows.Add((br, bar, name, value, rowGo));
            }
        }

        private void Fill(string monster, List<Row> rows)
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

        private static void Stretch(RectTransform r, float left, float right)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(left, 0);
            r.offsetMax = new Vector2(right, 0);
        }
    }
}
