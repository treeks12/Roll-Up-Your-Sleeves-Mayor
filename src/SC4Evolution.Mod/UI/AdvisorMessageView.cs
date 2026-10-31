using System;
using ColossalFramework.UI;
using SC4Evolution.Core;
using SC4Evolution.Mod.Runtime;
using UnityEngine;

namespace SC4Evolution.Mod.UI
{
    /// <summary>Owns a nonmodal advisor notice; portrait textures and native style assets are borrowed.</summary>
    internal sealed class AdvisorMessageView
    {
        private const string Prefix = "SC4EvolutionAdvisorMessage";
        private const float Padding = 16f;
        private readonly UIView _view;
        private readonly UIPanel _panel;
        private readonly UITextureSprite _surface;
        private readonly RectOffset _surfaceBorder;
        private Texture2D _surfaceTexture;
        private readonly UITextureSprite _portrait;
        private readonly UIPanel _thoughtBubble;
        private readonly UILabel _thoughtDots;
        private readonly UIScrollablePanel _text;
        private readonly UIScrollbar _scrollbar;
        private readonly UIPanel _track, _thumb;
        private readonly UILabel _name, _department, _mood, _title, _metric, _body;
        private readonly UIButton _close, _action;
        private AdvisorBriefing _briefing;
        private AdvisorAction _effectiveAction;
        private Action<AdvisorAction> _onAction;
        private bool _released;
        private bool _needsLayout = true;

        internal UIPanel Panel { get { return _panel; } }
        internal UITextureSprite Portrait { get { return _portrait; } }
        internal UIButton CloseButton { get { return _close; } }
        internal UIButton ActionButton { get { return _action; } }
        internal AdvisorBriefing Briefing { get { return CloneBriefing(_briefing); } }
        internal bool IsVisible { get { return !_released && _panel != null && _panel.isVisibleSelf; } }

        internal AdvisorMessageView(UIView view, UIPanel cityStyle, UIButton buttonStyle, UILabel labelStyle)
        {
            if (view == null) throw new ArgumentNullException("view");
            if (cityStyle == null) throw new ArgumentNullException("cityStyle");
            if (buttonStyle == null) throw new ArgumentNullException("buttonStyle");
            if (labelStyle == null) throw new ArgumentNullException("labelStyle");
            _view = view;
            try
            {
                _panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
                if (_panel == null) throw new InvalidOperationException("Native UIView did not create the advisor panel.");
                _panel.name = Prefix;
                _panel.atlas = cityStyle.atlas;
                _panel.backgroundSprite = cityStyle.backgroundSprite;
                _panel.color = cityStyle.color;
                _panel.opacity = 1f;
                _panel.autoLayout = false;
                _panel.clipChildren = true;
                _panel.isInteractive = true;
                _panel.canFocus = false;
                _panel.isVisible = false;

                // The city panel's native sprite has translucent interior pixels. Keep its
                // native border, with an opaque owned interior before all content siblings.
                UITextureAtlas.SpriteInfo background = cityStyle.atlas == null ? null
                    : cityStyle.atlas[cityStyle.backgroundSprite];
                RectOffset border = background == null ? null : background.border;
                // Sprite slicing also reserves a tall header; keep only the outer rim
                // transparent so every content row, including identification, is opaque.
                _surfaceBorder = border == null || (border.horizontal == 0 && border.vertical == 0)
                    ? new RectOffset(6, 6, 6, 6)
                    : new RectOffset(Math.Min(6, Math.Max(0, border.left)), Math.Min(6, Math.Max(0, border.right)),
                        Math.Min(6, Math.Max(0, border.top)), Math.Min(6, Math.Max(0, border.bottom)));
                _surface = _panel.AddUIComponent<UITextureSprite>();
                _surface.name = Prefix + "Surface";
                _surface.material = view.defaultAtlas.material;
                _surface.color = Color.white;
                _surface.opacity = 1f;
                _surface.isInteractive = false;
                _surface.canFocus = false;
                _surfaceTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                _surfaceTexture.name = Prefix + ".OpaqueSurface";
                _surfaceTexture.wrapMode = TextureWrapMode.Clamp;
                _surfaceTexture.filterMode = FilterMode.Point;
                _surfaceTexture.SetPixel(0, 0, new Color32(64, 64, 64, 255));
                _surfaceTexture.Apply();
                _surface.texture = _surfaceTexture;

                _text = _panel.AddUIComponent<UIScrollablePanel>();
                _text.name = Prefix + "Scroll";
                _text.autoLayout = false;
                _text.autoReset = false;
                _text.clipChildren = true;
                _text.scrollWheelDirection = UIOrientation.Vertical;
                _text.scrollWheelAmount = 24;
                _text.builtinKeyNavigation = true; // Native wheel scrolling uses this gate; canFocus stays false.
                _text.isInteractive = true;
                _text.canFocus = false;
                _text.scrollPadding = new RectOffset(0, 0, 0, 6);

                _name = Label("Name", labelStyle);
                _department = Label("Department", labelStyle);
                _mood = Label("Mood", labelStyle);
                _title = Label("Title", labelStyle);
                _title.textScale = labelStyle.textScale * 1.12f;
                _metric = Label("Metric", labelStyle);
                _body = Label("Body", labelStyle);

                _portrait = _panel.AddUIComponent<FramedAdvisorPortrait>();
                _portrait.name = Prefix + "Portrait";
                _portrait.material = view.defaultAtlas.material;
                _portrait.isInteractive = false;
                _portrait.canFocus = false;
                _portrait.isVisible = false;

                // SC4's advisors sometimes interrupt with a small thought bubble.
                // Reuse the game's own button atlas so the marker belongs to the
                // native UI family and does not introduce a new texture asset.
                _thoughtBubble = _panel.AddUIComponent<UIPanel>();
                _thoughtBubble.name = Prefix + "ThoughtBubble";
                _thoughtBubble.atlas = buttonStyle.atlas;
                _thoughtBubble.backgroundSprite = buttonStyle.normalBgSprite;
                _thoughtBubble.color = buttonStyle.color;
                _thoughtBubble.opacity = 0.96f;
                _thoughtBubble.isInteractive = false;
                _thoughtBubble.canFocus = false;
                _thoughtBubble.clipChildren = true;
                _thoughtBubble.isVisible = false;
                _thoughtDots = _thoughtBubble.AddUIComponent<UILabel>();
                _thoughtDots.name = Prefix + "ThoughtDots";
                _thoughtDots.autoSize = false;
                _thoughtDots.size = new Vector2(44f, 22f);
                _thoughtDots.font = labelStyle.font;
                _thoughtDots.textScale = labelStyle.textScale * 1.1f;
                _thoughtDots.textColor = labelStyle.textColor;
                _thoughtDots.textAlignment = UIHorizontalAlignment.Center;
                _thoughtDots.verticalAlignment = UIVerticalAlignment.Middle;
                _thoughtDots.text = "• • •";
                _thoughtDots.isInteractive = false;
                _thoughtDots.canFocus = false;

                _scrollbar = _panel.AddUIComponent<UIScrollbar>();
                _scrollbar.name = Prefix + "Scrollbar";
                _scrollbar.orientation = UIOrientation.Vertical;
                _scrollbar.autoHide = true;
                _scrollbar.incrementAmount = 24f;
                _scrollbar.isInteractive = true;
                _scrollbar.canFocus = false;
                _track = _scrollbar.AddUIComponent<UIPanel>();
                _track.name = Prefix + "ScrollTrack";
                _track.atlas = cityStyle.atlas;
                _track.backgroundSprite = cityStyle.backgroundSprite;
                _track.color = cityStyle.color;
                _track.isInteractive = true;
                _track.canFocus = false;
                _thumb = _track.AddUIComponent<UIPanel>();
                _thumb.name = Prefix + "ScrollThumb";
                _thumb.atlas = buttonStyle.atlas;
                _thumb.backgroundSprite = buttonStyle.normalBgSprite;
                _thumb.color = buttonStyle.color;
                _thumb.isInteractive = true;
                _thumb.canFocus = false;
                _scrollbar.trackObject = _track;
                _scrollbar.thumbObject = _thumb;
                _text.verticalScrollbar = _scrollbar;

                _close = Button("Close", GameLanguage.Text("Fechar", "Close"), buttonStyle);
                _close.eventClicked += CloseClicked;
                _action = Button("Action", GameLanguage.Text("Abrir visualização", "Open view"), buttonStyle);
                _action.eventClicked += ActionClicked;
            }
            catch
            {
                ReleaseSurface();
                if (_panel != null)
                {
                    if (_panel.parent != null) _panel.parent.RemoveUIComponent(_panel);
                    UnityEngine.Object.Destroy(_panel.gameObject);
                }
                throw;
            }
        }

        internal bool Show(AdvisorBriefing briefing, Texture2D borrowedPortrait, AdvisorAction effectiveAction,
            bool actionAvailable, Action<AdvisorAction> onAction)
        {
            if (_released || _panel == null || briefing == null || !ModRuntime.IsLoaded || UIView.HasModalInput()) return false;
            Vector2 size; Vector3 position;
            if (!TryBounds(out size, out position)) { Close(); return false; }
            AdvisorBriefing current = CloneBriefing(briefing);
            _briefing = current;
            _effectiveAction = effectiveAction;
            _onAction = onAction;
            SetText(_name, current.Name);
            SetText(_department, current.Department);
            SetText(_mood, MoodText(current.Mood));
            SetText(_title, current.Title);
            SetText(_metric, current.MetricText);
            string message = current.Message ?? string.Empty;
            if (!string.IsNullOrEmpty(current.Detail) && !string.Equals(message, current.Detail, StringComparison.Ordinal))
                message = message.Length == 0 ? current.Detail : message + "\n\n" + current.Detail;
            SetText(_body, message);
            ((FramedAdvisorPortrait)_portrait).SetFraming(current.Advisor, current.Mood);
            _portrait.texture = borrowedPortrait;
            _portrait.isVisible = borrowedPortrait != null;
            _thoughtBubble.isVisible = current.Severity >= IssueSeverity.Notice && !current.IsSuspended;
            _action.text = GameLanguage.Text("Abrir ", "Open ") + ActionText(effectiveAction);
            _action.isEnabled = actionAvailable && onAction != null && Enum.IsDefined(typeof(AdvisorAction), effectiveAction);
            _action.tooltip = _action.isEnabled ? string.Empty : GameLanguage.Text("Visualização indisponível no momento.", "This view is not available right now.");
            _text.scrollPosition = Vector2.zero;
            _needsLayout = true;
            _panel.isVisible = true;
            RefreshLayout();
            if (!IsVisible) return false;
            _panel.BringToFront(); // Only the owned notice changes its sibling order.
            return true;
        }

        internal void Close()
        {
            if (_panel != null) _panel.isVisible = false;
            if (_portrait != null) { _portrait.texture = null; _portrait.isVisible = false; }
            if (_thoughtBubble != null) _thoughtBubble.isVisible = false;
            _briefing = null;
            _onAction = null;
            if (_action != null) _action.isEnabled = false;
        }

        internal void Animate(Texture2D texture)
        {
            if (!_released && IsVisible && texture != null && _portrait != null) _portrait.texture = texture;
        }

        internal void RefreshLayout()
        {
            if (_released || _panel == null || _briefing == null) return;
            if (!ModRuntime.IsLoaded) { Close(); return; }
            Vector2 size; Vector3 position;
            if (!TryBounds(out size, out position)) { Close(); return; }
            if (!_needsLayout && _panel.size == size && _panel.absolutePosition == position) return;
            _panel.size = size;
            _panel.absolutePosition = position;
            Place(_surface, _surfaceBorder.left, _surfaceBorder.top,
                Mathf.Max(0f, size.x - _surfaceBorder.horizontal),
                Mathf.Max(0f, size.y - _surfaceBorder.vertical));
            float viewportHeight = size.y - Padding * 2f - 44f;
            float portraitX = size.x - Padding - 128f;
            float textArea = portraitX - 12f - Padding;
            Place(_text, Padding, Padding, textArea - 16f, viewportHeight);
            Place(_scrollbar, Padding + textArea - 12f, Padding, 12f, viewportHeight);
            Place(_track, 0f, 0f, 12f, viewportHeight);
            _thumb.width = 12f;
            _thumb.height = Mathf.Max(20f, _thumb.height);
            Texture texture = _portrait.texture;
            float aspect = texture == null || texture.height <= 0 ? 1f : (float)texture.width / texture.height;
            float portraitWidth = aspect >= 1f ? 128f : 128f * aspect;
            float portraitHeight = aspect >= 1f ? 128f / aspect : 128f;
            Place(_portrait, portraitX + (128f - portraitWidth) * 0.5f, Padding, portraitWidth, portraitHeight);
            // Keep the small interruption marker attached to the advisor portrait;
            // it never changes the text viewport or the native action controls.
            Place(_thoughtBubble, portraitX - 38f, Padding + Mathf.Max(54f, portraitHeight - 26f), 44f, 24f);
            Place(_thoughtDots, 0f, 0f, 44f, 24f);
            _thoughtBubble.BringToFront();
            Place(_close, size.x - Padding - 100f, size.y - Padding - 28f, 100f, 28f);
            Place(_action, Padding, size.y - Padding - 28f, Mathf.Min(260f, size.x - Padding * 2f - 112f), 28f);

            Vector2 previousScroll = _text.scrollPosition;
            _text.scrollPosition = Vector2.zero;
            float y = 0f;
            y = PlaceText(_name, y, _text.width, 3f);
            y = PlaceText(_department, y, _text.width, 3f);
            y = PlaceText(_mood, y, _text.width, 10f);
            y = PlaceText(_title, y, _text.width, 8f);
            y = PlaceText(_metric, y, _text.width, 8f);
            PlaceText(_body, y, _text.width, 6f);
            _text.scrollPosition = previousScroll;
            _text.Invalidate();
            _needsLayout = false;
        }

        internal void Release()
        {
            if (_released) return;
            Close();
            _released = true;
            if (_close != null) _close.eventClicked -= CloseClicked;
            if (_action != null) _action.eventClicked -= ActionClicked;
            ReleaseSurface();
            if (_panel != null)
            {
                if (_panel.parent != null) _panel.parent.RemoveUIComponent(_panel);
                UnityEngine.Object.Destroy(_panel.gameObject);
            }
        }

        private void ReleaseSurface()
        {
            if (_surface != null) _surface.texture = null;
            if (_surfaceTexture != null)
            {
                UnityEngine.Object.Destroy(_surfaceTexture);
                _surfaceTexture = null;
            }
        }

        private bool TryBounds(out Vector2 size, out Vector3 position)
        {
            size = Vector2.zero; position = Vector3.zero;
            if (_view == null) return false;
            Vector2 resolution = _view.GetScreenResolution();
            if (!Finite(resolution.x) || !Finite(resolution.y)) return false;
            float width = Mathf.Min(660f, resolution.x - 24f);
            float height = Mathf.Min(360f, resolution.y - 172f);
            if (width < 384f || height < 260f) return false;
            size = new Vector2(width, height);
            position = new Vector3(Mathf.Max(12f, (resolution.x - width) * 0.5f),
                Mathf.Clamp(resolution.y - height - 112f, 48f, resolution.y - height - 12f));
            return true;
        }

        private UILabel Label(string suffix, UILabel style)
        {
            UILabel label = _text.AddUIComponent<UILabel>();
            label.name = Prefix + suffix;
            label.autoSize = false;
            label.size = new Vector2(300f, 22f);
            label.font = style.font;
            label.textScale = style.textScale;
            label.textColor = style.textColor;
            label.disabledTextColor = style.disabledTextColor;
            label.useDropShadow = style.useDropShadow;
            label.dropShadowColor = style.dropShadowColor;
            label.dropShadowOffset = style.dropShadowOffset;
            label.padding = new RectOffset(style.padding.left, style.padding.right, style.padding.top, style.padding.bottom);
            label.wordWrap = true;
            label.processMarkup = false;
            label.isInteractive = false;
            label.canFocus = false;
            label.autoHeight = true;
            // Native scroll Reset includes invisible active labels in its minimum position.
            // Empty labels may skip PlaceText, so do not retain AddUIComponent's initial offset.
            label.relativePosition = Vector3.zero;
            return label;
        }
        private UIButton Button(string suffix, string text, UIButton style)
        {
            UIButton button = _panel.AddUIComponent<UIButton>();
            button.name = Prefix + suffix;
            button.autoSize = false;
            button.font = style.font;
            button.atlas = style.atlas;
            button.normalBgSprite = style.normalBgSprite;
            button.hoveredBgSprite = style.hoveredBgSprite;
            button.pressedBgSprite = style.pressedBgSprite;
            button.focusedBgSprite = style.focusedBgSprite;
            button.disabledBgSprite = style.disabledBgSprite;
            button.color = style.color;
            button.hoveredColor = style.hoveredColor;
            button.pressedColor = style.pressedColor;
            button.focusedColor = style.focusedColor;
            button.disabledColor = style.disabledColor;
            button.textScale = style.textScale;
            button.textColor = style.textColor;
            button.hoveredTextColor = style.hoveredTextColor;
            button.pressedTextColor = style.pressedTextColor;
            button.focusedTextColor = style.focusedTextColor;
            button.disabledTextColor = style.disabledTextColor;
            button.textHorizontalAlignment = UIHorizontalAlignment.Center;
            button.textVerticalAlignment = UIVerticalAlignment.Middle;
            button.wordWrap = false;
            button.canFocus = false;
            button.isInteractive = true;
            button.text = text;
            return button;
        }
        private static void SetText(UILabel label, string text)
        { label.text = text ?? string.Empty; label.isVisible = label.text.Length != 0; }
        private static AdvisorBriefing CloneBriefing(AdvisorBriefing source)
        {
            if (source == null) return null;
            return new AdvisorBriefing
            {
                Advisor = source.Advisor,
                Name = source.Name,
                Department = source.Department,
                Title = source.Title,
                Message = source.Message,
                MetricText = source.MetricText,
                Detail = source.Detail,
                Mood = source.Mood,
                Severity = source.Severity,
                Action = source.Action,
                ConcernDays = source.ConcernDays,
                IsSuspended = source.IsSuspended
            };
        }
        private static float PlaceText(UILabel label, float y, float width, float gap)
        {
            if (!label.isVisibleSelf) return y;
            label.relativePosition = new Vector3(0f, y);
            label.width = width;
            label.Invalidate();
            return y + label.height + gap;
        }
        private static void Place(UIComponent component, float x, float y, float width, float height)
        { component.relativePosition = new Vector3(x, y); component.size = new Vector2(width, height); }
        private void CloseClicked(UIComponent component, UIMouseEventParameter e)
        { if (!UIView.HasModalInput()) { Close(); e.Use(); } }
        private void ActionClicked(UIComponent component, UIMouseEventParameter e)
        {
            if (_released || !IsVisible || !ModRuntime.IsLoaded || UIView.HasModalInput() || !_action.isEnabled || _onAction == null) return;
            Action<AdvisorAction> callback = _onAction;
            AdvisorAction action = _effectiveAction;
            e.Use();
            callback(action); // Controller validates, then closes only for an available native destination.
        }
        private static string MoodText(AdvisorMood mood)
        {
            switch (mood)
            {
                case AdvisorMood.Calm: return GameLanguage.Text("Calmo", "Calm");
                case AdvisorMood.Concerned: return GameLanguage.Text("Preocupado", "Concerned");
                case AdvisorMood.Angry: return GameLanguage.Text("Insatisfeito", "Unhappy");
                case AdvisorMood.Pleased: return GameLanguage.Text("Satisfeito", "Pleased");
                default: return string.Empty;
            }
        }
        private static string ActionText(AdvisorAction action)
        {
            switch (action)
            {
                case AdvisorAction.Overview: return GameLanguage.Text("visão geral", "overview");
                case AdvisorAction.Budget: return GameLanguage.Text("orçamento", "budget");
                case AdvisorAction.Education: return GameLanguage.Text("educação", "education");
                case AdvisorAction.Health: return GameLanguage.Text("saúde", "health");
                case AdvisorAction.Electricity: return GameLanguage.Text("energia", "electricity");
                case AdvisorAction.Water: return GameLanguage.Text("água e esgoto", "water and sewage");
                case AdvisorAction.Crime: return GameLanguage.Text("segurança", "safety");
                case AdvisorAction.Pollution: return GameLanguage.Text("poluição", "pollution");
                case AdvisorAction.Traffic: return GameLanguage.Text("trânsito", "traffic");
                case AdvisorAction.Zoning: return GameLanguage.Text("zoneamento", "zoning");
                case AdvisorAction.Garbage: return GameLanguage.Text("lixo", "garbage");
                default: return GameLanguage.Text("visualização", "view");
            }
        }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
