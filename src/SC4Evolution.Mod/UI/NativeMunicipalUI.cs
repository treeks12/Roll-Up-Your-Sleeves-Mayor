using System;
using System.Collections.Generic;
using System.Globalization;
using ColossalFramework;
using ColossalFramework.UI;
using SC4Evolution.Core;
using SC4Evolution.Core.Decisions;
using SC4Evolution.Mod.Runtime;
using UnityEngine;

namespace SC4Evolution.Mod.UI
{
    /// <summary>A council extension of the vanilla city panel, with vanilla controls and navigation.</summary>
    internal sealed class NativeMunicipalUI
    {
        private static readonly string[] Departments = new string[]
        {
            "Finanças", "Urbanismo", "Educação e saúde", "Serviços urbanos",
            "Segurança", "Meio ambiente", "Transportes"
        };

        private readonly UIView _view;
        private readonly Texture2D[] _portraits = new Texture2D[28];
        private readonly bool[] _portraitAttempted = new bool[28];
        private AdvisorPortraitAnimation _portraitAnimation;
        private AdvisorPortraitAnimation _messageAnimation;
        private int _portraitAnimationKey = -1;
        private int _messageAnimationKey = -1;
        private readonly UIButton[] _selectors = new UIButton[7];
        private readonly FramedAdvisorPortrait[] _avatars = new FramedAdvisorPortrait[7];
        private readonly UILabel[] _avatarFallbacks = new UILabel[7];
        private readonly AdvisorBriefing[] _briefings = new AdvisorBriefing[7];
        private readonly UILabel[] _newsTitles = new UILabel[32];
        private readonly UILabel[] _newsTexts = new UILabel[32];
        private readonly AdvisorAlertQueue _alerts = new AdvisorAlertQueue();
        // Gameplay decisions are emitted by the simulation-side DecisionEngine. Keep
        // their presentation state separate from the seven cabinet episodes: the engine
        // owns cooldowns, while this bounded session queue owns delivery/deduplication.
        private const int MaxDecisionPending = 32;
        private const int MaxDecisionSeen = 256;
        private readonly Dictionary<string, DecisionNotice> _decisionPending = new Dictionary<string, DecisionNotice>();
        private readonly HashSet<string> _decisionSeen = new HashSet<string>();
        private readonly Queue<string> _decisionSeenOrder = new Queue<string>();
        private string _decisionCityIdentity;
        private uint _decisionLastFrame;
        private bool _decisionFrameKnown;
        private CityInfoPanel _city;
        private InfoPanel _info;
        private UIPanel _host;
        private UIPanel _dock;
        private UIPanel _panel;
        private UIButton _toggle;
        private UIScrollablePanel _body;
        private UITextureSprite _portrait;
        private UILabel _portraitFallback;
        private UILabel _name;
        private UILabel _department;
        private UILabel _mood;
        private UILabel _title;
        private UILabel _metric;
        private UILabel _message;
        private UIButton _action;
        private UIButton _readNotice;
        private AdvisorMessageView _messageView;
        private UILabel _newsHeading;
        private UILabel _newsEmpty;
        private int _newsCount;
        private UIButton _buttonStyle;
        private UILabel _labelStyle;
        private NativeDemandTooltips _demandTooltips;
        private CabinetReport _lastReport;
        private long _lastSequence = -1;
        private Vector2 _layoutBodySize = new Vector2(-1f, -1f);
        private Vector2 _layoutResolution = new Vector2(-1f, -1f);
        private bool _contentDirty = true;
        private int _selected;
        private bool _expanded;
        private bool _changedClipping;
        private bool _released;

        internal UIPanel CouncilPanel { get { return _panel; } }
        internal UIPanel CouncilDock { get { return _dock; } }
        internal UIButton CouncilToggle { get { return _toggle; } }
        internal UIScrollablePanel CouncilContent { get { return _body; } }
        internal UITextureSprite AdvisorPortrait { get { return _portrait; } }
        internal UIComponent CityHost { get { return _host; } }
        internal int SelectedAdvisor { get { return _selected; } }
        internal bool IsExpanded { get { return _expanded; } }
        internal int NewsCount { get { return _newsCount; } }
        internal UIButton ReadNoticeButton { get { return _readNotice; } }
        internal UIPanel MessagePanel { get { return _messageView == null ? null : _messageView.Panel; } }
        internal UITextureSprite MessagePortrait { get { return _messageView == null ? null : _messageView.Portrait; } }
        internal UIButton MessageCloseButton { get { return _messageView == null ? null : _messageView.CloseButton; } }
        internal UIButton MessageActionButton { get { return _messageView == null ? null : _messageView.ActionButton; } }
        internal AdvisorBriefing MessageBriefing { get { return _messageView == null ? null : _messageView.Briefing; } }
        internal bool MessageVisible { get { return _messageView != null && _messageView.IsVisible; } }
        internal int PendingAlerts { get { return _alerts.PendingCount; } }
        internal int PendingDecisionNotices { get { return _decisionPending.Count; } }
        internal UIButton GetAdvisorButton(int index)
        {
            return index >= 0 && index < _selectors.Length ? _selectors[index] : null;
        }
        internal UITextureSprite GetAdvisorAvatar(int index)
        {
            return index >= 0 && index < _avatars.Length ? _avatars[index] : null;
        }

        internal NativeMunicipalUI(UIView view) { _view = view; }

        internal bool TryInstall()
        {
            if (_released) return false;
            if (_panel != null) return true;
            _city = CityInfoPanel.instance;
            if (_city == null && UIView.library != null) _city = UIView.library.Get<CityInfoPanel>("CityInfoPanel");
            if (_city == null) return false;
            _host = _city.component as UIPanel;
            if (_host == null) return false;
            // Do not add a child to a layout-managed replacement supplied by another mod:
            // doing so could move its native controls or resize its host.
            if (_host.autoLayout || _host.autoFitChildrenHorizontally || _host.autoFitChildrenVertically) return false;
            _buttonStyle = _city.Find<UIButton>("PoliciesButton");
            _labelStyle = _city.Find<UILabel>("PopulationAmount");
            if (_buttonStyle == null || _labelStyle == null) return false;

            FindInfoPanel();

            // CityInfoPanel.UpdatePolicies fits the vertical extent of its direct children
            // every visible frame. Keep our drawer out of that native sizing calculation.
            _dock = _host.AddUIComponent<UIPanel>();
            _dock.name = "SC4EvolutionCouncilDock";
            _dock.backgroundSprite = string.Empty;
            _dock.autoLayout = false;
            _dock.autoFitChildrenHorizontally = false;
            _dock.autoFitChildrenVertically = false;
            _dock.clipChildren = false;
            _dock.opacity = 1f;
            _dock.isInteractive = false;
            _dock.canFocus = false;
            _dock.size = Vector2.zero;
            _dock.relativePosition = Vector3.zero;

            _panel = _dock.AddUIComponent<UIPanel>();
            _panel.name = "SC4EvolutionCouncil";
            _panel.atlas = _host.atlas;
            _panel.backgroundSprite = _host.backgroundSprite;
            _panel.color = _host.color;
            _panel.autoLayout = false;
            _panel.isInteractive = true;
            _panel.canFocus = false;

            // This additive drawer is outside the vanilla content bounds. Do not resize or move
            // any native control, and restore clipping only if nobody has changed it since us.
            if (_host.clipChildren)
            {
                _host.clipChildren = false;
                _changedClipping = true;
            }

            _toggle = Button(_panel, "SC4EvolutionCouncilToggle", "Conselho municipal");
            _toggle.tooltip = "Consultar os sete assessores da cidade";
            _toggle.eventClicked += delegate { SetExpanded(!_expanded); };

            _body = _panel.AddUIComponent<UIScrollablePanel>();
            _body.name = "SC4EvolutionCouncilContent";
            _body.autoLayout = false;
            _body.autoReset = false;
            _body.clipChildren = true;
            _body.scrollWheelDirection = UIOrientation.Vertical;
            _body.scrollWheelAmount = 24;
            _body.builtinKeyNavigation = true;
            _body.isInteractive = true;
            _body.canFocus = false;

            for (int i = 0; i < _selectors.Length; i++)
            {
                int index = i;
                _selectors[i] = Button(_body, "SC4EvolutionCouncilAdvisor" + ((AdvisorId)i), string.Empty);
                _selectors[i].group = _body;
                _selectors[i].tabStrip = true;
                _selectors[i].eventClicked += delegate { SelectAdvisor(index); };
                _avatars[i] = _selectors[i].AddUIComponent<FramedAdvisorPortrait>();
                _avatars[i].name = "SC4EvolutionCouncilAvatar" + ((AdvisorId)i);
                _avatars[i].material = _view.defaultAtlas.material;
                _avatars[i].isInteractive = false;
                _avatars[i].canFocus = false;
                _avatarFallbacks[i] = Label("SC4EvolutionCouncilAvatarFallback" + ((AdvisorId)i), Departments[i], false, _selectors[i]);
                _avatarFallbacks[i].textAlignment = UIHorizontalAlignment.Center;
                _avatarFallbacks[i].verticalAlignment = UIVerticalAlignment.Middle;
            }

            _portrait = _body.AddUIComponent<FramedAdvisorPortrait>();
            _portrait.name = "SC4EvolutionCouncilPortrait";
            _portrait.material = _view.defaultAtlas.material;
            _portrait.isInteractive = false;
            _portrait.canFocus = false;
            _portraitFallback = Label("SC4EvolutionCouncilPortraitFallback", "Assessor", false);
            _name = Label("SC4EvolutionCouncilName", string.Empty, true);
            _department = Label("SC4EvolutionCouncilDepartment", string.Empty, true);
            _mood = Label("SC4EvolutionCouncilMood", string.Empty, true);
            _title = Label("SC4EvolutionCouncilTitle", string.Empty, true);
            _metric = Label("SC4EvolutionCouncilMetric", string.Empty, true);
            _message = Label("SC4EvolutionCouncilMessage", string.Empty, true);
            _message.isInteractive = true;
            _readNotice = Button(_body, "SC4EvolutionCouncilReadNotice", "Ler aviso");
            _readNotice.eventClicked += delegate { ReadSelectedNotice(); };
            _action = Button(_body, "SC4EvolutionCouncilAction", "Abrir visualização");
            _action.eventClicked += delegate { OpenAction(); };
            _newsHeading = Label("SC4EvolutionCouncilNewsHeading", "Últimos avisos", true);
            _newsEmpty = Label("SC4EvolutionCouncilNewsEmpty", "Nenhum aviso registrado.", true);
            for (int i = 0; i < _newsTitles.Length; i++)
            {
                _newsTitles[i] = Label("SC4EvolutionCouncilNewsTitle" + i, string.Empty, true);
                _newsTexts[i] = Label("SC4EvolutionCouncilNewsText" + i, string.Empty, true);
            }

            _messageView = new AdvisorMessageView(_view, _host, _buttonStyle, _labelStyle);

            if (_info != null) _demandTooltips = new NativeDemandTooltips(_info);
            SetExpanded(false);
            Refresh(true);
            return true;
        }

        internal void Toggle()
        {
            if (UIView.HasModalInput()) return;
            if (!TryInstall())
            {
                // Native library panels can initialize their controls only on first activation.
                OpenNativeCity();
                if (!TryInstall()) return;
                SetExpanded(true);
                return;
            }
            if (!_host.isVisible)
            {
                OpenNativeCity();
                SetExpanded(true);
            }
            else SetExpanded(!_expanded);
        }

        private void FindInfoPanel()
        {
            if (_info != null) return;
            foreach (InfoPanel info in _view.GetComponentsInChildren<InfoPanel>(true))
                if (info.Find<UISlider>("ResidentialDemand") != null) { _info = info; break; }
        }

        private void OpenNativeCity()
        {
            FindInfoPanel();
            CityInfoPanel city = CityInfoPanel.instance;
            if (city != null && city.isVisible) return;
            // Use the normal city-name route so vanilla positioning and rename callbacks survive.
            UIComponent source = _info == null ? null : _info.Find<UITextComponent>("Name");
            if (_info != null && source != null)
                _info.OnCityInfoClicked(source, new UIMouseEventParameter(source, UIMouseButton.Left,
                    1, default(Ray), Vector2.zero, Vector2.zero, 0f));
            else
                UIView.library.Show<CityInfoPanel>("CityInfoPanel");
        }

        internal void Refresh(bool force)
        {
            if (_released || _panel == null) return;
            if (_demandTooltips == null)
            {
                FindInfoPanel();
                if (_info != null) _demandTooltips = new NativeDemandTooltips(_info);
            }
            CabinetReport report = ModRuntime.CurrentReport;
            ObserveGameplayDecisionNotices();
            ApplyReport(report, force);
            if (_demandTooltips != null)
                _demandTooltips.Refresh(ModRuntime.CurrentDemand);
            RefreshActionAvailability();
            if (_messageView != null) _messageView.RefreshLayout();
            Layout();
            PresentPendingNotice(report);
        }

        internal void Animate()
        {
            if (_released) return;
            if (_portraitAnimation != null && _portrait != null && _portrait.isVisible)
                _portrait.texture = _portraitAnimation.Current;
            if (_messageAnimation != null && _messageView != null && _messageView.IsVisible)
                _messageView.Animate(_messageAnimation.Current);
        }

        private void ApplyReport(CabinetReport report, bool force)
        {
            if (force || !ReferenceEquals(report, _lastReport) || (report != null && report.Sequence != _lastSequence))
            {
                if (!ReferenceEquals(report, _lastReport) || (report != null && report.Sequence != _lastSequence))
                    _alerts.Observe(report);
                _lastReport = report;
                _lastSequence = report == null ? -1 : report.Sequence;
                Array.Clear(_briefings, 0, _briefings.Length);
                if (report != null && report.Snapshot != null && report.Snapshot.DataValid && report.Briefings != null)
                {
                    foreach (AdvisorBriefing briefing in report.Briefings)
                    {
                        if (briefing == null) continue;
                        int index = (int)briefing.Advisor;
                        if (index >= 0 && index < _briefings.Length) _briefings[index] = briefing;
                    }
                }
                RenderBriefing();
                RenderNews(report);
            }
        }

        internal bool ReadSelectedNotice()
        {
            string reason;
            if (_released || !TryInstall() || PresentationBlocked(false, out reason)) return false;
            // One fresh snapshot drives both the visible briefing and queue acknowledgement.
            CabinetReport report = ModRuntime.CurrentReport;
            ApplyReport(report, false);
            AdvisorBriefing briefing = _briefings[_selected];
            if (briefing == null || !PresentMessage(briefing)) return false;
            // An unavailable measurement does not display the real pending episode. Keep that
            // episode queued until usable advice can actually be shown or recovery cancels it.
            if (!briefing.IsSuspended) _alerts.Acknowledge(briefing.Advisor);
            return true;
        }

        private void PresentPendingNotice(CabinetReport report)
        {
            string reason;
            if (_messageView == null || _messageView.IsVisible || report == null || report.Snapshot == null
                || !report.Snapshot.DataValid || report.Snapshot.Population <= 0 || PresentationBlocked(true, out reason)) return;
            AdvisorBriefing briefing;
            if (_alerts.TryPeek(out briefing) && PresentMessage(briefing))
            {
                _alerts.Acknowledge(briefing.Advisor);
                return;
            }
            PresentPendingGameplayDecision();
        }

        private void ObserveGameplayDecisionNotices()
        {
            object gameplay = ModRuntime.CurrentGameplay;
            if (gameplay == null)
            {
                ResetGameplayDecisionState();
                return;
            }

            Type type = gameplay.GetType();
            string cityIdentity = ReadGameplayString(type, gameplay, "CityIdentity");
            uint frame;
            if (!TryReadGameplayUInt(type, gameplay, "Frame", out frame))
            {
                // An optional malformed snapshot must never interrupt the native
                // cabinet. Drop only this transient decision queue so a stale
                // notice cannot cross an unknown gameplay session boundary.
                ResetGameplayDecisionState();
                return;
            }

            // Gameplay sessions are city-local and event ids restart at zero. A changed
            // identity or frame rewind starts a fresh delivery window without touching
            // AdvisorEngine's persisted report state.
            if (!_decisionFrameKnown || !string.Equals(_decisionCityIdentity, cityIdentity, StringComparison.Ordinal)
                || frame < _decisionLastFrame)
            {
                _decisionPending.Clear();
                _decisionSeen.Clear();
                _decisionCityIdentity = cityIdentity;
                _decisionFrameKnown = true;
            }
            _decisionLastFrame = frame;

            PruneExpiredGameplayDecisions(frame);
            Array values = ReadGameplayProperty(type, gameplay, "DecisionNotices") as Array;
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                DecisionNotice notice = values.GetValue(i) as DecisionNotice;
                if (!ValidGameplayDecisionNotice(notice, frame)) continue;
                string key = GameplayDecisionKey(notice);
                if (_decisionSeen.Contains(key)) continue;
                RememberGameplayDecision(key);
                RemoveStaleGameplayDecision(notice);
                QueueGameplayDecision(key, notice);
            }
        }

        private void PresentPendingGameplayDecision()
        {
            DecisionNotice notice;
            while (TryPeekGameplayDecision(out notice))
            {
                if (!ValidGameplayDecisionNotice(notice, _decisionLastFrame))
                {
                    _decisionPending.Remove(GameplayDecisionKey(notice));
                    continue;
                }
                AdvisorBriefing briefing = ToGameplayBriefing(notice);
                if (briefing == null || !PresentMessage(briefing)) return;
                // The event remains owned by DecisionEngine; this only acknowledges that
                // the owned native message actually opened.
                _decisionPending.Remove(GameplayDecisionKey(notice));
                return;
            }
        }

        private bool TryPeekGameplayDecision(out DecisionNotice notice)
        {
            notice = null;
            foreach (DecisionNotice candidate in _decisionPending.Values)
                if (notice == null || HigherGameplayPriority(candidate, notice)) notice = candidate;
            return notice != null;
        }

        private static bool HigherGameplayPriority(DecisionNotice candidate, DecisionNotice previous)
        {
            if (candidate.Urgency != previous.Urgency) return candidate.Urgency > previous.Urgency;
            if (candidate.ShownAtFrame != previous.ShownAtFrame) return candidate.ShownAtFrame < previous.ShownAtFrame;
            return candidate.EventId < previous.EventId;
        }

        private static bool LowerGameplayPriority(DecisionNotice candidate, DecisionNotice previous)
        {
            if (candidate.Urgency != previous.Urgency) return candidate.Urgency < previous.Urgency;
            if (candidate.ShownAtFrame != previous.ShownAtFrame) return candidate.ShownAtFrame > previous.ShownAtFrame;
            return candidate.EventId > previous.EventId;
        }

        private static string GameplayDecisionKey(DecisionNotice notice)
        {
            return notice.EventId.ToString(CultureInfo.InvariantCulture) + ":" +
                notice.ShownAtFrame.ToString(CultureInfo.InvariantCulture);
        }

        private void RememberGameplayDecision(string key)
        {
            _decisionSeen.Add(key);
            _decisionSeenOrder.Enqueue(key);
            while (_decisionSeenOrder.Count > MaxDecisionSeen)
                _decisionSeen.Remove(_decisionSeenOrder.Dequeue());
        }

        private void QueueGameplayDecision(string key, DecisionNotice notice)
        {
            if (_decisionPending.Count >= MaxDecisionPending)
            {
                string lowestKey = null;
                DecisionNotice lowest = null;
                foreach (KeyValuePair<string, DecisionNotice> pair in _decisionPending)
                    if (lowest == null || LowerGameplayPriority(pair.Value, lowest))
                    {
                        lowestKey = pair.Key;
                        lowest = pair.Value;
                    }
                if (lowest != null && LowerGameplayPriority(notice, lowest)) return;
                if (lowestKey != null) _decisionPending.Remove(lowestKey);
            }
            _decisionPending[key] = notice;
        }

        private void PruneExpiredGameplayDecisions(uint frame)
        {
            List<string> expired = null;
            foreach (KeyValuePair<string, DecisionNotice> pair in _decisionPending)
                if (!ValidGameplayDecisionNotice(pair.Value, frame))
                {
                    if (expired == null) expired = new List<string>();
                    expired.Add(pair.Key);
                }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++) _decisionPending.Remove(expired[i]);
        }

        private void RemoveStaleGameplayDecision(DecisionNotice current)
        {
            List<string> stale = null;
            foreach (KeyValuePair<string, DecisionNotice> pair in _decisionPending)
                if (pair.Value.EventId == current.EventId && pair.Value.ShownAtFrame < current.ShownAtFrame)
                {
                    if (stale == null) stale = new List<string>();
                    stale.Add(pair.Key);
                }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++) _decisionPending.Remove(stale[i]);
        }

        private static bool ValidGameplayDecisionNotice(DecisionNotice notice, uint frame)
        {
            if (notice == null || notice.EventId < 0 || notice.Advisor < AdvisorId.Finance ||
                notice.Advisor > AdvisorId.Transport || string.IsNullOrEmpty(notice.Title) ||
                string.IsNullOrEmpty(notice.Message) || notice.Urgency < DecisionUrgency.Info ||
                notice.Urgency > DecisionUrgency.Critical) return false;
            // A notice that reached the UI after its event window is ignored rather
            // than replayed as stale advice.
            return notice.ExpiresAtFrame == 0 || frame < notice.ExpiresAtFrame;
        }

        private static AdvisorBriefing ToGameplayBriefing(DecisionNotice notice)
        {
            if (notice == null) return null;
            IssueSeverity severity;
            AdvisorMood mood;
            switch (notice.Urgency)
            {
                case DecisionUrgency.Critical:
                    severity = IssueSeverity.Critical; mood = AdvisorMood.Angry; break;
                case DecisionUrgency.Urgent:
                    severity = IssueSeverity.Critical; mood = AdvisorMood.Concerned; break;
                case DecisionUrgency.Attention:
                    severity = IssueSeverity.Warning; mood = AdvisorMood.Concerned; break;
                default:
                    severity = IssueSeverity.Notice; mood = AdvisorMood.Calm; break;
            }
            return new AdvisorBriefing
            {
                Advisor = notice.Advisor,
                Name = AdvisorName(notice.Advisor, notice.AdvisorName),
                Department = DepartmentName(notice.Advisor, notice.Department),
                Title = notice.Title,
                Message = notice.Message,
                MetricText = notice.IndicatorText ?? string.Empty,
                Detail = string.IsNullOrEmpty(notice.TargetText) ? string.Empty : "Alvo: " + notice.TargetText,
                Mood = mood,
                Severity = severity,
                Action = DecisionAction(notice.Advisor),
                ConcernDays = 0,
                IsSuspended = false
            };
        }

        private static AdvisorAction DecisionAction(AdvisorId advisor)
        {
            switch (advisor)
            {
                case AdvisorId.Finance: return AdvisorAction.Budget;
                case AdvisorId.UrbanPlanning: return AdvisorAction.Zoning;
                case AdvisorId.EducationHealth: return AdvisorAction.Health;
                case AdvisorId.Transport: return AdvisorAction.Traffic;
                default: return AdvisorAction.Overview;
            }
        }

        private static string AdvisorName(AdvisorId advisor, string supplied)
        {
            switch (advisor)
            {
                case AdvisorId.Finance: return "Betty D. Fato";
                case AdvisorId.UrbanPlanning: return "Ney Einstein";
                case AdvisorId.EducationHealth: return "Dinalva Gaze";
                case AdvisorId.Utilities: return "Jonas Pronto";
                case AdvisorId.PublicSafety: return "Samuel Fogaça";
                case AdvisorId.Environment: return "Camila Camomila";
                case AdvisorId.Transport: return "Roberto Carros";
                default: return string.IsNullOrEmpty(supplied) ? "Assessoria" : supplied;
            }
        }

        private static string DepartmentName(AdvisorId advisor, string supplied)
        {
            switch (advisor)
            {
                case AdvisorId.Finance: return "Finanças";
                case AdvisorId.UrbanPlanning: return "Planejamento urbano";
                case AdvisorId.EducationHealth: return "Educação e saúde";
                case AdvisorId.Utilities: return "Serviços públicos";
                case AdvisorId.PublicSafety: return "Segurança pública";
                case AdvisorId.Environment: return "Meio ambiente";
                case AdvisorId.Transport: return "Transportes";
                default: return string.IsNullOrEmpty(supplied) ? "Gabinete" : supplied;
            }
        }

        private static object ReadGameplayProperty(Type type, object value, string name)
        {
            try
            {
                System.Reflection.PropertyInfo property = type.GetProperty(name,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                return property == null ? null : property.GetValue(value, null);
            }
            catch { return null; }
        }

        private static string ReadGameplayString(Type type, object value, string name)
        {
            object result = ReadGameplayProperty(type, value, name);
            return result as string ?? string.Empty;
        }

        private static bool TryReadGameplayUInt(Type type, object value, string name, out uint result)
        {
            object raw = ReadGameplayProperty(type, value, name);
            if (raw is uint) { result = (uint)raw; return true; }
            try { result = Convert.ToUInt32(raw, CultureInfo.InvariantCulture); return raw != null; }
            catch { result = 0; return false; }
        }

        private void ResetGameplayDecisionState()
        {
            if (!_decisionFrameKnown && _decisionPending.Count == 0 && _decisionSeen.Count == 0) return;
            _decisionPending.Clear();
            _decisionSeen.Clear();
            _decisionSeenOrder.Clear();
            _decisionCityIdentity = null;
            _decisionLastFrame = 0;
            _decisionFrameKnown = false;
        }

        private static bool PresentationBlocked(bool automatic, out string reason)
        {
            reason = string.Empty;
            if (!ModRuntime.IsLoaded || !Singleton<LoadingManager>.exists)
            {
                reason = "Aguarde a cidade terminar de carregar.";
                return true;
            }
            if (UIView.HasModalInput())
            {
                reason = "Feche a janela de confirmação para ler o aviso.";
                return true;
            }
            LoadingAnimation animation = Singleton<LoadingManager>.instance.LoadingAnimationComponent;
            Camera camera = animation == null ? null : animation.GetComponent<Camera>();
            if (animation == null || camera == null
                || (animation.enabled && animation.gameObject.activeInHierarchy)
                || (camera.enabled && camera.gameObject.activeInHierarchy))
            {
                reason = "Aguarde a cidade terminar de carregar.";
                return true;
            }
            // Explicit player reading can coexist with a non-modal tutorial. Only unsolicited
            // notices wait for it; neither route changes the tutorial's state or presentation.
            TutorialAdvisorPanel tutorial = TutorialAdvisorPanel.instance;
            return automatic && tutorial != null && tutorial.isVisible;
        }

        private bool PresentMessage(AdvisorBriefing briefing)
        {
            if (briefing == null || _messageView == null) return false;
            AdvisorAction action = EffectiveAction(briefing.Advisor, briefing.Action);
            AdvisorId advisor = briefing.Advisor;
            int key = ((int)advisor * 4) + (int)briefing.Mood;
            ReleaseMessageAnimation();
            _messageAnimation = LoadAnimation(advisor, briefing.Mood);
            _messageAnimationKey = key;
            Texture2D portrait = _messageAnimation == null ? GetPortrait(advisor, briefing.Mood) : _messageAnimation.Current;
            return _messageView.Show(briefing, portrait, action,
                CanOpenAction(action, advisor), delegate(AdvisorAction target) { NavigateAction(target, advisor); });
        }

        private void SetExpanded(bool expanded)
        {
            _expanded = expanded;
            _body.isVisible = expanded;
            _toggle.text = expanded ? "Conselho municipal −" : "Conselho municipal +";
            Layout();
        }

        private void SelectAdvisor(int index)
        {
            if (index < 0 || index >= _briefings.Length) return;
            _selected = index;
            RenderBriefing();
            Layout();
            _body.ScrollToTop();
        }

        private void RenderBriefing()
        {
            _contentDirty = true;
            AdvisorBriefing briefing = _briefings[_selected];
            for (int i = 0; i < _selectors.Length; i++)
            {
                _selectors[i].state = i == _selected ? UIButton.ButtonState.Focused : UIButton.ButtonState.Normal;
                AdvisorBriefing advisor = _briefings[i];
                _selectors[i].tooltip = advisor == null ? Departments[i] : advisor.Name + "\n" + advisor.Department;
                AdvisorMood mood = advisor == null ? AdvisorMood.Calm : advisor.Mood;
                _avatars[i].SetFraming((AdvisorId)i, mood);
                Texture2D avatar = GetPortrait((AdvisorId)i, mood);
                _avatars[i].texture = avatar;
                _avatars[i].isVisible = avatar != null;
                _avatarFallbacks[i].isVisible = avatar == null;
            }
            _name.text = briefing == null ? "Aguardando avaliação" : briefing.Name;
            _department.text = briefing == null ? Departments[_selected] : briefing.Department;
            _mood.text = briefing == null ? string.Empty : "Humor: " + MoodText(briefing.Mood);
            _title.text = briefing == null ? "Sem parecer disponível" : briefing.Title;
            _metric.text = briefing == null ? string.Empty : briefing.MetricText;
            _message.text = briefing == null ? "O parecer será atualizado com a próxima amostra da cidade." : briefing.Message;
            _message.tooltip = briefing == null ? string.Empty : briefing.Detail;
            _action.text = _selected == (int)AdvisorId.Finance || (briefing != null && briefing.Action == AdvisorAction.Budget) ? "Abrir orçamento"
                : briefing != null && briefing.Action == AdvisorAction.Overview ? "Ver dados da cidade" : "Abrir visualização";
            RefreshActionAvailability();
            RenderPortrait(briefing == null ? AdvisorMood.Calm : briefing.Mood);
        }

        private void RenderPortrait(AdvisorMood mood)
        {
            FramedAdvisorPortrait framed = _portrait as FramedAdvisorPortrait;
            if (framed != null) framed.SetFraming((AdvisorId)_selected, mood);
            ReleasePortraitAnimationIfChanged((AdvisorId)_selected, mood);
            Texture2D portrait = _portraitAnimation == null ? GetPortrait((AdvisorId)_selected, mood) : _portraitAnimation.Current;
            _portrait.texture = portrait;
            _portrait.isVisible = portrait != null;
            _portraitFallback.isVisible = portrait == null;
        }

        private void ReleasePortraitAnimationIfChanged(AdvisorId advisor, AdvisorMood mood)
        {
            int key = ((int)advisor * 4) + (int)mood;
            if (_portraitAnimationKey == key && _portraitAnimation != null) return;
            if (_portraitAnimation != null && !ReferenceEquals(_portraitAnimation, _messageAnimation)) _portraitAnimation.Release();
            _portraitAnimation = LoadAnimation(advisor, mood);
            _portraitAnimationKey = key;
        }

        private AdvisorPortraitAnimation LoadAnimation(AdvisorId advisor, AdvisorMood mood)
        {
            try { return AdvisorPortraitRenderer.LoadAnimation(ModRuntime.ProjectRoot, advisor, mood); }
            catch (Exception exception) { ModRuntime.Log("Council portrait animation unavailable: " + exception.Message); return null; }
        }

        private void ReleaseMessageAnimation()
        {
            if (_messageAnimation != null && !ReferenceEquals(_messageAnimation, _portraitAnimation)) _messageAnimation.Release();
            _messageAnimation = null;
            _messageAnimationKey = -1;
        }

        private Texture2D GetPortrait(AdvisorId advisor, AdvisorMood mood)
        {
            int advisorIndex = (int)advisor;
            if (advisorIndex < 0 || advisorIndex >= _briefings.Length) return null;
            int moodIndex = (int)mood;
            if (moodIndex < 0 || moodIndex > 3) { mood = AdvisorMood.Calm; moodIndex = 0; }
            int index = advisorIndex * 4 + moodIndex;
            if (!_portraitAttempted[index])
            {
                _portraitAttempted[index] = true;
                try { _portraits[index] = AdvisorPortraitRenderer.Load(ModRuntime.ProjectRoot, advisor, mood); }
                catch (Exception exception) { ModRuntime.Log("Council portrait unavailable: " + exception.Message); }
            }
            return _portraits[index];
        }

        private void RenderNews(CabinetReport report)
        {
            _newsCount = 0;
            if (report != null && report.News != null)
                for (int i = report.News.Length - 1; i >= 0 && _newsCount < _newsTitles.Length; i--)
                {
                    CityNewsItem item = report.News[i];
                    if (item == null) continue;
                    _newsTitles[_newsCount].text = item.Date.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)
                        + " · " + item.Title;
                    _newsTexts[_newsCount].text = item.Summary ?? string.Empty;
                    _newsCount++;
                }
            for (int i = 0; i < _newsTitles.Length; i++)
            {
                _newsTitles[i].isVisible = i < _newsCount;
                _newsTexts[i].isVisible = i < _newsCount;
            }
            _newsEmpty.isVisible = _newsCount == 0;
            _contentDirty = true;
        }

        private void Layout()
        {
            if (_panel == null || _host == null) return;
            Vector2 resolution = _view.GetScreenResolution();
            Vector3 origin = _host.absolutePosition;
            float rightSpace = resolution.x - origin.x - _host.width - 8f;
            float leftSpace = origin.x - 8f;
            bool right = rightSpace >= leftSpace;
            float availableWidth = Mathf.Max(rightSpace, leftSpace);
            float width = Mathf.Min(420f, availableWidth);
            float height = _expanded ? Mathf.Min(450f, Mathf.Max(180f, resolution.y - 116f)) : 36f;
            float x = right ? origin.x + _host.width + 4f : origin.x - width - 4f;
            float y = Mathf.Clamp(origin.y, 4f, Mathf.Max(4f, resolution.y - height - 100f));
            if (availableWidth < 240f)
            {
                float above = origin.y - 8f;
                float below = resolution.y - 108f - origin.y - _host.height;
                if (Mathf.Max(above, below) >= 120f)
                {
                    width = Mathf.Min(420f, resolution.x - 8f);
                    height = _expanded ? Mathf.Min(450f, Mathf.Max(above, below)) : 36f;
                    x = Mathf.Clamp(origin.x, 4f, resolution.x - width - 4f);
                    y = above >= below ? origin.y - height - 4f : origin.y + _host.height + 4f;
                }
            }
            // If vanilla already consumes the viewport, do not cover its controls or the HUD.
            _panel.isVisible = width >= 180f;
            if (!_panel.isVisibleSelf) return;
            // Logical coordinates follow UIView's UI scale. The drawer never changes vanilla bounds.
            _panel.size = new Vector2(width, height);
            _panel.absolutePosition = new Vector3(Mathf.Clamp(x, 4f, Mathf.Max(4f, resolution.x - width - 4f)), y);
            _panel.ResetLayout(false, true);
            _toggle.relativePosition = new Vector3(8f, 5f);
            _toggle.size = new Vector2(width - 16f, 26f);
            _body.relativePosition = new Vector3(10f, 38f);
            _body.size = new Vector2(width - 20f, Mathf.Max(1f, height - 48f));
            _body.ResetLayout(false, true);
            float contentWidth = _body.width - 4f;
            const float avatarButtonSize = 52f;
            const float avatarGap = 2f;
            int columns = contentWidth >= 7f * avatarButtonSize + 6f * avatarGap ? 7
                : Math.Min(4, Math.Max(1, (int)((contentWidth + avatarGap) / (avatarButtonSize + avatarGap))));
            if (!_contentDirty && _layoutBodySize == _body.size && _layoutResolution == resolution
                && SelectorLayoutValid(columns, contentWidth)) return;
            _contentDirty = false;
            _layoutBodySize = _body.size;
            _layoutResolution = resolution;
            Vector2 previousScroll = _body.scrollPosition;
            _body.scrollPosition = Vector2.zero;
            bool wide = contentWidth >= 280f;
            for (int i = 0; i < _selectors.Length; i++)
            {
                Vector2 position = SelectorPosition(i, columns, contentWidth);
                Place(_selectors[i], position.x, position.y, avatarButtonSize, avatarButtonSize);
                Place(_avatars[i], 1f, 1f, 50f, 50f);
                Place(_avatarFallbacks[i], 1f, 1f, 50f, 50f);
            }
            float portraitY = ((_selectors.Length + columns - 1) / columns) * (avatarButtonSize + avatarGap) + 8f;
            float portraitSize = wide ? 128f : Mathf.Min(112f, contentWidth);
            float portraitX = wide ? 0f : (contentWidth - portraitSize) * 0.5f;
            Place(_portrait, portraitX, portraitY, portraitSize, portraitSize);
            Place(_portraitFallback, portraitX, portraitY, portraitSize, portraitSize);
            float nameX = wide ? portraitSize + 12f : 0f;
            float nameY = wide ? portraitY : portraitY + portraitSize + 8f;
            nameY = PlaceIdentification(_name, nameX, nameY, contentWidth - nameX) + 3f;
            nameY = PlaceIdentification(_department, nameX, nameY, contentWidth - nameX) + 3f;
            nameY = PlaceIdentification(_mood, nameX, nameY, contentWidth - nameX);
            float textY = Mathf.Max(portraitY + portraitSize, nameY) + 10f;
            textY = PlaceText(_title, textY, contentWidth) + 7f;
            textY = PlaceText(_metric, textY, contentWidth) + 7f;
            textY = PlaceText(_message, textY, contentWidth) + 12f;
            Place(_readNotice, 0f, textY, contentWidth, 28f);
            textY += 34f;
            Place(_action, 0f, textY, contentWidth, 28f);
            textY += 46f;
            textY = PlaceText(_newsHeading, textY, contentWidth) + 10f;
            if (_newsCount == 0) PlaceText(_newsEmpty, textY, contentWidth);
            else _newsEmpty.relativePosition = Vector3.zero;
            for (int i = 0; i < _newsCount; i++)
            {
                textY = PlaceText(_newsTitles[i], textY, contentWidth) + 4f;
                textY = PlaceText(_newsTexts[i], textY, contentWidth) + 16f;
            }
            // Native Reset considers active children even when invisible. Unused labels must
            // have canonical coordinates too, rather than AddUIComponent's negative-Y default.
            for (int i = _newsCount; i < _newsTitles.Length; i++)
            {
                _newsTitles[i].relativePosition = Vector3.zero;
                _newsTexts[i].relativePosition = Vector3.zero;
            }
            foreach (UIComponent child in _body.components) child.ResetLayout(false, true);
            _body.scrollPosition = previousScroll;
        }

        private static Vector2 SelectorPosition(int index, int columns, float contentWidth)
        {
            int rowCount = Math.Min(columns, 7 - (index / columns) * columns);
            float rowWidth = rowCount * 52f + (rowCount - 1) * 2f;
            return new Vector2((contentWidth - rowWidth) * 0.5f + (index % columns) * 54f,
                (index / columns) * 54f);
        }

        private bool SelectorLayoutValid(int columns, float contentWidth)
        {
            Vector2 scroll = _body.scrollPosition;
            for (int i = 0; i < _selectors.Length; i++)
            {
                Vector2 logicalPosition = (Vector2)_selectors[i].relativePosition + scroll;
                if ((logicalPosition - SelectorPosition(i, columns, contentWidth)).sqrMagnitude > 0.25f
                    || (_selectors[i].size - new Vector2(52f, 52f)).sqrMagnitude > 0.25f) return false;
            }
            return true;
        }

        private static float PlaceText(UILabel label, float y, float width)
        {
            return PlaceIdentification(label, 0f, y, width);
        }

        private static float PlaceIdentification(UILabel label, float x, float y, float width)
        {
            label.width = width;
            label.Invalidate();
            label.relativePosition = new Vector3(x, y);
            label.ResetLayout(false, true);
            return y + label.height;
        }

        private static void Place(UIComponent component, float x, float y, float width, float height)
        {
            component.size = new Vector2(width, height);
            component.relativePosition = new Vector3(x, y);
            component.ResetLayout(false, true);
        }

        private UIButton Button(UIComponent parent, string name, string text)
        {
            UIButton button = parent.AddUIComponent<UIButton>();
            button.name = name;
            button.autoSize = false;
            button.size = new Vector2(150f, 26f);
            button.font = _buttonStyle.font;
            button.atlas = _buttonStyle.atlas;
            button.normalBgSprite = _buttonStyle.normalBgSprite;
            button.hoveredBgSprite = _buttonStyle.hoveredBgSprite;
            button.pressedBgSprite = _buttonStyle.pressedBgSprite;
            button.focusedBgSprite = _buttonStyle.focusedBgSprite;
            button.disabledBgSprite = _buttonStyle.disabledBgSprite;
            button.color = _buttonStyle.color;
            button.hoveredColor = _buttonStyle.hoveredColor;
            button.pressedColor = _buttonStyle.pressedColor;
            button.focusedColor = _buttonStyle.focusedColor;
            button.disabledColor = _buttonStyle.disabledColor;
            button.textScale = _buttonStyle.textScale;
            button.textColor = _buttonStyle.textColor;
            button.hoveredTextColor = _buttonStyle.hoveredTextColor;
            button.pressedTextColor = _buttonStyle.pressedTextColor;
            button.focusedTextColor = _buttonStyle.focusedTextColor;
            button.disabledTextColor = _buttonStyle.disabledTextColor;
            button.textHorizontalAlignment = UIHorizontalAlignment.Center;
            button.textVerticalAlignment = UIVerticalAlignment.Middle;
            button.wordWrap = false;
            button.text = text;
            return button;
        }

        private UILabel Label(string name, string text, bool autoHeight, UIComponent parent = null)
        {
            UILabel label = (parent ?? _body).AddUIComponent<UILabel>();
            label.name = name;
            label.autoSize = false;
            label.autoHeight = false;
            label.size = new Vector2(300f, 22f);
            label.font = _labelStyle.font;
            label.textScale = _labelStyle.textScale;
            label.textColor = _labelStyle.textColor;
            label.disabledTextColor = _labelStyle.disabledTextColor;
            label.useDropShadow = _labelStyle.useDropShadow;
            label.dropShadowColor = _labelStyle.dropShadowColor;
            label.dropShadowOffset = _labelStyle.dropShadowOffset;
            label.wordWrap = true;
            label.isInteractive = false;
            label.canFocus = false;
            label.text = text ?? string.Empty;
            label.autoHeight = autoHeight;
            label.relativePosition = Vector3.zero;
            label.ResetLayout(false, true);
            return label;
        }

        private static string MoodText(AdvisorMood mood)
        {
            switch (mood)
            {
                case AdvisorMood.Concerned: return "Preocupado";
                case AdvisorMood.Angry: return "Irritado";
                case AdvisorMood.Pleased: return "Satisfeito";
                default: return "Tranquilo";
            }
        }

        private static AdvisorAction EffectiveAction(AdvisorId advisor, AdvisorAction action)
        {
            return advisor == AdvisorId.Finance ? AdvisorAction.Budget : action;
        }

        private static bool CanOpenAction(AdvisorAction action, AdvisorId advisor)
        {
            if (action == AdvisorAction.Budget)
                return GetBudgetToolbar() != null && ToolsModifierControl.economyPanel != null
                    // Municipal finance is consultable from the start. Native budget controls
                    // retain their own restrictions; no milestone or unlock state is changed.
                    && (advisor == AdvisorId.Finance || ToolsModifierControl.IsUnlocked(UnlockManager.Feature.Economy));
            if (action == AdvisorAction.Overview)
                return CityInfoPanel.instance != null && CityInfoPanel.instance.component != null;
            InfoManager.InfoMode mode;
            return TryGetMapMode(action, out mode) && Singleton<InfoManager>.exists
                && Singleton<InfoManager>.instance.IsInfoModeAvailable(mode) && ToolsModifierControl.IsUnlocked(mode);
        }

        private static GameMainToolbar GetBudgetToolbar()
        {
            // This is the same native host used by GameMainToolbar.instance, with a null guard
            // for UI teardown/initialization instead of dereferencing an absent toolstrip.
            UITabstrip strip = UIView.Find<UITabstrip>("MainToolstrip");
            return strip == null ? null : strip.GetComponent<GameMainToolbar>();
        }

        private bool TryGetAction(out AdvisorAction action)
        {
            AdvisorBriefing briefing = _briefings[_selected];
            action = EffectiveAction((AdvisorId)_selected, briefing == null ? AdvisorAction.Overview : briefing.Action);
            return _selected == (int)AdvisorId.Finance || briefing != null;
        }

        private void RefreshActionAvailability()
        {
            AdvisorAction action;
            bool hasAction = TryGetAction(out action);
            _action.isEnabled = hasAction && CanOpenAction(action, (AdvisorId)_selected);
            AdvisorBriefing briefing = _briefings[_selected];
            string reason;
            bool noticeBlocked = PresentationBlocked(false, out reason);
            _readNotice.isEnabled = briefing != null && !noticeBlocked;
            _readNotice.tooltip = briefing == null ? "Sem parecer disponível."
                : noticeBlocked ? reason : "Ler o parecer atual deste assessor.";
            _action.tooltip = _action.isEnabled ? (briefing == null ? string.Empty : briefing.Detail)
                : !hasAction ? "Sem parecer disponível."
                : action == AdvisorAction.Budget ? "O orçamento não está disponível neste momento."
                : "Esta visualização não está disponível nesta cidade.";
            if (_messageView != null && _messageView.IsVisible && _messageView.Briefing != null && _messageView.ActionButton != null)
            {
                AdvisorBriefing notice = _messageView.Briefing;
                _messageView.ActionButton.isEnabled = CanOpenAction(EffectiveAction(notice.Advisor, notice.Action), notice.Advisor);
            }
        }

        private void OpenAction()
        {
            AdvisorAction action;
            if (!TryGetAction(out action)) return;
            NavigateAction(action, (AdvisorId)_selected);
        }

        private void NavigateAction(AdvisorAction action, AdvisorId advisor)
        {
            if (!CanOpenAction(action, advisor))
            {
                RefreshActionAvailability();
                return;
            }
            if (action == AdvisorAction.Budget)
            {
                GameMainToolbar toolbar = GetBudgetToolbar();
                if (toolbar == null) { RefreshActionAvailability(); return; }
                if (_messageView != null) _messageView.Close();
                _city.OnCloseButton();
                toolbar.ShowEconomyPanel(EconomyPanel.kBudgetTab);
                return;
            }
            if (action == AdvisorAction.Overview)
            {
                // Notices outlive the city panel. Restore the real destination when the player
                // closed that host while reading, and retain the notice if opening was refused.
                if (_host == null || !_host.isVisible)
                {
                    OpenNativeCity();
                    if (_host == null || !_host.isVisible) { RefreshActionAvailability(); return; }
                }
                if (_messageView != null) _messageView.Close();
                SetExpanded(false);
                return;
            }
            InfoManager.InfoMode mode;
            if (TryGetMapMode(action, out mode) && Singleton<InfoManager>.exists)
            {
                InfoManager manager = Singleton<InfoManager>.instance;
                if (manager.IsInfoModeAvailable(mode) && ToolsModifierControl.IsUnlocked(mode))
                {
                    if (_messageView != null) _messageView.Close();
                    _city.OnCloseButton();
                    manager.SetCurrentMode(mode, InfoManager.SubInfoMode.Default);
                }
                else RefreshActionAvailability();
            }
        }

        private static bool TryGetMapMode(AdvisorAction action, out InfoManager.InfoMode mode)
        {
            switch (action)
            {
                case AdvisorAction.Education: mode = InfoManager.InfoMode.Education; return true;
                case AdvisorAction.Health: mode = InfoManager.InfoMode.Health; return true;
                case AdvisorAction.Electricity: mode = InfoManager.InfoMode.Electricity; return true;
                case AdvisorAction.Water: mode = InfoManager.InfoMode.Water; return true;
                case AdvisorAction.Crime: mode = InfoManager.InfoMode.CrimeRate; return true;
                case AdvisorAction.Pollution: mode = InfoManager.InfoMode.Pollution; return true;
                case AdvisorAction.Traffic: mode = InfoManager.InfoMode.Traffic; return true;
                case AdvisorAction.Zoning: mode = InfoManager.InfoMode.BuildingLevel; return true;
                case AdvisorAction.Garbage: mode = InfoManager.InfoMode.Garbage; return true;
                default: mode = InfoManager.InfoMode.None; return false;
            }
        }

        internal void Release()
        {
            if (_released) return;
            _released = true;
            if (_messageView != null) _messageView.Release();
            _alerts.Reset();
            ResetGameplayDecisionState();
            if (_demandTooltips != null) _demandTooltips.Release();
            for (int i = 0; i < _avatars.Length; i++) if (_avatars[i] != null) _avatars[i].texture = null;
            if (_portrait != null) _portrait.texture = null;
            if (_dock != null)
            {
                _dock.isVisible = false;
                if (_dock.parent != null) _dock.parent.RemoveUIComponent(_dock);
                UnityEngine.Object.Destroy(_dock.gameObject);
                _dock = null;
            }
            _panel = null;
            if (_changedClipping && _host != null && !_host.clipChildren) _host.clipChildren = true;
            if (_portraitAnimation != null && !ReferenceEquals(_portraitAnimation, _messageAnimation)) _portraitAnimation.Release();
            if (_messageAnimation != null) _messageAnimation.Release();
            _portraitAnimation = null; _messageAnimation = null;
            for (int i = 0; i < _portraits.Length; i++)
            {
                AdvisorPortraitRenderer.Release(_portraits[i]);
                _portraits[i] = null;
            }
        }
    }
}
