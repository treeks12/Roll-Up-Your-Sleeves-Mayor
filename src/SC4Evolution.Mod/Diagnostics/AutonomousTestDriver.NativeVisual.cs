using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ColossalFramework;
using ColossalFramework.UI;
using SC4Evolution.Core;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Mod.UI;
using UnityEngine;

namespace SC4Evolution.Mod.Diagnostics
{
    public sealed partial class AutonomousTestDriver
    {
        private UIPanel _capturedCouncil;
        private UIScrollablePanel _capturedBody;
        private Texture _capturedPortrait;
        private Vector2 _capturedScroll;
        private string _capturedText, _rciCheck, _visualCheck;
        private bool _budgetOpened, _budgetValidated;
        private EconomyPanel _budgetPanel;
        private string _budgetAnimationKey, _budgetAnimationState;
        private float _budgetGeometryStableSince = -1f;
        private Rect _budgetObservedRect, _capturedBudgetRect;
        private bool _budgetGeometryObserved, _budgetCaptureGeometryPinned, _budgetCaptureGeometryUnchanged;
        private CabinetRoot _budgetCouncilRoot;
        private CityInfoPanel _budgetCityPanel;
        private UIComponent _budgetCityHost;
        private UIPanel _budgetCouncilPanel;
        private UIPanel _budgetCouncilDock;
        private bool _budgetHostNonOccluding;
        private string _budgetHostVisibilityState;
        private UIPanel _budgetMessagePanel;
        private bool _noticePending, _noticeOpenRequested;
        private CabinetRoot _noticeRoot;
        private UIPanel _noticePanel;
        private UITextureSprite _noticePortrait;
        private UIButton _noticeClose, _noticeAction;
        private UIScrollablePanel _noticeScroll;
        private AdvisorBriefing _noticeBriefing;
        private Texture _noticeTexture;
        private Rect _noticePanelRect, _noticePortraitRect, _noticeFraming;
        private Vector2 _noticeScrollPosition;
        private string _noticeText, _avatarStripCheck;
        private long _noticeReportSequence;
        private string _noticeBriefingSignature;
        private int _noticePopulation, _noticePendingAlertsBefore, _noticePendingAlertsAfter;
        private readonly UITextureSprite[] _capturedAvatars = new UITextureSprite[7];
        private readonly UIButton[] _capturedAvatarButtons = new UIButton[7];
        private readonly Texture[] _capturedAvatarTextures = new Texture[7];
        private readonly Rect[] _capturedAvatarFraming = new Rect[7];
        private UITextureSprite _capturedCouncilPortrait;
        private readonly List<VisualGeometryPin> _councilGeometry = new List<VisualGeometryPin>();
        private readonly List<VisualGeometryPin> _noticeGeometry = new List<VisualGeometryPin>();
        private readonly List<string> _noticeChecks = new List<string>();
        private UIButton _selectorObservationTarget;
        private float _selectorObservationStartedAt, _selectorStableSince;
        private int _selectorStableUpdates;
        private string _selectorPreviousGeometry, _selectorFirstBefore, _selectorFirstAfter, _selectorObservationState;
        private bool _selectorObservationActive;
        private readonly List<string> _advisorChecks = new List<string>();
        private static readonly string[] AdvisorTextNames = { "Name", "Department", "Mood", "Title", "Metric", "Message" };

        private CabinetRoot RequireCouncil()
        {
            CabinetRoot root = FindRoot();
            CityInfoPanel city = CityInfoPanel.instance;
            if (root == null || city == null || root.CityHost != city.component || root.CouncilPanel == null
                || root.CouncilDock == null || root.CouncilDock.name != "SC4EvolutionCouncilDock" || root.CouncilDock.parent != city.component
                || root.CouncilPanel.parent != root.CouncilDock || root.CouncilDock.size != Vector2.zero
                || root.CouncilDock.relativePosition.sqrMagnitude > 0.0001f || root.CouncilDock.clipChildren
                || root.CouncilDock.autoLayout || root.CouncilDock.autoFitChildrenHorizontally || root.CouncilDock.autoFitChildrenVertically
                || root.CouncilContent == null
                || root.CouncilContent.parent != root.CouncilPanel || !root.CouncilExpanded || !city.isVisible)
                throw new InvalidOperationException("Native council identity/expanded CityInfoPanel precondition failed.");
            for (int i = 0; i < 7; ++i)
            {
                UIButton button = root.GetAdvisorButton(i);
                if (button == null || button.parent != root.CouncilContent
                    || button.name != "SC4EvolutionCouncilAdvisor" + ((AdvisorId)i).ToString())
                    throw new InvalidOperationException("Exact native council advisor selector is missing: " + i);
                UITextureSprite avatar = root.GetAdvisorAvatar(i);
                if (avatar == null || avatar.parent != button || avatar.name != "SC4EvolutionCouncilAvatar" + ((AdvisorId)i).ToString())
                    throw new InvalidOperationException("Exact native advisor avatar is missing: " + i);
            }
            return root;
        }

        private void EnsureVisualTargetIdentity(UIComponent target)
        {
            if (target == null) throw new InvalidOperationException("Native visual target disappeared.");
            UIComponent expected;
            if (_tab == 7 && _budgetOpened)
            {
                ValidateBudget();
                expected = _budgetPanel.component;
            }
            else if (_noticeOpenRequested) expected = RequireNotice().AdvisorMessagePanel;
            else expected = RequireCouncil().CouncilPanel;
            UIComponent current = target;
            for (int i = 0; current != null && i < 32; ++i, current = current.parent)
                if (current == expected) return;
            throw new InvalidOperationException("Visual target is outside the exact owned council/native budget hierarchy.");
        }

        private bool CouncilSelectorReadyForClick(CabinetRoot root, UIButton selector)
        {
            float now = Time.realtimeSinceStartup;
            string before = CouncilSelectorSnapshot(root, selector);
            root.CouncilContent.ScrollToTop(); // Normal own scroll action only; never force layout.
            string after = CouncilSelectorSnapshot(root, selector);
            if (!_selectorObservationActive)
            {
                _selectorObservationActive = true;
                _selectorObservationTarget = selector;
                _selectorObservationStartedAt = _selectorStableSince = now;
                _selectorStableUpdates = 0;
                _selectorPreviousGeometry = null;
                _selectorFirstBefore = before;
                _selectorFirstAfter = after;
            }
            if (_selectorObservationTarget != selector)
                throw new InvalidOperationException("Own selector identity changed during the pre-click geometry observation.");
            if (_selectorPreviousGeometry != after || before != after)
            {
                _selectorPreviousGeometry = after;
                _selectorStableSince = now;
                _selectorStableUpdates = 0;
            }
            else ++_selectorStableUpdates;
            bool viewportValid = false;
            string viewportError = null;
            // Collect raw before/after geometry BEFORE invoking the unchanged strict
            // viewport gate. Elapsed time alone can never authorize a clipped click.
            try { RequireViewport(selector, true); viewportValid = true; }
            catch (InvalidOperationException error) { viewportError = error.Message; }
            float elapsed = now - _selectorObservationStartedAt;
            float stable = now - _selectorStableSince;
            bool ready = viewportValid && stable >= 0.25f && _selectorStableUpdates >= 4 && elapsed < 3f;
            _selectorObservationState = "{\"selector\":" + Json(selector.name)
                + ",\"startedRealtime\":" + JsonFloat(_selectorObservationStartedAt) + ",\"observedRealtime\":" + JsonFloat(now)
                + ",\"elapsedSeconds\":" + JsonFloat(elapsed) + ",\"timeoutSeconds\":3,\"requiredStableSeconds\":0.25"
                + ",\"observedStableSeconds\":" + JsonFloat(stable) + ",\"requiredStableUpdates\":4,\"observedStableUpdates\":"
                + _selectorStableUpdates.ToString(CultureInfo.InvariantCulture) + ",\"viewportValid\":" + Bool(viewportValid)
                + ",\"scrollActionChangedGeometry\":" + Bool(before != after)
                + ",\"viewportError\":" + Json(viewportError) + ",\"ready\":" + Bool(ready)
                + ",\"firstBeforeScrollToTop\":" + _selectorFirstBefore + ",\"firstAfterScrollToTop\":" + _selectorFirstAfter
                + ",\"beforeScrollToTop\":" + before + ",\"afterScrollToTop\":" + after + "}";
            if (!ready)
            {
                _renderPreconditionsValid = false;
                if (_phase != "WaitingForCouncilSelectorGeometry") { _phase = "WaitingForCouncilSelectorGeometry"; WriteReport(); }
                if (elapsed >= 3f)
                    throw new TimeoutException("Own council selector did not establish stable geometry and a fully valid viewport within 3 seconds; no click. Trace=" + _selectorObservationState);
                return false;
            }
            _phase = "CouncilSelectorGeometryReady";
            WriteReport();
            return true;
        }

        private static string CouncilSelectorSnapshot(CabinetRoot root, UIButton selector)
        {
            UIView view = root.CouncilPanel.GetUIView();
            Vector2 logical = view.GetScreenResolution();
            UIScrollablePanel body = root.CouncilContent;
            Vector2 scroll = body.scrollPosition;
            List<string> ancestors = new List<string>();
            UIComponent parent = selector.parent;
            for (int i = 0; parent != null && i < 32; ++i, parent = parent.parent) ancestors.Add(ObservedComponentJson(parent));
            return "{\"host\":" + ObservedComponentJson(root.CityHost) + ",\"dock\":" + ObservedComponentJson(root.CouncilDock)
                + ",\"council\":" + ObservedComponentJson(root.CouncilPanel) + ",\"body\":" + ObservedComponentJson(body)
                + ",\"selector\":" + ObservedComponentJson(selector) + ",\"scrollX\":" + JsonFloat(scroll.x) + ",\"scrollY\":" + JsonFloat(scroll.y)
                + ",\"logicalResolution\":{\"width\":" + JsonFloat(logical.x) + ",\"height\":" + JsonFloat(logical.y) + "}"
                + ",\"physicalResolution\":{\"width\":" + Screen.width.ToString(CultureInfo.InvariantCulture) + ",\"height\":" + Screen.height.ToString(CultureInfo.InvariantCulture) + "}"
                + ",\"ancestors\":[" + string.Join(",", ancestors.ToArray()) + "],\"ancestorBoundExceeded\":" + Bool(parent != null) + "}";
        }

        private static string ObservedComponentJson(UIComponent component)
        {
            if (component == null) return "null";
            Vector3 absolute = component.absolutePosition, relative = component.relativePosition;
            UIPanel panel = component as UIPanel;
            UIScrollablePanel scroll = component as UIScrollablePanel;
            RectOffset padding = scroll != null ? scroll.scrollPadding : panel != null ? panel.padding : null;
            return "{\"name\":" + Json(component.name) + ",\"parent\":" + Json(component.parent == null ? null : component.parent.name)
                + ",\"absoluteRect\":" + RectJson(new Rect(absolute.x, absolute.y, component.width, component.height))
                + ",\"relativePosition\":{\"x\":" + JsonFloat(relative.x) + ",\"y\":" + JsonFloat(relative.y) + ",\"z\":" + JsonFloat(relative.z) + "}"
                + ",\"clipChildren\":" + Bool(component.clipChildren) + ",\"visibleSelf\":" + Bool(component.isVisibleSelf)
                + ",\"padding\":" + (padding == null ? "null" : "{\"left\":" + padding.left.ToString(CultureInfo.InvariantCulture)
                    + ",\"right\":" + padding.right.ToString(CultureInfo.InvariantCulture) + ",\"top\":" + padding.top.ToString(CultureInfo.InvariantCulture)
                    + ",\"bottom\":" + padding.bottom.ToString(CultureInfo.InvariantCulture) + "}") + "}";
        }

        private void PrepareCapture()
        {
            if (_cancelled) return;
            EnsureOwnedReady();
            if (!LoadingOverlayReadyForCapture())
            {
                _settleFrames = 0;
                if (_phase != "WaitingForLoadingOverlay") { _phase = "WaitingForLoadingOverlay"; WriteReport(); }
                return;
            }
            if (_tab == 7) { PrepareBudgetCapture(); return; }
            if (_noticePending) { PrepareNoticeCapture(); return; }
            CabinetRoot root = RequireCouncil();
            if (root.AdvisorMessageVisible) throw new InvalidOperationException("An unrequested own advisor notice is already visible; manual coverage cannot be inferred.");
            EnsureRenderPreconditions(root.CouncilPanel);
            if (_settleFrames == 0)
            {
                UIButton selector = root.GetAdvisorButton(_tab);
                EnsureRenderPreconditions(selector);
                if (!CouncilSelectorReadyForClick(root, selector)) return;
                RequireViewport(selector, true);
                if (!selector.isEnabled) throw new InvalidOperationException("Own advisor selector is disabled.");
                selector.SimulateClick();
                _selectorObservationActive = false;
            }
            if (++_settleFrames < 4) return;
            if (root.SelectedAdvisor != _tab) throw new InvalidOperationException("Advisor selection did not follow the normal own button action.");
            // The lifecycle controller refreshes on its own native cadence. A newer
            // runtime report may arrive before its LateUpdate; wait for the texts to agree.
            string content;
            long sequence;
            if (!CurrentAdvisorTextMatches(root, out content, out sequence) || !ValidateRciTooltips())
            {
                if (_phase != "WaitingForNativeUiData") { _phase = "WaitingForNativeUiData"; WriteReport(); }
                return;
            }
            UITextureSprite portrait = root.AdvisorPortrait;
            UILabel name = CouncilLabel(root, "Name");
            UILabel title = CouncilLabel(root, "Title");
            UILabel message = CouncilLabel(root, "Message");
            if (portrait == null || portrait.parent != root.CouncilContent || portrait.texture == null)
                throw new InvalidOperationException("The selected advisor portrait texture is unavailable.");
            if (VisibleRect(portrait).height < portrait.height - 1f || VisibleRect(name).height < name.height - 1f)
            {
                // Normal public scrolling of our own panel; long text is not required
                // to fit at once. Keep the portrait/name and a text excerpt in view.
                root.CouncilContent.scrollPosition = new Vector2(0f, Math.Max(0f, portrait.relativePosition.y + root.CouncilContent.scrollPosition.y - 8f));
                if (_settleFrames++ < 8) return;
            }
            EnsureRenderPreconditions(portrait);
            RequireViewport(portrait, true);
            EnsureRenderPreconditions(name);
            RequireViewport(name, true);
            EnsureRenderPreconditions(title);
            EnsureRenderPreconditions(message);
            Rect titleVisible = VisibleRect(title), messageVisible = VisibleRect(message);
            if ((titleVisible.width < 20f || titleVisible.height < 8f) && (messageVisible.width < 20f || messageVisible.height < 8f))
                throw new InvalidOperationException("No readable advisor title/message excerpt intersects the scroll viewport.");
            UIScrollablePanel body = root.CouncilContent;
            if (!body.clipChildren || body.scrollWheelAmount <= 0 || body.scrollWheelDirection != UIOrientation.Vertical || !body.builtinKeyNavigation)
                throw new InvalidOperationException("Own long council content lacks native clipping/scroll navigation.");
            _capturedCouncil = root.CouncilPanel;
            _capturedBody = body;
            _capturedPortrait = portrait.texture;
            _capturedScroll = body.scrollPosition;
            _capturedText = content;
            ValidateAvatarStrip(root, false);
            CaptureCouncilGeometry(root);
            _visualCheck = "{\"kind\":\"advisor\",\"advisor\":" + _tab.ToString(CultureInfo.InvariantCulture)
                + ",\"advisorId\":" + Json(((AdvisorId)_tab).ToString()) + ",\"sourceReportSequence\":" + sequence.ToString(CultureInfo.InvariantCulture)
                + ",\"exactNativeHost\":true,\"sevenExactSelectors\":true,\"textMatchesReport\":true,\"portraitTexturePresent\":true"
                + ",\"nameViewport\":" + RectJson(VisibleRect(name)) + ",\"portraitViewport\":" + RectJson(VisibleRect(portrait))
                + ",\"titleViewport\":" + RectJson(titleVisible) + ",\"messageViewport\":" + RectJson(messageVisible)
                + ",\"scrollNavigationAvailable\":true,\"scrollY\":" + JsonFloat(body.scrollPosition.y) + ",\"rci\":" + _rciCheck
                + ",\"avatarStrip\":" + _avatarStripCheck + ",\"geometryPins\":" + GeometryPinsJson(_councilGeometry) + "}";
            SubmitVisualCapture("council-advisor-" + _tab.ToString(CultureInfo.InvariantCulture) + "-" + ((AdvisorId)_tab).ToString() + ".png");
        }

        private void SubmitVisualCapture(string filename)
        {
            _capturePath = Path.Combine(_directory, filename);
            if (File.Exists(_capturePath)) throw new IOException("Refusing to overwrite a screenshot.");
            if (_cancelled) return;
            Application.CaptureScreenshot(_capturePath);
            _captureAt = Time.realtimeSinceStartup;
            _stage = 3;
            _phase = "WaitingForEngineScreenshot";
            WriteReport();
        }

        private void WaitForCapture()
        {
            EnsureOwnedReady();
            if (!LoadingOverlayReadyForCapture())
                throw new InvalidOperationException("Native loading overlay reappeared during asynchronous capture; PNG is not accepted or retried.");
            if (_tab == 7)
            {
                ValidateBudget();
                EnsureRenderPreconditions(_budgetPanel.component);
                EnsurePendingBudgetGeometry();
                RequireViewport(_budgetPanel.component, true);
            }
            else if (_noticePending) ValidateNoticeCapture(true);
            else
            {
                CabinetRoot root = RequireCouncil();
                if (root.AdvisorMessageVisible) throw new InvalidOperationException("Own notice appeared during council PNG; no recapture.");
                if (root.CouncilPanel != _capturedCouncil || root.CouncilContent != _capturedBody || root.SelectedAdvisor != _tab
                    || root.AdvisorPortrait == null || root.AdvisorPortrait != _capturedCouncilPortrait || root.AdvisorPortrait.texture != _capturedPortrait
                    || (root.CouncilContent.scrollPosition - _capturedScroll).sqrMagnitude > 0.01f
                    || ReadAdvisorText(root) != _capturedText)
                    throw new InvalidOperationException("Council identity, selected content, portrait or scroll changed while PNG was pending; no recapture.");
                EnsureRenderPreconditions(root.CouncilPanel);
                EnsureRenderPreconditions(root.AdvisorPortrait);
                RequireViewport(root.AdvisorPortrait, true);
                EnsureRenderPreconditions(CouncilLabel(root, "Name"));
                RequireViewport(CouncilLabel(root, "Name"), true);
                UILabel title = CouncilLabel(root, "Title"), message = CouncilLabel(root, "Message");
                EnsureRenderPreconditions(title);
                EnsureRenderPreconditions(message);
                Rect titleVisible = VisibleRect(title), messageVisible = VisibleRect(message);
                if ((titleVisible.width < 20f || titleVisible.height < 8f) && (messageVisible.width < 20f || messageVisible.height < 8f))
                    throw new InvalidOperationException("Advisor excerpt disappeared while PNG was pending; no recapture.");
                ValidateAvatarStrip(root, true);
                ValidateGeometryPins(_councilGeometry, root.CouncilDock);
            }
            if (Time.realtimeSinceStartup - _captureAt > 12f) throw new TimeoutException("Engine screenshot was not completed within 12 seconds.");
            if (!CompletePng(_capturePath)) return;
            _screenshots.Add(_capturePath);
            _renderChecks.Add("{\"capture\":" + Json(_tab == 7 ? "nativeBudget" : (_noticePending ? "manualNotice:" : "council:") + ((AdvisorId)_tab).ToString())
                + ",\"view\":" + _renderView + ",\"loadingOverlayState\":" + _loadingOverlayState + ",\"path\":" + _renderPath + ",\"nativeVisual\":" + _visualCheck + "}");
            if (_tab == 7)
            {
                int expected = _request.CaptureAllTabs ? 7 : 1;
                if (_advisorChecks.Count != expected || _noticeChecks.Count != expected)
                    throw new InvalidOperationException("Native visual completion requires every requested council and manual notice capture.");
                Finish("Completed", null);
                return;
            }
            if (!_noticePending)
            {
                _advisorChecks.Add(_visualCheck);
                _noticePending = true;
                _settleFrames = 0;
                _stage = 2;
                _phase = "OpeningManualAdvisorNotice";
                WriteReport();
                return;
            }
            _noticeChecks.Add(_visualCheck);
            CloseCapturedNotice();
            _noticePending = false;
            _tab = _request.CaptureAllTabs && _tab < 6 ? _tab + 1 : 7;
            _settleFrames = 0;
            _stage = 2;
            _phase = _tab == 7 ? "OpeningNativeBudget" : "SelectingNextAdvisor";
            WriteReport();
        }

        private void PrepareNoticeCapture()
        {
            CabinetRoot root = RequireCouncil();
            if (!_noticeOpenRequested)
            {
                if (!TryOpenManualNotice(root, _tab)) return;
                _settleFrames = 0;
                return;
            }
            if (++_settleFrames < 4) return;
            ValidateNoticeCapture(false);
            _noticeText = ReadNoticeText();
            _noticeTexture = _noticePortrait.texture;
            _noticePanelRect = ComponentRect(_noticePanel);
            _noticePortraitRect = ComponentRect(_noticePortrait);
            _noticeFraming = PortraitFraming(_noticePortrait);
            _noticeScrollPosition = _noticeScroll.scrollPosition;
            CaptureNoticeGeometry();
            _visualCheck = "{\"kind\":\"manualAdvisorNotice\",\"advisorId\":" + Json(_noticeBriefing.Advisor.ToString())
                + ",\"sourceReportSequence\":" + _noticeReportSequence.ToString(CultureInfo.InvariantCulture)
                + ",\"sourcePopulation\":" + _noticePopulation.ToString(CultureInfo.InvariantCulture)
                + ",\"manualReadButtonInvoked\":true,\"briefingMatchesRealReport\":true,\"automaticCriticalAlertVerified\":false"
                + ",\"pendingAlertsBefore\":" + _noticePendingAlertsBefore.ToString(CultureInfo.InvariantCulture)
                + ",\"pendingAlertsAfter\":" + _noticePendingAlertsAfter.ToString(CultureInfo.InvariantCulture)
                + ",\"briefing\":" + _noticeBriefingSignature + ",\"panelRect\":" + RectJson(_noticePanelRect)
                + ",\"portraitRect\":" + RectJson(_noticePortraitRect) + ",\"portraitFraming\":" + RectJson(_noticeFraming)
                + ",\"bodyViewport\":" + RectJson(VisibleRect(NoticeLabel("Body"))) + ",\"scrollNavigationAvailable\":true"
                + ",\"geometryPins\":" + GeometryPinsJson(_noticeGeometry) + "}";
            SubmitVisualCapture("advisor-notice-" + _tab.ToString(CultureInfo.InvariantCulture) + "-" + ((AdvisorId)_tab).ToString() + ".png");
        }

        private bool TryOpenManualNotice(CabinetRoot root, int advisor)
        {
            if (root.SelectedAdvisor != advisor) throw new InvalidOperationException("Wrong selected advisor before manual notice.");
            if (root.AdvisorMessageVisible) throw new InvalidOperationException("An unrequested advisor notice is already open; no automatic coverage is inferred.");
            UIButton read = root.CouncilReadNotice;
            if (read == null || read.name != "SC4EvolutionCouncilReadNotice" || read.parent != root.CouncilContent || !read.isEnabled)
                throw new InvalidOperationException("Exact own Ler aviso control is unavailable.");
            ScrollOwnControlFullyIntoView(root.CouncilContent, read);
            if (++_settleFrames < 4) return false;
            EnsureRenderPreconditions(read);
            RequireViewport(read, true);
            var report = ModRuntime.CurrentReport;
            AdvisorBriefing source = ReportBriefing(report, advisor);
            _noticeBriefing = CopyBriefing(source);
            _noticeBriefingSignature = BriefingJson(_noticeBriefing);
            _noticeReportSequence = report.Sequence;
            _noticePopulation = report.Snapshot.Population;
            _noticePendingAlertsBefore = root.PendingAdvisorAlerts;
            // Intentional manual action. Nonmodal tutorials remain untouched; the
            // product's automatic-alert policy is a different, unverified path.
            read.SimulateClick();
            // The simulation can publish while this main-thread action reads it.
            // Accept only a complete real snapshot observed on either side of the
            // click, never a synthetic mixture of fields from separate reports.
            if (BriefingJson(root.AdvisorMessageBriefing) != _noticeBriefingSignature)
            {
                var after = ModRuntime.CurrentReport;
                AdvisorBriefing fresh = CopyBriefing(ReportBriefing(after, advisor));
                string freshSignature = BriefingJson(fresh);
                if (BriefingJson(root.AdvisorMessageBriefing) != freshSignature)
                    throw new InvalidOperationException("Manual notice does not match either observed real report snapshot.");
                _noticeBriefing = fresh;
                _noticeBriefingSignature = freshSignature;
                _noticeReportSequence = after.Sequence;
                _noticePopulation = after.Snapshot.Population;
            }
            _noticeRoot = root;
            _noticePanel = root.AdvisorMessagePanel;
            _noticePortrait = root.AdvisorMessagePortrait;
            _noticeClose = root.AdvisorMessageCloseButton;
            _noticeAction = root.AdvisorMessageActionButton;
            _noticeScroll = _noticePanel == null ? null : _noticePanel.Find<UIScrollablePanel>("SC4EvolutionAdvisorMessageScroll");
            _noticeOpenRequested = true;
            _noticePendingAlertsAfter = root.PendingAdvisorAlerts;
            RequireNotice();
            UITextureSprite avatar = root.GetAdvisorAvatar(advisor);
            if (avatar == null || avatar.texture == null || _noticePortrait.texture != avatar.texture
                || !SameBudgetGeometry(PortraitFraming(avatar), PortraitFraming(_noticePortrait)))
                throw new InvalidOperationException("Manual notice portrait does not match the selected real advisor avatar.");
            return true;
        }

        private CabinetRoot RequireNotice()
        {
            CabinetRoot root = FindRoot();
            if (root == null || root != _noticeRoot || _noticePanel == null || root.AdvisorMessagePanel != _noticePanel
                || _noticePanel.name != "SC4EvolutionAdvisorMessage" || !root.AdvisorMessageVisible || !_noticePanel.isVisible
                || _noticeBriefing == null || root.SelectedAdvisor != (int)_noticeBriefing.Advisor
                || BriefingJson(root.AdvisorMessageBriefing) != _noticeBriefingSignature
                || _noticePortrait == null || root.AdvisorMessagePortrait != _noticePortrait || _noticePortrait.parent != _noticePanel
                || _noticePortrait.name != "SC4EvolutionAdvisorMessagePortrait"
                || _noticeClose == null || root.AdvisorMessageCloseButton != _noticeClose || _noticeClose.parent != _noticePanel
                || _noticeClose.name != "SC4EvolutionAdvisorMessageClose"
                || _noticeAction == null || root.AdvisorMessageActionButton != _noticeAction || _noticeAction.parent != _noticePanel
                || _noticeAction.name != "SC4EvolutionAdvisorMessageAction" || _noticeScroll == null || _noticeScroll.parent != _noticePanel)
                throw new InvalidOperationException("Manual advisor notice identity, real briefing content or visibility changed.");
            return root;
        }

        private void ValidateNoticeCapture(bool pending)
        {
            RequireNotice();
            EnsureRenderPreconditions(_noticePanel);
            RequireViewport(_noticePanel, true);
            EnsureRenderPreconditions(_noticePortrait);
            RequireViewport(_noticePortrait, true);
            if (_noticePortrait.texture == null || Math.Abs(Math.Max(_noticePortrait.width, _noticePortrait.height) - 128f) > 0.01f)
                throw new InvalidOperationException("Manual notice requires its real portrait at a 128 logical-pixel maximum edge.");
            if (!_noticeScroll.clipChildren || !_noticeScroll.builtinKeyNavigation || _noticeScroll.scrollWheelAmount <= 0
                || _noticeScroll.scrollWheelDirection != UIOrientation.Vertical)
                throw new InvalidOperationException("Manual notice text lacks native scroll navigation.");
            string mood = _noticeBriefing.Mood == AdvisorMood.Calm ? GameLanguage.Text("Calmo", "Calm") : _noticeBriefing.Mood == AdvisorMood.Concerned ? GameLanguage.Text("Preocupado", "Concerned")
                : _noticeBriefing.Mood == AdvisorMood.Angry ? GameLanguage.Text("Insatisfeito", "Unhappy") : _noticeBriefing.Mood == AdvisorMood.Pleased ? GameLanguage.Text("Satisfeito", "Pleased") : string.Empty;
            string[] names = { "Name", "Department", "Mood", "Title", "Metric", "Body" };
            string[] expected = { _noticeBriefing.Name, _noticeBriefing.Department, mood, _noticeBriefing.Title, _noticeBriefing.MetricText, NoticeBody(_noticeBriefing) };
            for (int i = 0; i < names.Length; ++i)
            {
                UILabel label = NoticeLabel(names[i]);
                if (!string.Equals(label.text, expected[i] ?? string.Empty, StringComparison.Ordinal))
                    throw new InvalidOperationException("Manual notice text differs from its real source briefing: " + names[i]);
                if (!string.IsNullOrEmpty(expected[i])) EnsureRenderPreconditions(label);
            }
            RequireViewport(NoticeLabel("Name"), true);
            Rect title = VisibleRect(NoticeLabel("Title")), body = VisibleRect(NoticeLabel("Body"));
            if ((title.width < 20f || title.height < 8f) && (body.width < 20f || body.height < 8f))
                throw new InvalidOperationException("Manual notice has no visible title/message excerpt.");
            EnsureRenderPreconditions(_noticeClose);
            RequireViewport(_noticeClose, true);
            if (pending && (_noticePortrait.texture != _noticeTexture || ReadNoticeText() != _noticeText
                || !SameBudgetGeometry(_noticePanelRect, ComponentRect(_noticePanel))
                || !SameBudgetGeometry(_noticePortraitRect, ComponentRect(_noticePortrait))
                || !SameBudgetGeometry(_noticeFraming, PortraitFraming(_noticePortrait))
                || (_noticeScroll.scrollPosition - _noticeScrollPosition).sqrMagnitude > 0.0001f))
                throw new InvalidOperationException("Manual notice text/portrait/framing/geometry/scroll changed while PNG was pending; no recapture.");
            if (pending) ValidateGeometryPins(_noticeGeometry, _noticePanel);
        }

        private void CloseCapturedNotice()
        {
            RequireNotice();
            EnsureRenderPreconditions(_noticeClose);
            RequireViewport(_noticeClose, true);
            _noticeClose.SimulateClick();
            if (_noticeRoot.AdvisorMessageVisible || _noticePanel.isVisible)
                throw new InvalidOperationException("Normal own notice close action did not hide the notice.");
            _noticeOpenRequested = false;
        }

        private UILabel NoticeLabel(string suffix)
        {
            UILabel label = _noticePanel.Find<UILabel>("SC4EvolutionAdvisorMessage" + suffix);
            if (label == null || label.parent != _noticeScroll) throw new InvalidOperationException("Exact advisor notice label missing: " + suffix);
            return label;
        }

        private string ReadNoticeText()
        {
            StringBuilder text = new StringBuilder();
            foreach (string suffix in new string[] { "Name", "Department", "Mood", "Title", "Metric", "Body" }) text.Append(Json(NoticeLabel(suffix).text)).Append('|');
            return text.ToString();
        }

        private static string NoticeBody(AdvisorBriefing briefing)
        {
            string body = briefing.Message ?? string.Empty;
            if (!string.IsNullOrEmpty(briefing.Detail) && !string.Equals(body, briefing.Detail, StringComparison.Ordinal))
                body = body.Length == 0 ? briefing.Detail : body + "\n\n" + briefing.Detail;
            return body;
        }

        private static AdvisorBriefing ReportBriefing(CabinetReport report, int advisor)
        {
            if (report == null || report.Snapshot == null || !report.Snapshot.DataValid || report.Briefings == null || report.Briefings.Length != 7)
                throw new InvalidOperationException("Manual notice needs the seven real briefings of a valid live report.");
            AdvisorBriefing found = null;
            foreach (AdvisorBriefing briefing in report.Briefings)
                if (briefing != null && (int)briefing.Advisor == advisor)
                {
                    if (found != null) throw new InvalidOperationException("Duplicate real advisor briefing.");
                    found = briefing;
                }
            if (found == null) throw new InvalidOperationException("Requested real advisor briefing is unavailable.");
            return found;
        }

        private static AdvisorBriefing CopyBriefing(AdvisorBriefing value)
        {
            return new AdvisorBriefing { Advisor = value.Advisor, Name = value.Name, Department = value.Department, Mood = value.Mood,
                Title = value.Title, MetricText = value.MetricText, Message = value.Message, Detail = value.Detail, Action = value.Action,
                Severity = value.Severity, ConcernDays = value.ConcernDays, IsSuspended = value.IsSuspended };
        }

        private static string BriefingJson(AdvisorBriefing value)
        {
            if (value == null) return "null";
            return "{\"advisor\":" + Json(value.Advisor.ToString()) + ",\"name\":" + Json(value.Name) + ",\"department\":" + Json(value.Department)
                + ",\"mood\":" + Json(value.Mood.ToString()) + ",\"title\":" + Json(value.Title) + ",\"metric\":" + Json(value.MetricText)
                + ",\"message\":" + Json(value.Message) + ",\"detail\":" + Json(value.Detail) + ",\"action\":" + Json(value.Action.ToString())
                + ",\"severity\":" + Json(value.Severity.ToString()) + ",\"concernDays\":" + value.ConcernDays.ToString(CultureInfo.InvariantCulture)
                + ",\"isSuspended\":" + Bool(value.IsSuspended) + "}";
        }

        private static Rect PortraitFraming(UITextureSprite portrait)
        {
            FramedAdvisorPortrait framed = portrait as FramedAdvisorPortrait;
            if (framed == null) throw new InvalidOperationException("Expected real framed advisor portrait component.");
            Rect bounds = framed.FramingBounds;
            if (!Finite(bounds.x) || !Finite(bounds.y) || !Finite(bounds.width) || !Finite(bounds.height)
                || bounds.x < 0f || bounds.y < 0f || bounds.width <= 0f || bounds.height <= 0f || bounds.xMax > 1.0001f || bounds.yMax > 1.0001f)
                throw new InvalidOperationException("Advisor portrait framing is invalid.");
            return bounds;
        }

        private void ValidateAvatarStrip(CabinetRoot root, bool pending)
        {
            List<string> checks = new List<string>();
            var report = ModRuntime.CurrentReport;
            for (int i = 0; i < 7; ++i)
            {
                UITextureSprite avatar = root.GetAdvisorAvatar(i);
                UIButton button = root.GetAdvisorButton(i);
                if (avatar == null || avatar.parent != button || avatar.texture == null) throw new InvalidOperationException("Real advisor face-strip texture/parent missing: " + i);
                EnsureRenderPreconditions(avatar);
                RequireViewport(avatar, true);
                Rect framing = PortraitFraming(avatar);
                if (pending)
                {
                    if (avatar != _capturedAvatars[i] || button != _capturedAvatarButtons[i] || avatar.texture != _capturedAvatarTextures[i] || !SameBudgetGeometry(framing, _capturedAvatarFraming[i]))
                        throw new InvalidOperationException("Real advisor face strip changed while council PNG was pending; no recapture.");
                }
                else
                {
                    AdvisorBriefing briefing = ReportBriefing(report, i);
                    if (button.tooltip != briefing.Name + "\n" + briefing.Department) throw new InvalidOperationException("Face-strip tooltip differs from the real advisor identity.");
                    _capturedAvatars[i] = avatar;
                    _capturedAvatarButtons[i] = button;
                    _capturedAvatarTextures[i] = avatar.texture;
                    _capturedAvatarFraming[i] = framing;
                    checks.Add("{\"advisorId\":" + Json(((AdvisorId)i).ToString()) + ",\"texturePresent\":true,\"identityTooltipMatchesReport\":true"
                        + ",\"framing\":" + RectJson(framing) + ",\"viewport\":" + RectJson(VisibleRect(avatar)) + "}");
                }
            }
            if (!pending) _avatarStripCheck = "[" + string.Join(",", checks.ToArray()) + "]";
        }

        private sealed class VisualGeometryPin
        {
            public UIComponent Component, Parent;
            public Rect Bounds;
            public string Name;
            public bool ZeroExtentDock;
        }

        private void CaptureCouncilGeometry(CabinetRoot root)
        {
            _councilGeometry.Clear();
            _capturedCouncilPortrait = root.AdvisorPortrait;
            AddGeometryPin(_councilGeometry, root.CouncilDock, true);
            AddGeometryPin(_councilGeometry, root.CouncilPanel);
            AddGeometryPin(_councilGeometry, root.CouncilContent);
            AddGeometryPin(_councilGeometry, root.AdvisorPortrait);
            AddGeometryPin(_councilGeometry, root.CouncilToggle);
            foreach (string suffix in AdvisorTextNames) AddGeometryPin(_councilGeometry, CouncilLabel(root, suffix));
            AddGeometryPin(_councilGeometry, root.CouncilReadNotice);
            AddGeometryPin(_councilGeometry, root.CouncilPanel.Find<UIButton>("SC4EvolutionCouncilAction"));
            for (int i = 0; i < 7; ++i)
            {
                AddGeometryPin(_councilGeometry, root.GetAdvisorButton(i));
                AddGeometryPin(_councilGeometry, root.GetAdvisorAvatar(i));
            }
        }

        private void CaptureNoticeGeometry()
        {
            _noticeGeometry.Clear();
            AddGeometryPin(_noticeGeometry, _noticePanel);
            AddGeometryPin(_noticeGeometry, _noticePortrait);
            AddGeometryPin(_noticeGeometry, _noticeScroll);
            foreach (string suffix in new string[] { "Name", "Department", "Mood", "Title", "Metric", "Body" })
            {
                UILabel label = NoticeLabel(suffix);
                // An empty, locally hidden label has no rendered rectangle. Its
                // identity/text is still checked by NoticeLabel and briefing checks.
                if (label.isVisibleSelf || !string.IsNullOrEmpty(label.text)) AddGeometryPin(_noticeGeometry, label);
            }
            AddGeometryPin(_noticeGeometry, _noticeClose);
            AddGeometryPin(_noticeGeometry, _noticeAction);
            foreach (string suffix in new string[] { "Scrollbar", "ScrollTrack", "ScrollThumb" })
            {
                UIComponent component = _noticePanel.Find<UIComponent>("SC4EvolutionAdvisorMessage" + suffix);
                if (component != null && component.isVisible) AddGeometryPin(_noticeGeometry, component);
            }
        }

        private static void AddGeometryPin(List<VisualGeometryPin> pins, UIComponent component, bool zeroExtentDock = false)
        {
            if (component == null) throw new InvalidOperationException("A required own visual component disappeared before geometry pinning.");
            pins.Add(new VisualGeometryPin { Component = component, Parent = component.parent, Bounds = ComponentRect(component, zeroExtentDock), Name = component.name, ZeroExtentDock = zeroExtentDock });
        }

        private static void ValidateGeometryPins(List<VisualGeometryPin> pins, UIComponent scope)
        {
            if (pins.Count == 0) throw new InvalidOperationException("Pending own UI screenshot has no geometry pins.");
            foreach (VisualGeometryPin pin in pins)
            {
                if (pin.Component == null || pin.Component.parent != pin.Parent || pin.Component.name != pin.Name
                    || (pin.Component != scope && scope.Find<UIComponent>(pin.Name) != pin.Component))
                    throw new InvalidOperationException("Own UI component identity/parent changed while PNG was pending: " + pin.Name + "; no recapture.");
                Rect current = ComponentRect(pin.Component, pin.ZeroExtentDock);
                if (!SameBudgetGeometry(pin.Bounds, current))
                    throw new InvalidOperationException("Own UI geometry changed while PNG was pending: " + pin.Name
                        + "; pinned=" + RectJson(pin.Bounds) + "; current=" + RectJson(current) + "; no recapture.");
            }
        }

        private static string GeometryPinsJson(List<VisualGeometryPin> pins)
        {
            List<string> values = new List<string>();
            foreach (VisualGeometryPin pin in pins)
                values.Add("{\"name\":" + Json(pin.Name) + ",\"parent\":" + Json(pin.Parent == null ? null : pin.Parent.name)
                    + ",\"componentRect\":" + RectJson(pin.Bounds) + ",\"toleranceLogicalPixels\":0.01}");
            return "[" + string.Join(",", values.ToArray()) + "]";
        }

        private static void ScrollOwnControlFullyIntoView(UIScrollablePanel body, UIComponent control)
        {
            body.ScrollIntoView(control);
            Rect visible = VisibleRect(control);
            if (visible.height < control.height - 1f)
            {
                float contentY = control.relativePosition.y + body.scrollPosition.y;
                float alignedY = body.height - body.scrollPadding.bottom - control.height - 8f;
                body.scrollPosition = new Vector2(0f, Math.Max(0f, contentY - alignedY));
            }
        }

        private void PrepareBudgetCapture()
        {
            if (!_budgetOpened)
            {
                CabinetRoot root = RequireCouncil();
                if (!_noticeOpenRequested)
                {
                    if (_settleFrames == 0)
                    {
                        UIButton finance = root.GetAdvisorButton(0);
                        EnsureRenderPreconditions(finance);
                        if (!CouncilSelectorReadyForClick(root, finance)) return;
                        RequireViewport(finance, true);
                        finance.SimulateClick();
                        _selectorObservationActive = false;
                    }
                    if (!TryOpenManualNotice(root, 0)) return;
                    _settleFrames = 0;
                    return;
                }
                ValidateNoticeCapture(false);
                if (++_settleFrames < 4) return;
                if (!_noticeAction.isEnabled || _noticeAction.text != GameLanguage.Text("Abrir orçamento", "Open budget"))
                    throw new InvalidOperationException("Finance notice budget action is unavailable.");
                EnsureRenderPreconditions(_noticeAction);
                RequireViewport(_noticeAction, true);
                _budgetCouncilRoot = root;
                _budgetCityPanel = CityInfoPanel.instance;
                _budgetCityHost = root.CityHost;
                _budgetCouncilPanel = root.CouncilPanel;
                _budgetCouncilDock = root.CouncilDock;
                _budgetMessagePanel = _noticePanel;
                _noticeAction.SimulateClick();
                _noticeOpenRequested = false;
                _budgetPanel = ToolsModifierControl.economyPanel;
                _budgetOpened = true;
                _budgetAnimationKey = _budgetPanel == null ? null : _budgetPanel.GetType().ToString();
                _settleFrames = 0;
                return;
            }
            ValidateBudget();
            EnsureRenderPreconditions(_budgetPanel.component);
            if (!BudgetAnimationAndGeometryReady())
            {
                if (_phase != "WaitingForNativeBudgetAnimation") { _phase = "WaitingForNativeBudgetAnimation"; WriteReport(); }
                return;
            }
            RequireViewport(_budgetPanel.component, true);
            _budgetValidated = true;
            _capturedBudgetRect = ComponentRect(_budgetPanel.component);
            _budgetCaptureGeometryPinned = _budgetCaptureGeometryUnchanged = true;
            _visualCheck = "{\"kind\":\"nativeBudget\",\"exactEconomyPanel\":true,\"selectedTab\":"
                + _budgetPanel.selectedTab.ToString(CultureInfo.InvariantCulture) + ",\"expectedBudgetTab\":"
                + EconomyPanel.kBudgetTab.ToString(CultureInfo.InvariantCulture) + ",\"viewport\":" + RectJson(VisibleRect(_budgetPanel.component))
                + ",\"animationAndGeometry\":" + _budgetAnimationState + ",\"pinnedComponentRect\":" + RectJson(_capturedBudgetRect)
                + ",\"sourceHostVisibility\":" + _budgetHostVisibilityState + "}";
            SubmitVisualCapture("native-budget.png");
        }

        private bool BudgetAnimationAndGeometryReady()
        {
            // EconomyPanel.Show/Hide use GetType().ToString() as the public animator
            // key. Observe its completion; never cancel it or alter panel geometry.
            bool animating = IsBudgetAnimating();
            Rect current = ComponentRect(_budgetPanel.component);
            float now = Time.realtimeSinceStartup;
            if (animating)
            {
                _budgetGeometryObserved = false;
                _budgetGeometryStableSince = -1f;
                _settleFrames = 0;
            }
            else if (!_budgetGeometryObserved || !SameBudgetGeometry(_budgetObservedRect, current))
            {
                _budgetObservedRect = current;
                _budgetGeometryObserved = true;
                _budgetGeometryStableSince = now;
                _settleFrames = 0;
            }
            else ++_settleFrames;
            float stableSeconds = _budgetGeometryStableSince < 0f ? 0f : now - _budgetGeometryStableSince;
            bool ready = !animating && stableSeconds >= 0.25f && _settleFrames >= 4;
            RecordBudgetAnimationState(animating, current, stableSeconds, ready);
            if (!ready) _renderPreconditionsValid = false;
            return ready;
        }

        private bool IsBudgetAnimating()
        {
            if (!Singleton<ValueAnimator>.exists || string.IsNullOrEmpty(_budgetAnimationKey))
                throw new InvalidOperationException("Native budget ValueAnimator/key is unavailable; animation completion cannot be established.");
            return ValueAnimator.IsAnimating(_budgetAnimationKey);
        }

        private void EnsurePendingBudgetGeometry()
        {
            bool animating = IsBudgetAnimating();
            Rect current = ComponentRect(_budgetPanel.component);
            _budgetCaptureGeometryUnchanged = _budgetCaptureGeometryPinned && !animating && SameBudgetGeometry(_capturedBudgetRect, current);
            RecordBudgetAnimationState(animating, current,
                _budgetGeometryStableSince < 0f ? 0f : Time.realtimeSinceStartup - _budgetGeometryStableSince, _budgetCaptureGeometryUnchanged);
            if (!_budgetCaptureGeometryUnchanged)
                throw new InvalidOperationException("Native budget animation/position/size changed while PNG was pending; no recapture. pinned="
                    + RectJson(_capturedBudgetRect) + ", current=" + RectJson(current) + ", animating=" + animating);
        }

        private void RecordBudgetAnimationState(bool animating, Rect current, float stableSeconds, bool ready)
        {
            _budgetAnimationState = "{\"key\":" + Json(_budgetAnimationKey) + ",\"isAnimating\":" + Bool(animating)
                + ",\"configuredShowHideSeconds\":" + JsonFloat(_budgetPanel.m_ShowHideTime)
                + ",\"requiredStableSeconds\":0.25,\"observedStableSeconds\":" + JsonFloat(stableSeconds)
                + ",\"requiredStableUpdates\":4,\"observedStableUpdates\":" + _settleFrames.ToString(CultureInfo.InvariantCulture)
                + ",\"geometryToleranceLogicalPixels\":0.01,\"componentRect\":" + RectJson(current) + ",\"ready\":" + Bool(ready) + "}";
        }

        private static bool SameBudgetGeometry(Rect expected, Rect actual)
        {
            return Math.Abs(expected.x - actual.x) <= 0.01f && Math.Abs(expected.y - actual.y) <= 0.01f
                && Math.Abs(expected.width - actual.width) <= 0.01f && Math.Abs(expected.height - actual.height) <= 0.01f;
        }

        private void ValidateBudget()
        {
            UIComponent component = UIView.Find("EconomyPanel");
            if (_budgetPanel == null || ToolsModifierControl.economyPanel != _budgetPanel || component == null
                || component.GetComponent<EconomyPanel>() != _budgetPanel || _budgetPanel.component != component
                || !_budgetPanel.isVisible || _budgetPanel.selectedTab != EconomyPanel.kBudgetTab || !_budgetPanel.isBudgetOpen)
                throw new InvalidOperationException("Actual native EconomyPanel identity/visible budget tab precondition failed.");
            ValidateBudgetHostHidden();
        }

        private void ValidateBudgetHostHidden()
        {
            CabinetRoot root = FindRoot();
            CityInfoPanel city = CityInfoPanel.instance;
            bool exact = _budgetCouncilRoot != null && root == _budgetCouncilRoot && _budgetCityPanel != null && city == _budgetCityPanel
                && _budgetCityHost != null && city.component == _budgetCityHost && root.CityHost == _budgetCityHost
                && _budgetCouncilDock != null && root.CouncilDock == _budgetCouncilDock && _budgetCouncilDock.parent == _budgetCityHost
                && _budgetCouncilDock.size == Vector2.zero && _budgetCouncilDock.relativePosition.sqrMagnitude <= 0.0001f
                && !_budgetCouncilDock.clipChildren && !_budgetCouncilDock.autoLayout
                && !_budgetCouncilDock.autoFitChildrenHorizontally && !_budgetCouncilDock.autoFitChildrenVertically
                && _budgetCouncilPanel != null && root.CouncilPanel == _budgetCouncilPanel && _budgetCouncilPanel.parent == _budgetCouncilDock
                && _budgetMessagePanel != null && root.AdvisorMessagePanel == _budgetMessagePanel;
            // UIComponent.isVisible includes inherited parent visibility (unlike
            // isVisibleSelf). Native OnCloseButton hides the CityInfo host, which
            // also prevents its council subtree from rendering even if locally shown.
            bool cityVisible = _budgetCityHost != null && _budgetCityHost.isVisible;
            bool councilVisible = _budgetCouncilPanel != null && _budgetCouncilPanel.isVisible;
            bool dockVisible = _budgetCouncilDock != null && _budgetCouncilDock.isVisible;
            bool messageVisible = _budgetMessagePanel != null && _budgetMessagePanel.isVisible;
            bool messageControllerVisible = root != null && root.AdvisorMessageVisible;
            _budgetHostNonOccluding = exact && !cityVisible && !dockVisible && !councilVisible && !messageVisible && !messageControllerVisible;
            _budgetHostVisibilityState = "{\"exactPinnedSourceIdentity\":" + Bool(exact)
                + ",\"cityInfoVisible\":" + Bool(cityVisible) + ",\"cityInfoVisibleSelf\":" + Bool(_budgetCityHost != null && _budgetCityHost.isVisibleSelf)
                + ",\"councilVisible\":" + Bool(councilVisible) + ",\"councilVisibleSelf\":" + Bool(_budgetCouncilPanel != null && _budgetCouncilPanel.isVisibleSelf)
                + ",\"councilDockVisible\":" + Bool(dockVisible)
                + ",\"advisorMessageVisible\":" + Bool(messageVisible) + ",\"advisorMessageControllerVisible\":" + Bool(messageControllerVisible)
                + ",\"sourceHostNonOccluding\":" + Bool(_budgetHostNonOccluding) + "}";
            if (!_budgetHostNonOccluding)
                throw new InvalidOperationException("Native budget evidence is blocked by its source CityInfo/council host or source identity changed: "
                    + _budgetHostVisibilityState + ". The driver does not hide, reorder or recapture the UI.");
        }

        private static UILabel CouncilLabel(CabinetRoot root, string suffix)
        {
            UILabel label = root.CouncilPanel.Find<UILabel>("SC4EvolutionCouncil" + suffix);
            if (label == null || label.parent != root.CouncilContent) throw new InvalidOperationException("Own council label missing: " + suffix);
            return label;
        }

        private static string ReadAdvisorText(CabinetRoot root)
        {
            StringBuilder value = new StringBuilder();
            foreach (string suffix in AdvisorTextNames) value.Append(Json(CouncilLabel(root, suffix).text)).Append('|');
            value.Append(Json(CouncilLabel(root, "Message").tooltip));
            return value.ToString();
        }

        private bool CurrentAdvisorTextMatches(CabinetRoot root, out string content, out long sequence)
        {
            content = ReadAdvisorText(root);
            var report = ModRuntime.CurrentReport;
            sequence = report == null ? -1 : report.Sequence;
            AdvisorBriefing selected = null;
            if (report == null || report.Briefings == null) return false;
            if (report.Briefings.Length != 7) throw new InvalidOperationException("Live report must contain exactly seven advisors.");
            HashSet<AdvisorId> advisorIds = new HashSet<AdvisorId>();
            foreach (AdvisorBriefing briefing in report.Briefings)
            {
                if (briefing == null || (int)briefing.Advisor < 0 || (int)briefing.Advisor > 6 || !advisorIds.Add(briefing.Advisor))
                    throw new InvalidOperationException("Live report contains a missing/invalid/duplicate advisor.");
                if ((int)briefing.Advisor == _tab)
                {
                    selected = briefing;
                }
            }
            if (selected == null) return false;
            string mood = selected.Mood == AdvisorMood.Concerned ? GameLanguage.Text("Preocupado", "Concerned") : selected.Mood == AdvisorMood.Angry ? GameLanguage.Text("Irritado", "Angry")
                : selected.Mood == AdvisorMood.Pleased ? GameLanguage.Text("Satisfeito", "Pleased") : GameLanguage.Text("Tranquilo", "Calm");
            string[] expected = { selected.Name, selected.Department, GameLanguage.Text("Humor: ", "Mood: ") + mood, selected.Title, selected.MetricText, selected.Message };
            for (int i = 0; i < expected.Length; ++i)
                if (!string.Equals(CouncilLabel(root, AdvisorTextNames[i]).text, expected[i], StringComparison.Ordinal)) return false;
            return string.Equals(CouncilLabel(root, "Message").tooltip, selected.Detail, StringComparison.Ordinal);
        }

        private bool ValidateRciTooltips()
        {
            var demand = ModRuntime.CurrentDemand;
            if (demand == null || !demand.Converged) return false;
            InfoPanel info = null;
            foreach (InfoPanel candidate in UIView.GetAView().GetComponentsInChildren<InfoPanel>(true))
                if (candidate.Find<UISlider>("ResidentialDemand") != null)
                {
                    if (info != null) throw new InvalidOperationException("Native RCI InfoPanel identity is ambiguous.");
                    info = candidate;
                }
            if (info == null) throw new InvalidOperationException("Native RCI InfoPanel is missing.");
            string[] names = { "ResidentialDemand", "CommercialDemand", "IndustryOfficeDemand" };
            uint[][] keys = { new uint[] { 0x21010, 0x21020, 0x21030 }, new uint[] { 0x23110, 0x23120, 0x23130 },
                new uint[] { 0x23320, 0x23330, 0x24100, 0x24200, 0x24300, 0x24400 } };
            string[][] labels = { new string[] { "R$", "R$$", "R$$$" }, new string[] { GameLanguage.Text("Serviços $", "Services $"), GameLanguage.Text("Serviços $$", "Services $$"), GameLanguage.Text("Serviços $$$", "Services $$$") },
                new string[] { GameLanguage.Text("Escritórios $$", "Offices $$"), GameLanguage.Text("Escritórios $$$", "Offices $$$"), GameLanguage.Text("Agricultura", "Agriculture"), GameLanguage.Text("Indústria suja", "Polluting industry"), GameLanguage.Text("Manufatura", "Manufacturing"), GameLanguage.Text("Alta tecnologia", "High tech") } };
            List<string> checks = new List<string>();
            for (int i = 0; i < names.Length; ++i)
            {
                UISlider slider = info.Find<UISlider>(names[i]);
                if (slider == null) throw new InvalidOperationException("Existing native RCI slider missing: " + names[i]);
                StringBuilder expected = new StringBuilder(demand.GameplayEnabled ? GameLanguage.Text("\n\nDemanda por categoria", "\n\nDemand by category") : GameLanguage.Text("\n\nProjeção por categoria", "\n\nProjection by category"));
                for (int k = 0; k < keys[i].Length; ++k)
                {
                    int value;
                    if (!demand.TryGetDemand(keys[i][k], out value)) throw new InvalidOperationException("Required RCI graph category missing.");
                    expected.Append('\n').Append(labels[i][k]).Append(": ").Append(Math.Max(-1000, Math.Min(1000, value)).ToString("+0;-0;0", CultureInfo.CurrentCulture));
                }
                expected.Append(GameLanguage.Text("\nÍndice −1000 a +1000. Valores positivos indicam procura por novas construções.", "\nIndex −1000 to +1000. Positive values indicate demand for new buildings."));
                if (slider.tooltip == null || !slider.tooltip.EndsWith(expected.ToString(), StringComparison.Ordinal)) return false;
                checks.Add("{\"nativeSlider\":" + Json(names[i]) + ",\"categoryValuesMatch\":true,\"ownedSuffix\":" + Json(expected.ToString()) + "}");
            }
            _rciCheck = "[" + string.Join(",", checks.ToArray()) + "]";
            return true;
        }

        private static Rect ComponentRect(UIComponent component)
        {
            return ComponentRect(component, false);
        }

        private static Rect ComponentRect(UIComponent component, bool zeroExtentDock)
        {
            // absolutePosition and size share UIView logical pixel coordinates. Refuse
            // transformed replacement layouts rather than pretending axis-aligned bounds.
            RequireAxisAlignedTransform(component);
            Vector3 position = component.absolutePosition;
            if (!Finite(position.x) || !Finite(position.y) || !Finite(component.width) || !Finite(component.height)
                || (zeroExtentDock ? component.width != 0f || component.height != 0f : component.width <= 0f || component.height <= 0f))
                throw new InvalidOperationException("Invalid UI geometry: " + component.name);
            return new Rect(position.x, position.y, component.width, component.height);
        }

        private static void RequireAxisAlignedTransform(UIComponent component)
        {
            if (Quaternion.Angle(component.transform.localRotation, Quaternion.identity) > 0.01f
                || (component.transform.localScale - Vector3.one).sqrMagnitude > 0.0001f)
                throw new InvalidOperationException("Unsupported transformed UI geometry: " + component.name);
        }

        private static Rect IntersectRects(Rect a, Rect b)
        {
            float left = Math.Max(a.xMin, b.xMin), top = Math.Max(a.yMin, b.yMin);
            return new Rect(left, top, Math.Max(0f, Math.Min(a.xMax, b.xMax) - left), Math.Max(0f, Math.Min(a.yMax, b.yMax) - top));
        }

        private static Rect VisibleRect(UIComponent target)
        {
            Rect visible = ComponentRect(target);
            Vector2 resolution = target.GetUIView().GetScreenResolution();
            visible = IntersectRects(visible, new Rect(0f, 0f, resolution.x, resolution.y));
            UIComponent parent = target.parent;
            int count = 0;
            while (parent != null && ++count <= 32)
            {
                RequireAxisAlignedTransform(parent);
                if (parent.clipChildren)
                {
                    Rect clip = ComponentRect(parent);
                    UIScrollablePanel scroll = parent as UIScrollablePanel;
                    UIPanel panel = parent as UIPanel;
                    RectOffset padding = scroll != null ? scroll.scrollPadding : panel != null ? panel.padding : null;
                    if (padding == null) throw new InvalidOperationException("Unsupported native clipping ancestor: " + parent.name);
                    clip = new Rect(clip.x + padding.left, clip.y + padding.top, Math.Max(0f, clip.width - padding.horizontal), Math.Max(0f, clip.height - padding.vertical));
                    visible = IntersectRects(visible, clip);
                }
                parent = parent.parent;
            }
            if (parent != null) throw new InvalidOperationException("Viewport ancestor chain exceeds bound.");
            return visible;
        }

        private static void RequireViewport(UIComponent component, bool fullyVisible)
        {
            Rect visible = VisibleRect(component);
            if (visible.width < 8f || visible.height < 8f
                || (fullyVisible && (visible.width < component.width - 1f || visible.height < component.height - 1f)))
            {
                Vector2 resolution = component.GetUIView().GetScreenResolution();
                throw new InvalidOperationException("UI content is clipped/outside its native viewport: " + component.name
                    + "; component=" + RectJson(ComponentRect(component)) + "; visible=" + RectJson(visible)
                    + "; screen=" + RectJson(new Rect(0f, 0f, resolution.x, resolution.y)) + "; fullyVisibleRequired=" + fullyVisible);
            }
        }

        private static string RectJson(Rect rect)
        {
            return "{\"x\":" + JsonFloat(rect.x) + ",\"y\":" + JsonFloat(rect.y) + ",\"width\":" + JsonFloat(rect.width) + ",\"height\":" + JsonFloat(rect.height) + "}";
        }

        private string NativeVisualReportJson()
        {
            if (_request.Mode == "InventoryMaps" || _request.Mode == "ResidentialClassTestCity") return "null";
            return "{\"route\":\"CityInfoPanel.NativeCouncil\",\"legacyCaptureAllTabs\":" + Bool(_request.CaptureAllTabs)
                + ",\"legacyFieldMeaning\":" + Json(_request.CaptureAllTabs ? "All seven advisor selectors and manual notices" : "Finance selector and manual notice only")
                + ",\"expectedAdvisorCaptures\":" + (_request.CaptureAllTabs ? "7" : "1")
                + ",\"acceptedAdvisorCaptures\":" + _advisorChecks.Count.ToString(CultureInfo.InvariantCulture)
                + ",\"selectorPreClickObservation\":" + (_selectorObservationState ?? "null")
                + ",\"advisors\":[" + string.Join(",", _advisorChecks.ToArray()) + "]"
                + ",\"expectedManualNoticeCaptures\":" + (_request.CaptureAllTabs ? "7" : "1")
                + ",\"acceptedManualNoticeCaptures\":" + _noticeChecks.Count.ToString(CultureInfo.InvariantCulture)
                + ",\"manualAdvisorNotices\":[" + string.Join(",", _noticeChecks.ToArray()) + "]"
                + ",\"automaticCriticalAlertVerified\":false,\"alertCoverage\":\"Manual presentation of real briefings only; no population or alert injected\""
                + ",\"rciTooltips\":" + (_rciCheck ?? "null") + ",\"nativeBudgetOpened\":" + Bool(_budgetOpened)
                + ",\"nativeBudgetValidated\":" + Bool(_budgetValidated) + ",\"budgetAnimationAndGeometry\":" + (_budgetAnimationState ?? "null")
                + ",\"budgetCaptureGeometryPinned\":" + Bool(_budgetCaptureGeometryPinned)
                + ",\"budgetCaptureGeometryUnchanged\":" + Bool(_budgetCaptureGeometryUnchanged)
                + ",\"budgetCapturedComponentRect\":" + (_budgetCaptureGeometryPinned ? RectJson(_capturedBudgetRect) : "null")
                + ",\"budgetSourceHostNonOccluding\":" + Bool(_budgetHostNonOccluding)
                + ",\"budgetSourceHostVisibility\":" + (_budgetHostVisibilityState ?? "null") + "}";
        }
    }
}
