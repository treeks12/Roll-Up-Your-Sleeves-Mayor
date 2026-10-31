using System;
using ColossalFramework.UI;
using SC4Evolution.Core;
using SC4Evolution.Mod.Runtime;
using UnityEngine;

namespace SC4Evolution.Mod.UI
{
    public static class CabinetUI
    {
        private static CabinetRoot _root;

        public static void Install()
        {
            if (_root != null) return;
            try
            {
                UIView view = UIView.GetAView();
                if (view == null) return;
                _root = view.AddUIComponent(typeof(CabinetRoot)) as CabinetRoot;
                if (_root == null) return;
                _root.Initialize(view);
            }
            catch (Exception exception)
            {
                Log("Native municipal UI install failed: " + exception);
                Uninstall();
            }
        }

        public static void Uninstall()
        {
            CabinetRoot root = _root;
            _root = null;
            if (root == null) return;
            try { root.Shutdown(); }
            catch (Exception exception) { Log("Native municipal UI cleanup failed: " + exception); }
            finally { UnityEngine.Object.Destroy(root.gameObject); }
        }

        public static void Toggle()
        {
            if (_root == null) Install();
            if (_root != null) _root.ToggleWindow();
        }

        internal static void Log(string message)
        {
            try { ModRuntime.Log(message); }
            catch { }
        }
    }

    /// <summary>Compatibility lifecycle controller; all visible controls live in native hosts.</summary>
    public sealed class CabinetRoot : UIPanel
    {
        private UIView _view;
        private NativeMunicipalUI _native;
        private NativeCivicFundingUI _civic;
        private float _nextCivicRetry;
        private float _nextRefresh;
        private bool _initialized;
        private bool _shutdown;

        internal UIPanel CouncilPanel { get { return _native == null ? null : _native.CouncilPanel; } }
        internal UIPanel CouncilDock { get { return _native == null ? null : _native.CouncilDock; } }
        internal UIButton CouncilToggle { get { return _native == null ? null : _native.CouncilToggle; } }
        internal UIScrollablePanel CouncilContent { get { return _native == null ? null : _native.CouncilContent; } }
        internal UIComponent CityHost { get { return _native == null ? null : _native.CityHost; } }
        internal UITextureSprite AdvisorPortrait { get { return _native == null ? null : _native.AdvisorPortrait; } }
        internal int SelectedAdvisor { get { return _native == null ? -1 : _native.SelectedAdvisor; } }
        internal bool CouncilExpanded { get { return _native != null && _native.IsExpanded; } }
        internal int CouncilNewsCount { get { return _native == null ? 0 : _native.NewsCount; } }
        internal UIButton CouncilReadNotice { get { return _native == null ? null : _native.ReadNoticeButton; } }
        internal UIPanel AdvisorMessagePanel { get { return _native == null ? null : _native.MessagePanel; } }
        internal UITextureSprite AdvisorMessagePortrait { get { return _native == null ? null : _native.MessagePortrait; } }
        internal UIButton AdvisorMessageCloseButton { get { return _native == null ? null : _native.MessageCloseButton; } }
        internal UIButton AdvisorMessageActionButton { get { return _native == null ? null : _native.MessageActionButton; } }
        internal AdvisorBriefing AdvisorMessageBriefing { get { return _native == null ? null : _native.MessageBriefing; } }
        internal bool AdvisorMessageVisible { get { return _native != null && _native.MessageVisible; } }
        internal int PendingAdvisorAlerts { get { return _native == null ? 0 : _native.PendingAlerts; } }
        internal UIPanel CivicFundingPanel { get { return _civic == null ? null : _civic.Panel; } }
        internal SC4Evolution.Mod.Civic.CivicFundingSnapshot CivicFundingSnapshot { get { return _civic == null ? null : _civic.Snapshot; } }
        internal bool CivicFundingPending { get { return _civic != null && _civic.Pending; } }
        internal string CivicFundingStatus { get { return _civic == null ? string.Empty : _civic.Status; } }
        internal UIButton CivicFundingCapacityMinus { get { return _civic == null ? null : _civic.CapacityMinus; } }
        internal UIButton CivicFundingCapacityPlus { get { return _civic == null ? null : _civic.CapacityPlus; } }
        internal UIButton CivicFundingCapacityInherit { get { return _civic == null ? null : _civic.CapacityInherit; } }
        internal UIButton CivicFundingCoverageMinus { get { return _civic == null ? null : _civic.CoverageMinus; } }
        internal UIButton CivicFundingCoveragePlus { get { return _civic == null ? null : _civic.CoveragePlus; } }
        internal UIButton CivicFundingCoverageInherit { get { return _civic == null ? null : _civic.CoverageInherit; } }
        internal UIButton GetAdvisorButton(int index)
        {
            return _native == null ? null : _native.GetAdvisorButton(index);
        }
        internal UITextureSprite GetAdvisorAvatar(int index)
        {
            return _native == null ? null : _native.GetAdvisorAvatar(index);
        }

        internal bool ReadSelectedAdvisorMessage()
        {
            return _native != null && _native.ReadSelectedNotice();
        }

        public void Initialize(UIView view)
        {
            if (_initialized || view == null) return;
            _initialized = true;
            _view = view;
            name = "SC4EvolutionCabinetRoot";
            size = Vector2.one;
            relativePosition = Vector3.zero;
            backgroundSprite = string.Empty;
            opacity = 1f;
            isInteractive = false;
            canFocus = false;
            _native = new NativeMunicipalUI(view);
            _civic = new NativeCivicFundingUI(view);
        }

        public override void Update()
        {
            base.Update();
            if (_shutdown || !_initialized) return;
            if (_native != null) _native.Animate();
            if (Input.GetKeyDown(KeyCode.M)
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                && !UIView.HasInputFocus() && !UIView.HasModalInput())
                ToggleWindow();
        }

        public override void LateUpdate()
        {
            base.LateUpdate();
            if (_shutdown || !_initialized || _view == null) return;
            RefreshCivicFunding();
            if (Time.realtimeSinceStartup < _nextRefresh) return;
            _nextRefresh = Time.realtimeSinceStartup + 0.25f;
            try
            {
                if (_native == null) _native = new NativeMunicipalUI(_view);
                if (_native.TryInstall()) _native.Refresh(false);
            }
            catch (Exception exception)
            {
                CabinetUI.Log("Native municipal UI refresh failed: " + exception);
                ReleaseNative();
                _nextRefresh = Time.realtimeSinceStartup + 5f;
            }
        }

        private void RefreshCivicFunding()
        {
            if (Time.realtimeSinceStartup < _nextCivicRetry) return;
            try
            {
                if (_civic == null) _civic = new NativeCivicFundingUI(_view);
                _civic.Refresh();
            }
            catch (Exception exception)
            {
                CabinetUI.Log("Native civic funding UI refresh failed: " + exception);
                ReleaseCivicFunding();
                _nextCivicRetry = Time.realtimeSinceStartup + 5f;
            }
        }
        private void ReleaseCivicFunding()
        {
            NativeCivicFundingUI civic = _civic;
            _civic = null;
            if (civic != null) civic.Release();
        }

        public void ToggleWindow()
        {
            if (_shutdown || !_initialized || _view == null) return;
            try
            {
                if (_native == null) _native = new NativeMunicipalUI(_view);
                _native.Toggle();
            }
            catch (Exception exception)
            {
                CabinetUI.Log("Native municipal UI could not open: " + exception);
                ReleaseNative();
            }
        }

        private void ReleaseNative()
        {
            NativeMunicipalUI native = _native;
            _native = null;
            if (native != null) native.Release();
        }

        public void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            try { ReleaseCivicFunding(); }
            finally { ReleaseNative(); }
            isVisible = false;
            enabled = false;
            _view = null;
        }

        public override void OnDestroy()
        {
            try { Shutdown(); }
            finally { base.OnDestroy(); }
        }
    }
}
