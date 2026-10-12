using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CHPrivateMods
{
    /// <summary>
    /// The passive tree panel (docs/passive-tree.md). The server owns the tree and its rules; this only draws what the
    /// server sends (TreeLayout 31000 and TreeState 31001, read at the start of NetworkManager.processCommand, patch P14)
    /// and sends <c>.tree …</c> chat commands back. A "Passives" button joins the HUD options menu (patch P15).
    /// </summary>
    public static class PassiveTree
    {
        private const uint LayoutOpcode = 31000, StateOpcode = 31001;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        internal sealed class Node
        {
            public int Id;
            public int Kind; // 0 start, 1 small, 2 notable, 3 keystone
            public string Name;
            public Vector2 Position;
            public int Class;
            public int[] Links;
            public string[] Lines;
        }

        internal static string Version;
        internal static Dictionary<int, Node> Nodes;
        internal static HashSet<int> Owned = new HashSet<int>();
        internal static int Start, PointsFree, PointsTotal;
        internal static string[] Summary = new string[0];

        private static PropertyInfo _position;
        private static MethodInfo _readVarUInt, _readVarInt, _readString, _readByte;

        /// <summary>Called at the start of NetworkManager.processCommand(msg) (patch P14). Always leaves the read position as it was.</summary>
        public static void OnMessage(object msg)
        {
            if (msg == null) return;
            object start = null;
            try
            {
                if (_position == null) Bind(msg.GetType());
                start = _position.GetValue(msg);
                var opcode = (uint)_readVarUInt.Invoke(msg, null);
                if (opcode == LayoutOpcode) ReadLayout(msg);
                else if (opcode == StateOpcode) ReadState(msg);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] passive tree message: " + e);
            }
            finally
            {
                try { if (start != null) _position.SetValue(msg, start); } catch (Exception) { }
            }
        }

        private static void Bind(Type type)
        {
            _position = type.GetProperty("Position", Any);
            _readVarUInt = type.GetMethod("ReadVariableUInt32", Any, null, Type.EmptyTypes, null);
            _readVarInt = type.GetMethod("ReadVariableInt32", Any, null, Type.EmptyTypes, null);
            _readString = type.GetMethod("ReadString", Any, null, Type.EmptyTypes, null);
            _readByte = type.GetMethod("ReadByte", Any, null, Type.EmptyTypes, null);
        }

        private static int Int(object m) => (int)_readVarInt.Invoke(m, null);
        private static string Str(object m) => (string)_readString.Invoke(m, null);
        private static byte Byte(object m) => (byte)_readByte.Invoke(m, null);

        private static void ReadLayout(object m)
        {
            var version = Str(m);
            var nodes = new Dictionary<int, Node>();
            for (int i = 0, n = Int(m); i < n; i++)
            {
                var node = new Node { Id = Int(m), Kind = Byte(m), Name = Str(m) };
                var x = Int(m);
                var y = Int(m);
                node.Position = new Vector2(x, -y); // the layout's y grows downwards
                node.Class = Int(m);
                node.Links = new int[Int(m)];
                for (var l = 0; l < node.Links.Length; l++) node.Links[l] = Int(m);
                node.Lines = new string[Int(m)];
                for (var l = 0; l < node.Lines.Length; l++) node.Lines[l] = Str(m);
                nodes[node.Id] = node;
            }
            Nodes = nodes;
            Version = version;
            if (TreePanel.Instance != null) TreePanel.Instance.Rebuild();
        }

        private static void ReadState(object m)
        {
            var open = Byte(m) == 1;
            var version = Str(m);
            Start = Int(m);
            PointsFree = Int(m);
            PointsTotal = Int(m);
            var owned = new HashSet<int>();
            for (int i = 0, n = Int(m); i < n; i++) owned.Add(Int(m));
            Owned = owned;
            var summary = new string[Int(m)];
            for (var i = 0; i < summary.Length; i++) summary[i] = Str(m);
            Summary = summary;
            if (Nodes == null || version != Version) return; // the layout always comes first; ignore a stale state
            if (open) TreePanel.Show();
            else if (TreePanel.Instance != null) TreePanel.Instance.Refresh();
        }

        /// <summary>Sends a chat line through the game's own NetworkManager.sendChatMessage (LOCAL channel).</summary>
        internal static void Send(string text)
        {
            try
            {
                var controllerType = Type.GetType("CController, Assembly-CSharp");
                var controller = controllerType?.GetField("instance", Any)?.GetValue(null);
                var network = controllerType?.GetField("m_networkManager", Any)?.GetValue(controller);
                var send = network?.GetType().GetMethod("sendChatMessage", Any);
                if (send == null) return;
                var channel = Enum.ToObject(send.GetParameters()[1].ParameterType, 3); // HW_CHAT_BOX_CHANNEL_LOCAL
                send.Invoke(network, new[] { text, channel, "" });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] passive tree command not sent: " + e);
            }
        }

        /// <summary>Called at the end of HUDOptionsScreen.Init (patch P15): a "Passives" button in the options menu.</summary>
        public static void AttachMenuButton(object optionsScreen)
        {
            try
            {
                var type = optionsScreen.GetType();
                var stats = type.GetField("statsButton", Any)?.GetValue(optionsScreen) as Button;
                var freePlat = type.GetField("freePlatButton", Any)?.GetValue(optionsScreen) as Button;
                GameObject target;
                if (freePlat != null && freePlat.gameObject.activeSelf)
                {
                    target = freePlat.gameObject; // free platinum offers don't exist on a private server
                }
                else if (stats != null)
                {
                    target = UnityEngine.Object.Instantiate(stats.gameObject, stats.transform.parent, false);
                    target.transform.SetSiblingIndex(stats.transform.GetSiblingIndex() + 1);
                    if (stats.transform.parent.GetComponent<LayoutGroup>() == null)
                    {
                        var rect = (RectTransform)target.transform;
                        rect.anchoredPosition = ((RectTransform)stats.transform).anchoredPosition - new Vector2(0f, rect.rect.height + 6f);
                    }
                }
                else return;
                target.name = "btnPassiveTree";
                foreach (var component in target.GetComponentsInChildren<Component>(true))
                    if (component != null && component.GetType().Name.IndexOf("Locali", StringComparison.OrdinalIgnoreCase) >= 0)
                        UnityEngine.Object.Destroy(component);
                foreach (Transform child in target.transform)
                    if (child.name.IndexOf("otif", StringComparison.OrdinalIgnoreCase) >= 0 || child.name.IndexOf("park", StringComparison.OrdinalIgnoreCase) >= 0)
                        child.gameObject.SetActive(false);
                foreach (var label in target.GetComponentsInChildren<TMP_Text>(true)) label.text = "Passives";
                foreach (var label in target.GetComponentsInChildren<Text>(true)) label.text = "Passives";
                var button = target.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(() => Send(".tree open"));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] Passives button not added: " + e);
            }
        }
    }

    /// <summary>The full-screen panel: pan by dragging, zoom with the wheel, left-click to take a node, right-click to refund.</summary>
    public sealed class TreePanel : MonoBehaviour
    {
        internal static TreePanel Instance;

        private static readonly Color Backdrop = new Color(0.03f, 0.03f, 0.05f, 0.93f);
        private static readonly Color Taken = new Color(1f, 0.78f, 0.25f);
        private static readonly Color Open = new Color(0.82f, 0.86f, 0.95f);
        private static readonly Color Locked = new Color(0.32f, 0.32f, 0.36f);
        private static readonly Color StartColour = new Color(0.45f, 0.85f, 0.5f);
        private static readonly Color LinkTaken = new Color(1f, 0.78f, 0.25f, 0.95f);
        private static readonly Color LinkOpen = new Color(0.55f, 0.58f, 0.66f, 0.8f);
        private static readonly Color LinkLocked = new Color(0.25f, 0.25f, 0.29f, 0.8f);

        private TMP_FontAsset _font;
        private Sprite _circle;
        private RectTransform _viewport, _content, _lines, _nodes, _tooltip;
        private TextMeshProUGUI _points, _summary, _tooltipText, _resetLabel;
        private readonly Dictionary<int, NodeView> _views = new Dictionary<int, NodeView>();
        private readonly List<(int A, int B, Image Line)> _links = new List<(int, int, Image)>();
        private string _builtVersion;
        private float _resetArmedUntil;
        private NodeView _hovered;
        private bool _wasHtmlOpen;
        private bool _fitPending;

        internal static void Show()
        {
            if (Instance == null)
            {
                var root = new GameObject("CHPassiveTree", typeof(RectTransform));
                DontDestroyOnLoad(root);
                Instance = root.AddComponent<TreePanel>();
                Instance.Build();
            }
            Instance.gameObject.SetActive(true);
            Instance.Rebuild();
            Canvas.ForceUpdateCanvases();
            Instance.Fit();
            Instance._fitPending = true; // the canvas size may only settle next frame
            Instance.BlockWorldInput(true);
        }

        private void Close()
        {
            BlockWorldInput(false);
            gameObject.SetActive(false);
            _tooltip.gameObject.SetActive(false);
        }

        /// <summary>While the panel is open the world ignores clicks and keys (GUIManager.htmlWindowOpen, as the HTML window uses).</summary>
        private void BlockWorldInput(bool block)
        {
            try
            {
                var type = Type.GetType("GUIManager, Assembly-CSharp");
                var manager = type?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                var flag = type?.GetField("htmlWindowOpen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (manager == null || flag == null) return;
                if (block)
                {
                    _wasHtmlOpen = (bool)flag.GetValue(manager);
                    flag.SetValue(manager, true);
                }
                else
                {
                    flag.SetValue(manager, _wasHtmlOpen);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] passive tree input block: " + e);
            }
        }

        private void Build()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            var anyText = FindAnyObjectByType<TMP_Text>();
            _font = anyText != null ? anyText.font : null;
            _circle = MakeCircle(64);

            var backdrop = MakePanel("Backdrop", transform, Backdrop);
            Stretch(backdrop.rectTransform, 0, 0, 0, 0);

            // The tree area, right of the summary column.
            var view = MakePanel("Viewport", transform, new Color(0, 0, 0, 0));
            _viewport = view.rectTransform;
            Stretch(_viewport, 380, 20, 90, 60);
            view.gameObject.AddComponent<RectMask2D>();
            var panZoom = view.gameObject.AddComponent<PanZoom>();
            _content = Child("Content", _viewport);
            _lines = Child("Links", _content);
            _nodes = Child("Nodes", _content);
            panZoom.Content = _content;

            // Header.
            var title = MakeText("Title", transform, "Passive Tree", 40, TextAlignmentOptions.Left);
            Anchor(title.rectTransform, new Vector2(0, 1), new Vector2(400, 20), new Vector2(500, 60));
            _points = MakeText("Points", transform, "", 30, TextAlignmentOptions.Left);
            Anchor(_points.rectTransform, new Vector2(0, 1), new Vector2(700, 30), new Vector2(620, 50));
            var hint = MakeText("Hint", transform, "Click to take  ·  Right-click (or Alt-click) to give back  ·  Drag to move  ·  Wheel to zoom", 20, TextAlignmentOptions.Center);
            hint.color = new Color(0.7f, 0.7f, 0.75f);
            hint.rectTransform.anchorMin = hint.rectTransform.anchorMax = new Vector2(0.5f, 0);
            hint.rectTransform.pivot = new Vector2(0.5f, 0);
            hint.rectTransform.sizeDelta = new Vector2(1300, 40);
            hint.rectTransform.anchoredPosition = new Vector2(180, 12);
            var reset = MakeButton("Reset", transform, "Reset all", () =>
            {
                if (Time.unscaledTime < _resetArmedUntil)
                {
                    PassiveTree.Send(".tree reset");
                    _resetArmedUntil = 0;
                }
                else
                {
                    _resetArmedUntil = Time.unscaledTime + 3f;
                }
            });
            _resetLabel = reset.GetComponentInChildren<TextMeshProUGUI>();
            Anchor((RectTransform)reset.transform, new Vector2(1, 1), new Vector2(-250, 22), new Vector2(170, 52));
            var close = MakeButton("Close", transform, "Close", Close);
            Anchor((RectTransform)close.transform, new Vector2(1, 1), new Vector2(-60, 22), new Vector2(150, 52));

            // Summary column.
            var column = MakePanel("Summary", transform, new Color(0.08f, 0.08f, 0.11f, 0.95f));
            var col = column.rectTransform;
            col.anchorMin = new Vector2(0, 0);
            col.anchorMax = new Vector2(0, 1);
            col.offsetMin = new Vector2(20, 20);
            col.offsetMax = new Vector2(360, -90);
            var heading = MakeText("Heading", column.transform, "Your passives", 28, TextAlignmentOptions.TopLeft);
            heading.rectTransform.anchorMin = new Vector2(0, 1);
            heading.rectTransform.anchorMax = new Vector2(1, 1);
            heading.rectTransform.offsetMin = new Vector2(16, -56);
            heading.rectTransform.offsetMax = new Vector2(-16, -12);
            _summary = MakeText("Lines", column.transform, "", 21, TextAlignmentOptions.TopLeft);
            Stretch(_summary.rectTransform, 16, 16, 60, 16);

            // Tooltip.
            var tip = MakePanel("Tooltip", transform, new Color(0.06f, 0.06f, 0.08f, 0.97f));
            tip.raycastTarget = false;
            _tooltip = tip.rectTransform;
            _tooltip.anchorMin = _tooltip.anchorMax = new Vector2(0, 0);
            _tooltip.pivot = new Vector2(0, 1);
            _tooltipText = MakeText("Text", _tooltip, "", 22, TextAlignmentOptions.TopLeft);
            _tooltipText.raycastTarget = false;
            Stretch(_tooltipText.rectTransform, 14, 14, 10, 10);
            _tooltip.gameObject.SetActive(false);
        }

        /// <summary>(Re)creates the nodes and links when the layout changes, then refreshes colours and text.</summary>
        internal void Rebuild()
        {
            if (PassiveTree.Nodes == null) return;
            if (_builtVersion != PassiveTree.Version)
            {
                foreach (Transform child in _lines) Destroy(child.gameObject);
                foreach (Transform child in _nodes) Destroy(child.gameObject);
                _views.Clear();
                _links.Clear();
                foreach (var node in PassiveTree.Nodes.Values)
                {
                    foreach (var other in node.Links)
                    {
                        if (other < node.Id || !PassiveTree.Nodes.TryGetValue(other, out var b)) continue;
                        _links.Add((node.Id, other, Line(node.Position, b.Position)));
                    }
                }
                foreach (var node in PassiveTree.Nodes.Values) _views[node.Id] = NodeObject(node);
                _builtVersion = PassiveTree.Version;
            }
            Refresh();
        }

        internal void Refresh()
        {
            if (PassiveTree.Nodes == null) return;
            var owned = PassiveTree.Owned;
            bool Has(int id) => id == PassiveTree.Start || owned.Contains(id);
            foreach (var view in _views.Values)
            {
                var node = view.Node;
                Color colour;
                if (node.Id == PassiveTree.Start) colour = StartColour;
                else if (node.Kind == 0) colour = Locked; // other classes' starts
                else if (owned.Contains(node.Id)) colour = Taken;
                else if (PassiveTree.PointsFree > 0 && Array.Exists(node.Links, Has)) colour = Open;
                else colour = Locked;
                view.Ring.color = colour;
                view.Fill.color = Color.Lerp(colour, Color.black, owned.Contains(node.Id) || node.Id == PassiveTree.Start ? 0.35f : 0.75f);
            }
            foreach (var (a, b, line) in _links)
                line.color = Has(a) && Has(b) ? LinkTaken : Has(a) || Has(b) ? LinkOpen : LinkLocked;
            _points.text = "Points: " + PassiveTree.PointsFree + " free of " + PassiveTree.PointsTotal + "  (one every 5 levels)";
            _summary.text = PassiveTree.Summary.Length == 0 ? "None yet." : string.Join("\n", PassiveTree.Summary);
            if (_hovered != null) ShowTooltip(_hovered);
        }

        /// <summary>Zooms so the whole tree fits the area, centred.</summary>
        private void Fit()
        {
            if (PassiveTree.Nodes == null || PassiveTree.Nodes.Count == 0) return;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var n in PassiveTree.Nodes.Values)
            {
                minX = Mathf.Min(minX, n.Position.x); maxX = Mathf.Max(maxX, n.Position.x);
                minY = Mathf.Min(minY, n.Position.y); maxY = Mathf.Max(maxY, n.Position.y);
            }
            var size = _viewport.rect.size;
            var scale = Mathf.Clamp(Mathf.Min(size.x / (maxX - minX + 160), size.y / (maxY - minY + 160)), 0.2f, 2f);
            _content.localScale = Vector3.one * scale;
            _content.anchoredPosition = -new Vector2((minX + maxX) / 2, (minY + maxY) / 2) * scale;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
                return;
            }
            if (_fitPending)
            {
                _fitPending = false;
                Fit();
            }
            if (_resetLabel != null) _resetLabel.text = Time.unscaledTime < _resetArmedUntil ? "Click to confirm" : "Reset all";
            if (_tooltip.gameObject.activeSelf)
            {
                var scale = GetComponent<Canvas>().scaleFactor;
                var pos = (Vector2)Input.mousePosition / scale + new Vector2(24, -16);
                var size = _tooltip.sizeDelta;
                var screen = new Vector2(Screen.width, Screen.height) / scale;
                if (pos.x + size.x > screen.x) pos.x -= size.x + 48;
                if (pos.y - size.y < 0) pos.y = size.y;
                _tooltip.anchoredPosition = pos;
            }
        }

        internal void Clicked(NodeView view, PointerEventData.InputButton button)
        {
            var id = view.Node.Id;
            var refund = button == PointerEventData.InputButton.Right || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (refund) { if (PassiveTree.Owned.Contains(id)) PassiveTree.Send(".tree refund " + id); }
            else if (!PassiveTree.Owned.Contains(id) && view.Node.Kind != 0) PassiveTree.Send(".tree alloc " + id);
        }

        internal void Hover(NodeView view, bool enter)
        {
            _hovered = enter ? view : _hovered == view ? null : _hovered;
            if (_hovered == null) _tooltip.gameObject.SetActive(false);
            else ShowTooltip(_hovered);
        }

        private void ShowTooltip(NodeView view)
        {
            var node = view.Node;
            var kind = node.Kind == 3 ? "Keystone" : node.Kind == 2 ? "Notable" : node.Kind == 0 ? "Start" : "Passive";
            var state = PassiveTree.Owned.Contains(node.Id) ? "<color=#ffc840>Taken</color> (right-click to give back)"
                : node.Kind == 0 ? "" : "Click to take";
            _tooltipText.text = "<size=26><b>" + node.Name + "</b></size>\n<color=#9aa0b0>" + kind + "</color>\n" + string.Join("\n", node.Lines)
                + (state.Length > 0 ? "\n<size=18>" + state + "</size>" : "");
            _tooltipText.ForceMeshUpdate();
            var pref = _tooltipText.GetPreferredValues(_tooltipText.text, 420, 0);
            _tooltip.sizeDelta = new Vector2(Mathf.Min(450, pref.x + 28), pref.y + 22);
            _tooltip.gameObject.SetActive(true);
            _tooltip.SetAsLastSibling();
        }

        // ---- building blocks -------------------------------------------------------------------------------------

        private NodeView NodeObject(PassiveTree.Node node)
        {
            var size = node.Kind == 3 ? 76f : node.Kind == 2 ? 56f : node.Kind == 0 ? 64f : 34f;
            var go = new GameObject("Node" + node.Id, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_nodes, false);
            rect.sizeDelta = new Vector2(size, size);
            rect.anchoredPosition = node.Position;
            var ring = go.AddComponent<Image>();
            ring.sprite = _circle;
            var fillGo = new GameObject("Fill", typeof(RectTransform));
            var fillRect = (RectTransform)fillGo.transform;
            fillRect.SetParent(rect, false);
            Stretch(fillRect, 5, 5, 5, 5);
            var fill = fillGo.AddComponent<Image>();
            fill.sprite = _circle;
            fill.raycastTarget = false;
            if (node.Kind >= 2)
            {
                var label = MakeText("Label", rect, node.Name, node.Kind == 3 ? 22 : 18, TextAlignmentOptions.Center);
                label.raycastTarget = false;
                label.rectTransform.sizeDelta = new Vector2(220, 30);
                label.rectTransform.anchoredPosition = new Vector2(0, -size / 2 - 16);
            }
            var view = go.AddComponent<NodeView>();
            view.Node = node;
            view.Ring = ring;
            view.Fill = fill;
            view.Panel = this;
            return view;
        }

        private Image Line(Vector2 a, Vector2 b)
        {
            var go = new GameObject("Link", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_lines, false);
            var delta = b - a;
            rect.pivot = new Vector2(0, 0.5f);
            rect.sizeDelta = new Vector2(delta.magnitude, 6f);
            rect.anchoredPosition = a;
            rect.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            var image = go.AddComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        private static Sprite MakeCircle(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            var r = size / 2f;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                var a = Mathf.Clamp01(r - d);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static RectTransform Child(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = Vector2.zero;
            return rect;
        }

        private static Image MakePanel(string name, Transform parent, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = colour;
            return image;
        }

        private TextMeshProUGUI MakeText(string name, Transform parent, string text, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            t.fontSize = size;
            t.alignment = align;
            t.color = Color.white;
            t.richText = true;
            t.enableWordWrapping = true;
            t.text = text;
            return t;
        }

        private GameObject MakeButton(string name, Transform parent, string label, Action onClick)
        {
            var image = MakePanel(name, parent, new Color(0.22f, 0.2f, 0.16f, 1f));
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => onClick());
            var text = MakeText("Label", image.transform, label, 24, TextAlignmentOptions.Center);
            Stretch(text.rectTransform, 4, 4, 4, 4);
            return image.gameObject;
        }

        /// <summary>Anchors stretched to the parent with the given insets (−1 leaves that side alone).</summary>
        private static void Stretch(RectTransform rect, float left, float right, float top, float bottom)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left < 0 ? rect.offsetMin.x : left, bottom < 0 ? rect.offsetMin.y : bottom);
            rect.offsetMax = new Vector2(right < 0 ? rect.offsetMax.x : -right, top < 0 ? rect.offsetMax.y : -top);
        }

        /// <summary>A fixed-size box at a corner: <paramref name="offset"/> is from that corner (y measured downwards).</summary>
        private static void Anchor(RectTransform rect, Vector2 corner, Vector2 offset, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = corner;
            rect.pivot = new Vector2(corner.x, 1);
            rect.sizeDelta = size;
            rect.anchoredPosition = new Vector2(offset.x, -offset.y);
        }
    }

    /// <summary>A node: reports clicks and hovers to the panel.</summary>
    public sealed class NodeView : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        internal PassiveTree.Node Node;
        internal Image Ring, Fill;
        internal TreePanel Panel;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!eventData.dragging) Panel.Clicked(this, eventData.button);
        }

        public void OnPointerEnter(PointerEventData eventData) => Panel.Hover(this, true);

        public void OnPointerExit(PointerEventData eventData) => Panel.Hover(this, false);
    }

    /// <summary>Drag to move the tree, mouse wheel to zoom around the pointer.</summary>
    public sealed class PanZoom : MonoBehaviour, IDragHandler, IScrollHandler
    {
        internal RectTransform Content;

        public void OnDrag(PointerEventData eventData)
        {
            var canvas = GetComponentInParent<Canvas>();
            Content.anchoredPosition += eventData.delta / (canvas != null ? canvas.scaleFactor : 1f);
        }

        public void OnScroll(PointerEventData eventData)
        {
            var viewport = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, eventData.position, eventData.pressEventCamera, out var pointer)) return;
            var old = Content.localScale.x;
            var scale = Mathf.Clamp(old * (1f + eventData.scrollDelta.y * 0.1f), 0.2f, 2.5f);
            // Keep the point under the pointer still while zooming.
            var before = (pointer - Content.anchoredPosition) / old;
            Content.localScale = Vector3.one * scale;
            Content.anchoredPosition = pointer - before * scale;
        }
    }
}
