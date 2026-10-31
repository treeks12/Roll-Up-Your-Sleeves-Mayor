using System;
using System.IO;
using SC4Evolution.Simulation;

namespace SC4Evolution.Mod.Economy
{
    public sealed partial class EconomicBridge
    {
        private volatile ResidentialCapacityBridge _capacity;
        private bool _capacityModeRequested, _pendingNewCapacityCity;
        private byte[] _pendingCapacityState;
        private string _pendingCapacityFailure, _persistenceFailure;
        private int[] _externalInputs;
        internal object CapacitySync { get { return _sync; } }
        internal ResidentialCapacityBridge Capacity { get { return _capacity; } }
        internal bool IsCapacityModeRequested { get { return _capacityModeRequested; } }
        public ResidentialCapacityStatus PublishedCapacityStatus { get { ResidentialCapacityBridge bridge = _capacity; return bridge == null ? null : bridge.Published; } }

        public void SetLoadedCapacityState(byte[] state)
        {
            if (state != null && state.Length > 16 * 1024 * 1024) throw new InvalidDataException("Capacity payload exceeds limit.");
            lock (_sync)
            {
                if (_running) throw new InvalidOperationException("Load capacity state before starting a city session.");
                _pendingCapacityState = state == null ? null : (byte[])state.Clone(); _pendingCapacityFailure = null;
            }
        }
        public void SetCapacityLoadFailure(string reason)
        {
            lock (_sync)
            {
                if (_running) throw new InvalidOperationException("Report capacity load failure before starting a city session.");
                _pendingCapacityState = null; _pendingCapacityFailure = string.IsNullOrEmpty(reason) ? "Capacity payload could not be loaded." : reason;
            }
        }
        public void SetNewCapacityCity(bool isNewCity)
        {
            lock (_sync)
            { if (_running) throw new InvalidOperationException("Set explicit new-city evidence before Start."); _pendingNewCapacityCity = isNewCity; }
        }
        public bool TryGetSavePair(out EconomicSavePair pair, out string reason)
        {
            lock (_sync)
            {
                pair = null; reason = null;
                if (_persistenceFailure != null) { reason = _persistenceFailure; return false; }
                if (!_running || _classes == null) { reason = "No active validated economic session."; return false; }
                byte[] classes = _classes.Save();
                if (classes == null) { reason = "Waiting for validated class state, or class saving is suppressed."; return false; }
                byte[] capacity = null;
                if (_capacity != null && !_capacity.TryCapture(classes, out capacity, out reason)) return false;
                pair = new EconomicSavePair(classes, capacity); return true;
            }
        }
        public void ReportPersistenceFailure(string reason)
        {
            lock (_sync)
            {
                _persistenceFailure = "Economic persistence suspended for this session: " + (string.IsNullOrEmpty(reason) ? "unknown failure" : reason);
                _aggregate = false;
                if (_capacity != null) _capacity.Suspend(_persistenceFailure);
                if (_classes != null) _classes.Disable(_persistenceFailure);
                _status = _persistenceFailure;
            }
        }
        internal bool CanUseCapacityDemand(SegmentedDemandSnapshot snapshot)
        {
            return _capacityModeRequested && _capacity != null && _capacity.Ready && CanUseDemand(snapshot);
        }

        // Called only under the owner lock, on simulation thread. It changes stock
        // supply and propagates the graph against the last actual census. It never
        // starts a citizen scan and never changes census/person quantities.
        internal bool PublishCapacityMutation()
        {
            if (!_running || _capacity == null || !ResidentialCapacityBridge.OnSimulationThread()) return false;
            if (!_capacityModeRequested) return true; // Retained Shadow accounting has no gameplay graph authority.
            if (_graph == null || _published == null || !_published.Converged) return false;
            long[] q = _capacity.SupplyQ;
            if (q == null) return false;
            try
            {
                for (int i = 0; i < 3; i++) Supply((uint)(0x1010 + i * 0x10), ExactNominalSupply(q[i]));
                DemandPropagationResult result = _graph.Propagate(128);
                if (!result.Converged) { _capacity.Suspend("Capacity stock graph did not converge."); _published = null; return false; }
                _capacity.AcknowledgeGraph();
                _published = new SegmentedDemandSnapshot(_published.Census, _graph.GetAllStates(), true, _aggregate && _capacity.Ready, result.Passes);
                return true;
            }
            catch (Exception error) { _capacity.Suspend("Capacity publication failed: " + error.Message); _published = null; return false; }
        }
        private static float ExactNominalSupply(long q)
        {
            if (q < 0) throw new InvalidDataException("Negative nominal stock.");
            float value = (float)((double)q / 4.0);
            if (float.IsInfinity(value) || (double)value * 4.0 != (double)q)
                throw new InvalidDataException("Nominal stock exceeds the graph's exact quarter-person float domain.");
            return value;
        }
        private bool ExternalCensusChanged(params int[][] inputs)
        {
            int length = 0; foreach (int[] input in inputs) length = checked(length + input.Length);
            int[] next = new int[length]; int offset = 0;
            foreach (int[] input in inputs) { Array.Copy(input, 0, next, offset, input.Length); offset += input.Length; }
            bool changed = _externalInputs == null || _externalInputs.Length != next.Length;
            if (!changed) for (int i = 0; i < next.Length; i++) if (next[i] != _externalInputs[i]) { changed = true; break; }
            _externalInputs = next; return changed;
        }
    }
}
