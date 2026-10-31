using System;
using System.Globalization;
using ColossalFramework;
using ColossalFramework.UI;
using SC4Evolution.Mod.Civic;
using SC4Evolution.Mod.Runtime;
using UnityEngine;

namespace SC4Evolution.Mod.UI
{
    /// <summary>An additive child of the selected native service panel. Buffers stay on SIM.</summary>
    internal sealed class NativeCivicFundingUI
    {
        private const string Prefix = "SC4EvolutionCivicFunding";
        private readonly UIView _view;
        private readonly object _mailbox = new object();
        private volatile bool _released;
        private volatile int _selectionEpoch;
        private volatile int _selectedBuilding;
        private volatile CivicFundingBridge _selectedBridge;
        private CityServiceWorldInfoPanel _service;
        private UIPanel _host, _dock, _panel;
        private UILabel _title, _department, _capacityHeading, _capacityValue, _coverageHeading, _coverageValue, _effects, _status;
        private UIButton _capacityMinus, _capacityPlus, _capacityInherit, _coverageMinus, _coveragePlus, _coverageInherit;
        private CivicFundingSnapshot _snapshot;
        private Completion _completion;
        private bool _pending, _changedClipping;
        private float _nextPoll;
        private string _feedback, _lastError;

        private sealed class Completion
        {
            internal readonly CivicFundingBridge Bridge;
            internal readonly int Epoch;
            internal readonly ushort Building;
            internal readonly CivicFundingSnapshot Snapshot;
            internal readonly bool Command, Applied;
            internal readonly string Reason;
            internal Completion(CivicFundingBridge bridge, int epoch, ushort building, CivicFundingSnapshot snapshot, bool command, bool applied, string reason)
            { Bridge = bridge; Epoch = epoch; Building = building; Snapshot = snapshot; Command = command; Applied = applied; Reason = reason; }
        }

        internal NativeCivicFundingUI(UIView view) { _view = view; }
        internal UIPanel Panel { get { return _panel; } }
        internal CivicFundingSnapshot Snapshot { get { return _snapshot; } }
        internal bool Pending { get { return _pending; } }
        internal string Status { get { return _lastError ?? (_snapshot == null ? string.Empty : _snapshot.Status); } }
        internal UIButton CapacityMinus { get { return _capacityMinus; } }
        internal UIButton CapacityPlus { get { return _capacityPlus; } }
        internal UIButton CapacityInherit { get { return _capacityInherit; } }
        internal UIButton CoverageMinus { get { return _coverageMinus; } }
        internal UIButton CoveragePlus { get { return _coveragePlus; } }
        internal UIButton CoverageInherit { get { return _coverageInherit; } }

        // MAIN only. Static WorldInfoPanel selection is UI state, not a buffer read.
        internal void Refresh()
        {
            if (_released || _view == null) return;
            CivicFundingBridge bridge = ModRuntime.CurrentCivicFundingBridge;
            CityServiceWorldInfoPanel service = UIView.library == null ? null : UIView.library.Get<CityServiceWorldInfoPanel>("CityServiceWorldInfoPanel");
            UIPanel host = service == null ? null : service.component as UIPanel;
            ushort building = host != null && host.isVisible && ModRuntime.IsLoaded && !UIView.HasModalInput()
                ? WorldInfoPanel.GetCurrentInstanceID().Building : (ushort)0;
            if (bridge == null || !bridge.Enabled || building == 0)
            {
                ChangeSelection(null, 0);
                ReleaseControls(); _service = null; _host = null;
                return;
            }
            if (!ReferenceEquals(host, _host))
            {
                ChangeSelection(null, 0);
                ReleaseControls();
                _service = service; _host = host;
            }
            ChangeSelection(bridge, building);
            ConsumeCompletion();
            if (!_pending && Time.realtimeSinceStartup >= _nextPoll) Queue(false, null, null, null);
            if (_snapshot == null || !_snapshot.Supported)
            {
                ReleaseControls();
                return;
            }
            if (!TryInstall()) return;
            Render();
        }

        private void ChangeSelection(CivicFundingBridge bridge, ushort building)
        {
            if (ReferenceEquals(_selectedBridge, bridge) && _selectedBuilding == building) return;
            _selectionEpoch = unchecked(_selectionEpoch + 1);
            _selectedBridge = bridge; _selectedBuilding = building;
            _snapshot = null; _pending = false; _nextPoll = 0f; _feedback = null; _lastError = null;
            lock (_mailbox) _completion = null;
            Hide();
        }

        // Called on either thread; it deliberately touches no Unity/UI object.
        private bool IsCurrent(CivicFundingBridge bridge, ushort building, int epoch)
        {
            return !_released && bridge != null && bridge.Enabled && ModRuntime.IsLoaded && epoch == _selectionEpoch && building == _selectedBuilding
                && ReferenceEquals(bridge, _selectedBridge) && ReferenceEquals(bridge, ModRuntime.CurrentCivicFundingBridge);
        }

        private void Queue(bool command, CivicFundingSnapshot expected, int? capacity, int? coverage)
        {
            CivicFundingBridge bridge = _selectedBridge;
            ushort building = (ushort)_selectedBuilding;
            int epoch = _selectionEpoch;
            if (_pending || bridge == null || building == 0 || !IsCurrent(bridge, building, epoch)) return;
            _pending = true;
            UpdateButtons();
            try
            {
                Singleton<SimulationManager>.instance.AddAction(delegate
                {
                    if (!IsCurrent(bridge, building, epoch)) return;
                    CivicFundingSnapshot snapshot = null;
                    bool applied = false;
                    string reason = null;
                    try
                    {
                        // Apply itself checks the opaque owner/session/identity and both old overrides.
                        if (command)
                        {
                            if (expected == null || expected.BuildingId != building) reason = Text("A seleção mudou.", "The selection changed.");
                            else applied = bridge.Apply(expected, capacity, coverage, out reason);
                        }
                        if (IsCurrent(bridge, building, epoch)) snapshot = bridge.GetSnapshot(building);
                    }
                    catch (Exception error) { reason = error.GetType().Name + ": " + error.Message; }
                    if (!IsCurrent(bridge, building, epoch)) return;
                    Completion result = new Completion(bridge, epoch, building, snapshot, command, applied, reason);
                    lock (_mailbox) _completion = result;
                });
            }
            catch (Exception error)
            {
                _pending = false; _lastError = error.GetType().Name + ": " + error.Message;
                _nextPoll = Time.realtimeSinceStartup + 2f;
                UpdateButtons();
            }
        }

        private void ConsumeCompletion()
        {
            Completion result;
            lock (_mailbox) { result = _completion; _completion = null; }
            if (result == null || !IsCurrent(result.Bridge, result.Building, result.Epoch)) return;
            _pending = false;
            _snapshot = result.Snapshot != null && result.Snapshot.BuildingId == result.Building ? result.Snapshot : null;
            _lastError = result.Snapshot == null || (result.Command && !result.Applied) ? result.Reason : null;
            _nextPoll = Time.realtimeSinceStartup + (_snapshot == null ? 2f : 1f);
            if (result.Command) _feedback = result.Applied ? Text("Financiamento atualizado.", "Funding updated.") : Text("Alteração não aplicada. Confira a seleção e tente novamente.", "Change not applied. Check the selection and try again.");
        }

        private bool TryInstall()
        {
            if (_panel != null)
            {
                if (_dock != null && ReferenceEquals(_dock.parent, _host) && !_host.autoLayout && !_host.autoFitChildrenHorizontally && !_host.autoFitChildrenVertically) return true;
                ReleaseControls();
            }
            if (_host == null || _host.autoLayout || _host.autoFitChildrenHorizontally || _host.autoFitChildrenVertically) return false;
            UIPanel panelStyle = _service.Find<UIPanel>("Wrapper") ?? _host;
            UILabel labelStyle = _service.Find<UILabel>("Upkeep") ?? _service.Find<UILabel>("Info");
            CityInfoPanel city = CityInfoPanel.instance;
            if (city == null && UIView.library != null) city = UIView.library.Get<CityInfoPanel>("CityInfoPanel");
            UIButton buttonStyle = city == null ? null : city.Find<UIButton>("PoliciesButton");
            if (buttonStyle == null || labelStyle == null) return false;
            try
            {
                // CityServiceWorldInfoPanel.UpdateBindings sets root size from Wrapper.
                // The zero-size direct child stays out of that sizing and layout tree.
                _dock = _host.AddUIComponent<UIPanel>();
                _dock.name = Prefix + "Dock"; _dock.size = Vector2.zero; _dock.relativePosition = Vector3.zero;
                _dock.autoLayout = false; _dock.autoFitChildrenHorizontally = false; _dock.autoFitChildrenVertically = false;
                _dock.clipChildren = false; _dock.isInteractive = false; _dock.canFocus = false;
                _panel = _dock.AddUIComponent<UIPanel>();
                _panel.name = Prefix + "Panel"; _panel.atlas = panelStyle.atlas; _panel.backgroundSprite = panelStyle.backgroundSprite; _panel.color = panelStyle.color;
                _panel.autoLayout = false; _panel.autoFitChildrenHorizontally = false; _panel.autoFitChildrenVertically = false;
                _panel.clipChildren = true; _panel.isInteractive = true; _panel.canFocus = false;
                _title = Label("Title", labelStyle); _title.textScale = Mathf.Max(_title.textScale, 0.95f);
                _department = Label("Department", labelStyle);
                _capacityHeading = Label("CapacityHeading", labelStyle); _capacityHeading.text = Text("Capacidade", "Capacity");
                _capacityValue = Label("CapacityValue", labelStyle); _capacityValue.verticalAlignment = UIVerticalAlignment.Middle; _capacityValue.textAlignment = UIHorizontalAlignment.Center;
                _coverageHeading = Label("CoverageHeading", labelStyle); _coverageHeading.text = Text("Cobertura", "Coverage");
                _coverageValue = Label("CoverageValue", labelStyle); _coverageValue.verticalAlignment = UIVerticalAlignment.Middle; _coverageValue.textAlignment = UIHorizontalAlignment.Center;
                _effects = Label("Effects", labelStyle); _status = Label("Status", labelStyle);
                _capacityMinus = Button("CapacityMinus", "-", buttonStyle); _capacityMinus.eventClicked += CapacityDecrease;
                _capacityPlus = Button("CapacityPlus", "+", buttonStyle); _capacityPlus.eventClicked += CapacityIncrease;
                _capacityInherit = Button("CapacityInherit", Text("Herdar dep.", "Use budget"), buttonStyle); _capacityInherit.eventClicked += CapacityReset;
                _coverageMinus = Button("CoverageMinus", "-", buttonStyle); _coverageMinus.eventClicked += CoverageDecrease;
                _coveragePlus = Button("CoveragePlus", "+", buttonStyle); _coveragePlus.eventClicked += CoverageIncrease;
                _coverageInherit = Button("CoverageInherit", Text("Herdar dep.", "Use budget"), buttonStyle); _coverageInherit.eventClicked += CoverageReset;
                foreach (UIButton reset in new[] { _capacityInherit, _coverageInherit }) reset.tooltip = Text("Voltar ao orçamento do departamento apenas neste eixo.", "Use the department budget for this setting.");
                foreach (UIButton step in new[] { _capacityMinus, _capacityPlus, _coverageMinus, _coveragePlus }) step.tooltip = Text("Alterar o financiamento local em 5 pontos percentuais (0–120%).", "Change local funding by 5 percentage points (0–120%).");
                _capacityHeading.tooltip = _coverageHeading.tooltip = Text("Local: 0–120%. Herdar mantém o orçamento do departamento.", "Local: 0–120%. Use budget follows the department budget.");
                if (_host.clipChildren) { _changedClipping = true; _host.clipChildren = false; }
                _panel.isVisible = false;
                return true;
            }
            catch { ReleaseControls(); throw; }
        }

        private void Render()
        {
            if (_snapshot == null || !_snapshot.Supported || !TryLayout()) { Hide(); return; }
            CivicFundingSnapshot value = _snapshot;
            _title.text = Text("Financiamento local", "Local funding");
            _department.text = string.Format(CultureInfo.CurrentCulture, Text("Departamento: dia {0}% · noite {1}%", "Department: day {0}% · night {1}%"), value.GlobalDayPercent, value.GlobalNightPercent);
            _department.tooltip = string.Format(CultureInfo.CurrentCulture, Text("Departamento agora: {0}% · média nativa: {1}%", "Department now: {0}% · game average: {1}%"), value.GlobalCurrentPercent, value.GlobalAveragePercent);
            _capacityValue.text = Percent(value.CapacityOverridePercent, value.ResolvedCapacityPercent);
            _coverageValue.text = Percent(value.CoverageOverridePercent, value.ResolvedCoveragePercent);
            _effects.text = string.Format(CultureInfo.CurrentCulture, Text("Capacidade alvo: {0:N0} · raio: {1:N0} m", "Target capacity: {0:N0} · radius: {1:N0} m"), value.BudgetTargetCapacity, value.CurrentRange);
            if (value.LastEffectiveCapacity.HasValue) _effects.text += Text("\nÚltima capacidade observada: ", "\nLast observed capacity: ") + value.LastEffectiveCapacity.Value.ToString("N0", CultureInfo.CurrentCulture);
            _effects.tooltip = Text("Os valores observados acompanham o próximo cálculo nativo do serviço.", "Observed values update with the next service calculation.");
            // An unavailable backend is observational: do not present policy projections as active effects.
            bool controls = value.CanApply;
            foreach (UIComponent item in new UIComponent[] { _capacityHeading, _capacityValue, _capacityMinus, _capacityPlus, _capacityInherit,
                _coverageHeading, _coverageValue, _coverageMinus, _coveragePlus, _coverageInherit, _effects }) item.isVisible = controls;
            string text = !controls ? Text("Financiamento local indisponível nesta sessão.", "Local funding is unavailable in this session.") : _pending ? Text("Atualizando...", "Updating...") : _feedback ?? Text("Ajustes locais independentes; o departamento é preservado.", "Local settings are independent of the department budget.");
            _status.text = text; _status.tooltip = text;
            UpdateButtons();
            _panel.isVisible = true;
        }

        private static string Text(string portuguese, string english)
        { return GameLanguage.Text(portuguese, english); }

        private static string Percent(int? local, int resolved)
        { return resolved.ToString(CultureInfo.CurrentCulture) + "%" + (local.HasValue ? string.Empty : " · dep."); }

        private void UpdateButtons()
        {
            if (_capacityMinus == null) return;
            bool active = !_released && !_pending && _snapshot != null && _snapshot.Supported && _snapshot.CanApply;
            int capacity = _snapshot == null ? 0 : _snapshot.ResolvedCapacityPercent;
            int coverage = _snapshot == null ? 0 : _snapshot.ResolvedCoveragePercent;
            _capacityMinus.isEnabled = active && capacity > 0; _capacityPlus.isEnabled = active && capacity < 120;
            _coverageMinus.isEnabled = active && coverage > 0; _coveragePlus.isEnabled = active && coverage < 120;
            _capacityInherit.isEnabled = active && _snapshot.CapacityOverridePercent.HasValue;
            _coverageInherit.isEnabled = active && _snapshot.CoverageOverridePercent.HasValue;
        }

        private void ChangeAxis(bool capacityAxis, int delta, bool inherit)
        {
            if (_released || _pending || _snapshot == null || !_snapshot.Supported || !_snapshot.CanApply || _host == null || !_host.isVisible || UIView.HasModalInput()) return;
            if (WorldInfoPanel.GetCurrentInstanceID().Building != _snapshot.BuildingId) return;
            CivicFundingSnapshot expected = _snapshot;
            int current = capacityAxis ? expected.ResolvedCapacityPercent : expected.ResolvedCoveragePercent;
            int? changed = inherit ? (int?)null : current > 120 && delta < 0 ? 120 : Math.Max(0, Math.Min(120, current + delta));
            int? e = capacityAxis ? changed : expected.CapacityOverridePercent;
            int? c = capacityAxis ? expected.CoverageOverridePercent : changed;
            _feedback = null;
            Queue(true, expected, e, c);
        }
        private void CapacityDecrease(UIComponent component, UIMouseEventParameter e) { e.Use(); ChangeAxis(true, -5, false); }
        private void CapacityIncrease(UIComponent component, UIMouseEventParameter e) { e.Use(); ChangeAxis(true, 5, false); }
        private void CapacityReset(UIComponent component, UIMouseEventParameter e) { e.Use(); ChangeAxis(true, 0, true); }
        private void CoverageDecrease(UIComponent component, UIMouseEventParameter e) { e.Use(); ChangeAxis(false, -5, false); }
        private void CoverageIncrease(UIComponent component, UIMouseEventParameter e) { e.Use(); ChangeAxis(false, 5, false); }
        private void CoverageReset(UIComponent component, UIMouseEventParameter e) { e.Use(); ChangeAxis(false, 0, true); }

        private bool TryLayout()
        {
            Vector2 screen = _view.GetScreenResolution(); Vector3 origin = _host.absolutePosition;
            float hostWidth = _host.width, hostHeight = _host.height;
            if (!Finite(screen.x) || !Finite(screen.y) || !Finite(origin.x) || !Finite(origin.y) || !Finite(hostWidth) || !Finite(hostHeight) || hostWidth <= 0f || hostHeight <= 0f) return false;
            const float gap = 4f, margin = 8f;
            float height = _snapshot != null && _snapshot.CanApply ? 256f : 108f;
            if (screen.y < height + margin * 2f) return false;
            float right = screen.x - margin - (origin.x + hostWidth + gap), left = origin.x - gap - margin;
            float width, x, y;
            if (right >= 320f) { width = Mathf.Min(360f, right); x = origin.x + hostWidth + gap; y = Mathf.Clamp(origin.y, margin, screen.y - height - margin); }
            else if (left >= 320f) { width = Mathf.Min(360f, left); x = origin.x - gap - width; y = Mathf.Clamp(origin.y, margin, screen.y - height - margin); }
            else if (screen.y - margin - origin.y - hostHeight - gap >= height && screen.x - margin * 2f >= 320f)
            { width = Mathf.Min(360f, screen.x - margin * 2f); x = Mathf.Clamp(origin.x, margin, screen.x - margin - width); y = origin.y + hostHeight + gap; }
            else return false;
            if (!Finite(x) || !Finite(y) || !Finite(width) || x < margin || y < margin || x + width > screen.x - margin || y + height > screen.y - margin) return false;
            _panel.size = new Vector2(width, height); _panel.absolutePosition = new Vector3(x, y);
            float content = width - 24f;
            Place(_title, 12f, 10f, content, 22f); Place(_department, 12f, 36f, content, 20f);
            Place(_capacityHeading, 12f, 60f, content, 18f); AxisLayout(_capacityMinus, _capacityValue, _capacityPlus, _capacityInherit, 82f, content);
            Place(_coverageHeading, 12f, 116f, content, 18f); AxisLayout(_coverageMinus, _coverageValue, _coveragePlus, _coverageInherit, 138f, content);
            Place(_effects, 12f, 174f, content, 34f); Place(_status, 12f, height == 256f ? 214f : 64f, content, 32f);
            return true;
        }
        private static void AxisLayout(UIButton minus, UILabel value, UIButton plus, UIButton inherit, float y, float width)
        {
            float reset = Mathf.Min(140f, width - 176f);
            Place(minus, 12f, y, 30f, 26f); Place(value, 48f, y, width - reset - 82f, 26f);
            Place(plus, 12f + width - reset - 34f, y, 30f, 26f); Place(inherit, 12f + width - reset, y, reset, 26f);
        }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static void Place(UIComponent value, float x, float y, float width, float height)
        { value.relativePosition = new Vector3(x, y); value.size = new Vector2(width, height); }
        private void Hide() { if (_panel != null) _panel.isVisible = false; }

        private UILabel Label(string suffix, UILabel style)
        {
            UILabel value = _panel.AddUIComponent<UILabel>(); value.name = Prefix + suffix;
            value.font = style.font; value.textScale = Mathf.Clamp(style.textScale, 0.8f, 0.9f); value.textColor = style.textColor; value.disabledTextColor = style.disabledTextColor;
            value.useDropShadow = style.useDropShadow; value.dropShadowColor = style.dropShadowColor; value.dropShadowOffset = style.dropShadowOffset;
            value.autoSize = false; value.autoHeight = false; value.wordWrap = true; value.processMarkup = false; value.isInteractive = false; value.canFocus = false;
            value.padding = new RectOffset(0, 0, 0, 0); return value;
        }
        private UIButton Button(string suffix, string text, UIButton style)
        {
            UIButton value = _panel.AddUIComponent<UIButton>(); value.name = Prefix + suffix;
            value.atlas = style.atlas; value.font = style.font; value.textScale = Mathf.Clamp(style.textScale, 0.8f, 0.9f);
            value.normalBgSprite = style.normalBgSprite; value.hoveredBgSprite = style.hoveredBgSprite; value.pressedBgSprite = style.pressedBgSprite; value.focusedBgSprite = style.focusedBgSprite; value.disabledBgSprite = style.disabledBgSprite;
            value.color = style.color; value.hoveredColor = style.hoveredColor; value.pressedColor = style.pressedColor; value.focusedColor = style.focusedColor; value.disabledColor = style.disabledColor;
            value.textColor = style.textColor; value.hoveredTextColor = style.hoveredTextColor; value.pressedTextColor = style.pressedTextColor; value.focusedTextColor = style.focusedTextColor; value.disabledTextColor = style.disabledTextColor;
            value.textHorizontalAlignment = UIHorizontalAlignment.Center; value.textVerticalAlignment = UIVerticalAlignment.Middle;
            value.autoSize = false; value.wordWrap = false; value.canFocus = false; value.isInteractive = true; value.text = text; return value;
        }

        private void ReleaseControls()
        {
            bool owned = _dock != null && ReferenceEquals(_dock.parent, _host);
            if (_capacityMinus != null) _capacityMinus.eventClicked -= CapacityDecrease;
            if (_capacityPlus != null) _capacityPlus.eventClicked -= CapacityIncrease;
            if (_capacityInherit != null) _capacityInherit.eventClicked -= CapacityReset;
            if (_coverageMinus != null) _coverageMinus.eventClicked -= CoverageDecrease;
            if (_coveragePlus != null) _coveragePlus.eventClicked -= CoverageIncrease;
            if (_coverageInherit != null) _coverageInherit.eventClicked -= CoverageReset;
            if (_dock != null)
            {
                if (_dock.parent != null) _dock.parent.RemoveUIComponent(_dock);
                UnityEngine.Object.Destroy(_dock.gameObject);
            }
            if (owned && _changedClipping && _host != null && !_host.clipChildren) _host.clipChildren = true;
            _changedClipping = false; _dock = null; _panel = null;
            _capacityMinus = _capacityPlus = _capacityInherit = _coverageMinus = _coveragePlus = _coverageInherit = null;
            _title = _department = _capacityHeading = _capacityValue = _coverageHeading = _coverageValue = _effects = _status = null;
        }
        internal void Release()
        {
            if (_released) return;
            _released = true; _selectionEpoch = unchecked(_selectionEpoch + 1);
            _selectedBridge = null; _selectedBuilding = 0; _snapshot = null; _pending = false;
            lock (_mailbox) _completion = null;
            ReleaseControls(); _service = null; _host = null;
        }
    }
}
