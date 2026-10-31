using System;
using System.Collections.Generic;

namespace SC4Evolution.Simulation.Neighborhoods
{
    /// <summary>Deterministic neighborhood cycle engine (sprint 02). One update observes
    /// every lot, validates identity by generation, evaluates attractiveness once per lot
    /// and moves lots through Constructing / Occupied / Vacant / Abandoned with an explicit
    /// cause for every change:
    ///
    /// - Occupied lots below the maintenance bar keep their residents for the grace
    ///   period; an attractiveness drop never vacates or destroys on the same update.
    /// - Vacant lots are abandoned only after the vacancy duration at or below the
    ///   abandonment bar; vacancy alone with acceptable attractiveness never abandons.
    /// - Reoccupation (from vacant) and rehabilitation (from abandoned) require demand for
    ///   the lot's resident class AND the corresponding attractiveness bar.
    /// - Resident replacement raises only ResidentClass after sustained high attractiveness
    ///   plus higher-class demand; Level never changes with class.
    /// - A reused building id with a new buildIndex resets the state completely.
    /// No global prefab or native state is touched: the model is a pure decision layer;
    /// adapters apply outcomes natively later and the next observation confirms them.</summary>
    public sealed class NeighborhoodModel
    {
        private readonly NeighborhoodAttractiveness _attractiveness;
        private readonly NeighborhoodThresholds _thresholds;
        private readonly Dictionary<ushort, NeighborhoodLotState> _lots = new Dictionary<ushort, NeighborhoodLotState>();

        public NeighborhoodModel(NeighborhoodAttractiveness attractiveness, NeighborhoodThresholds thresholds)
        {
            if (attractiveness == null) throw new ArgumentNullException("attractiveness");
            if (thresholds == null) throw new ArgumentNullException("thresholds");
            _attractiveness = attractiveness;
            _thresholds = thresholds;
        }

        public int TrackedLotCount
        {
            get { return _lots.Count; }
        }

        /// <summary>Read-only view of one lot's state; always a copy, so callers can never
        /// mutate engine internals through reports or lookups.</summary>
        public bool TryGetLot(ushort lotId, out NeighborhoodLotState state)
        {
            NeighborhoodLotState existing;
            if (!_lots.TryGetValue(lotId, out existing)) { state = null; return false; }
            state = CopyOf(existing);
            return true;
        }

        private static NeighborhoodLotState CopyOf(NeighborhoodLotState source)
        {
            NeighborhoodLotState copy = new NeighborhoodLotState();
            copy.LotId = source.LotId;
            copy.BuildIndex = source.BuildIndex;
            copy.Level = source.Level;
            copy.ResidentClass = source.ResidentClass;
            copy.Phase = source.Phase;
            copy.VacancyUpdates = source.VacancyUpdates;
            copy.MaintenanceStrainUpdates = source.MaintenanceStrainUpdates;
            copy.ReplacementSustainedUpdates = source.ReplacementSustainedUpdates;
            copy.LastAttractiveness = source.LastAttractiveness;
            copy.LastReason = source.LastReason;
            copy.LastTransitionFrame = source.LastTransitionFrame;
            return copy;
        }

        public byte[] Save()
        {
            return NeighborhoodStateCodec.Save(_lots.Values);
        }

        /// <summary>Rebuilds a model from persisted state. Configuration is always supplied
        /// fresh; only per-lot state round-trips. Corrupt data throws InvalidDataException
        /// and must leave the caller's effects disabled, never an empty-looking model that
        /// silently forgets neighborhoods.</summary>
        public static NeighborhoodModel Restore(NeighborhoodAttractiveness attractiveness,
            NeighborhoodThresholds thresholds, byte[] data)
        {
            if (attractiveness == null) throw new ArgumentNullException("attractiveness");
            if (thresholds == null) throw new ArgumentNullException("thresholds");
            if (data == null) throw new ArgumentNullException("data");
            NeighborhoodModel model = new NeighborhoodModel(attractiveness, thresholds);
            List<NeighborhoodLotState> states = NeighborhoodStateCodec.Read(data);
            for (int i = 0; i < states.Count; i++) model._lots[states[i].LotId] = states[i];
            return model;
        }

        public NeighborhoodReport Apply(NeighborhoodUpdateInput input)
        {
            if (input == null) throw new ArgumentNullException("input");
            List<NeighborhoodTransition> transitions = new List<NeighborhoodTransition>();
            List<NeighborhoodLotState> snapshots = new List<NeighborhoodLotState>();
            for (int i = 0; i < input.Lots.Count; i++)
            {
                NeighborhoodLotInput lot = input.Lots[i];
                NeighborhoodLotState state = ResolveState(lot, input.Frame, transitions);
                UpdateLot(state, lot, input, transitions);
                snapshots.Add(CopyOf(state));
            }
            int[] phaseCounts = new int[6];
            int[] reasonCounts = new int[12];
            for (int i = 0; i < snapshots.Count; i++) phaseCounts[(int)snapshots[i].Phase]++;
            for (int i = 0; i < transitions.Count; i++)
            {
                if ((int)transitions[i].Reason < reasonCounts.Length)
                    reasonCounts[(int)transitions[i].Reason]++;
            }
            return new NeighborhoodReport(input.Frame, snapshots.ToArray(), transitions.ToArray(), phaseCounts, reasonCounts);
        }

        private NeighborhoodLotState ResolveState(NeighborhoodLotInput lot, uint frame, List<NeighborhoodTransition> transitions)
        {
            NeighborhoodLotState state;
            if (!_lots.TryGetValue(lot.LotId, out state))
            {
                state = new NeighborhoodLotState();
                state.LotId = lot.LotId;
                state.BuildIndex = lot.BuildIndex;
                state.Level = lot.Level;
                state.ResidentClass = StockClassToModel(lot.ObservedStockClass);
                state.Phase = InitialPhase(lot);
                state.LastTransitionFrame = frame;
                _lots[lot.LotId] = state;
                return state;
            }
            if (state.BuildIndex != lot.BuildIndex)
            {
                NeighborhoodTransitionReason reason = state.Phase == NeighborhoodPhase.None
                    ? NeighborhoodTransitionReason.None
                    : NeighborhoodTransitionReason.GenerationReset;
                // The reset record carries the NEW generation, so consumers can never
                // attribute the state reset to the previous building.
                state.BuildIndex = lot.BuildIndex;
                if (reason != NeighborhoodTransitionReason.None)
                    Record(transitions, frame, state, NeighborhoodPhase.None, state.ResidentClass, reason);
                state.Level = lot.Level;
                state.ResidentClass = StockClassToModel(lot.ObservedStockClass);
                state.Phase = InitialPhase(lot);
                state.VacancyUpdates = 0;
                state.MaintenanceStrainUpdates = 0;
                state.ReplacementSustainedUpdates = 0;
                state.LastReason = reason == NeighborhoodTransitionReason.None ? NeighborhoodTransitionReason.None : reason;
                state.LastTransitionFrame = frame;
            }
            else
            {
                state.Level = lot.Level;
            }
            return state;
        }

        private static NeighborhoodPhase InitialPhase(NeighborhoodLotInput lot)
        {
            if (lot.StockPhase == ResidentialStockPhase.Unavailable) return NeighborhoodPhase.NativeUnavailable;
            if (lot.StockPhase == ResidentialStockPhase.Constructing || lot.StockPhase == ResidentialStockPhase.Upgrading)
                return NeighborhoodPhase.Constructing;
            return lot.HasLiveResidents ? NeighborhoodPhase.Occupied : NeighborhoodPhase.Vacant;
        }

        private static int StockClassToModel(int stockClass)
        {
            return stockClass == 0 ? -1 : stockClass - 1;
        }

        private void UpdateLot(NeighborhoodLotState state, NeighborhoodLotInput lot, NeighborhoodUpdateInput input, List<NeighborhoodTransition> transitions)
        {
            float attractiveness = _attractiveness.Evaluate(lot.Factors);
            state.LastAttractiveness = attractiveness;
            if (lot.StockPhase == ResidentialStockPhase.Unavailable)
            {
                if (state.Phase != NeighborhoodPhase.NativeUnavailable)
                    Move(transitions, input.Frame, state, NeighborhoodPhase.NativeUnavailable, state.ResidentClass, NeighborhoodTransitionReason.NativeUnavailable);
                state.VacancyUpdates = 0; state.MaintenanceStrainUpdates = 0; state.ReplacementSustainedUpdates = 0;
                return;
            }
            if (lot.StockPhase == ResidentialStockPhase.Constructing || lot.StockPhase == ResidentialStockPhase.Upgrading)
            {
                if (state.Phase != NeighborhoodPhase.Constructing)
                    Move(transitions, input.Frame, state, NeighborhoodPhase.Constructing, state.ResidentClass, NeighborhoodTransitionReason.NativeRedevelopment);
                state.VacancyUpdates = 0; state.MaintenanceStrainUpdates = 0; state.ReplacementSustainedUpdates = 0;
                return;
            }
            // Stock completed: the model owns occupied/vacant/abandoned.
            int observedClass = StockClassToModel(lot.ObservedStockClass);
            if (observedClass >= 0 && state.ResidentClass != observedClass && state.Phase == NeighborhoodPhase.Occupied)
            {
                // Native class observation wins on occupied lots; record it like any cause.
                Move(transitions, input.Frame, state, state.Phase, observedClass, NeighborhoodTransitionReason.ClassReplacement);
            }
            switch (state.Phase)
            {
                case NeighborhoodPhase.Constructing:
                    Move(transitions, input.Frame, state,
                        lot.HasLiveResidents ? NeighborhoodPhase.Occupied : NeighborhoodPhase.Vacant,
                        state.ResidentClass, NeighborhoodTransitionReason.ConstructionCompleted);
                    if (lot.HasLiveResidents) { state.VacancyUpdates = 0; state.MaintenanceStrainUpdates = 0; }
                    else state.VacancyUpdates = 0;
                    break;
                case NeighborhoodPhase.Occupied:
                    UpdateOccupied(state, lot, input, attractiveness, transitions);
                    break;
                case NeighborhoodPhase.Vacant:
                    UpdateVacant(state, lot, input, attractiveness, transitions);
                    break;
                case NeighborhoodPhase.Abandoned:
                    if (attractiveness >= _thresholds.RehabilitationAttractiveness && ClassDemanded(input, state.ResidentClass))
                        Move(transitions, input.Frame, state, NeighborhoodPhase.Occupied, state.ResidentClass, NeighborhoodTransitionReason.RehabilitationDemandMet);
                    break;
                case NeighborhoodPhase.NativeUnavailable:
                case NeighborhoodPhase.None:
                default:
                    throw new InvalidOperationException("Unexpected phase " + state.Phase + " on completed stock.");
            }
        }

        private void UpdateOccupied(NeighborhoodLotState state, NeighborhoodLotInput lot, NeighborhoodUpdateInput input, float attractiveness, List<NeighborhoodTransition> transitions)
        {
            if (!lot.HasLiveResidents)
            {
                Move(transitions, input.Frame, state, NeighborhoodPhase.Vacant, state.ResidentClass, NeighborhoodTransitionReason.ObservedVacancy);
                state.VacancyUpdates = 0;
                state.MaintenanceStrainUpdates = 0;
                state.ReplacementSustainedUpdates = 0;
                return;
            }
            if (attractiveness <= _thresholds.MaintenanceAttractiveness)
            {
                state.MaintenanceStrainUpdates++;
                state.ReplacementSustainedUpdates = 0;
                if (state.MaintenanceStrainUpdates >= _thresholds.MaintenanceGraceUpdates)
                {
                    Move(transitions, input.Frame, state, NeighborhoodPhase.Vacant, state.ResidentClass, NeighborhoodTransitionReason.AttractivenessBelowMaintenance);
                    state.MaintenanceStrainUpdates = 0;
                    state.VacancyUpdates = 0;
                }
                return;
            }
            state.MaintenanceStrainUpdates = 0;
            if (state.ResidentClass >= 0 && state.ResidentClass < 2 &&
                attractiveness >= _thresholds.ReplacementAttractiveness &&
                input.DemandByClass[state.ResidentClass + 1] > 0)
            {
                state.ReplacementSustainedUpdates++;
                if (state.ReplacementSustainedUpdates >= _thresholds.ReplacementSustainedUpdates)
                {
                    Move(transitions, input.Frame, state, NeighborhoodPhase.Occupied, state.ResidentClass + 1, NeighborhoodTransitionReason.ClassReplacement);
                    state.ReplacementSustainedUpdates = 0;
                }
            }
            else state.ReplacementSustainedUpdates = 0;
        }

        private void UpdateVacant(NeighborhoodLotState state, NeighborhoodLotInput lot, NeighborhoodUpdateInput input, float attractiveness, List<NeighborhoodTransition> transitions)
        {
            if (lot.HasLiveResidents)
            {
                Move(transitions, input.Frame, state, NeighborhoodPhase.Occupied, state.ResidentClass, NeighborhoodTransitionReason.ObservedReoccupation);
                state.VacancyUpdates = 0;
                return;
            }
            state.VacancyUpdates++;
            if (attractiveness <= _thresholds.AbandonmentAttractiveness)
            {
                if (state.VacancyUpdates >= _thresholds.AbandonmentVacancyUpdates)
                    Move(transitions, input.Frame, state, NeighborhoodPhase.Abandoned, state.ResidentClass, NeighborhoodTransitionReason.ProlongedVacancyLowAttractiveness);
                return;
            }
            if (attractiveness >= _thresholds.ReoccupationAttractiveness && ClassDemanded(input, state.ResidentClass))
            {
                Move(transitions, input.Frame, state, NeighborhoodPhase.Occupied, state.ResidentClass, NeighborhoodTransitionReason.ReoccupationDemandMet);
                state.VacancyUpdates = 0;
            }
        }

        private static bool ClassDemanded(NeighborhoodUpdateInput input, int residentClass)
        {
            if (residentClass < 0 || residentClass > 2) return input.DemandByClass[0] > 0 || input.DemandByClass[1] > 0 || input.DemandByClass[2] > 0;
            return input.DemandByClass[residentClass] > 0;
        }

        private static void Move(List<NeighborhoodTransition> transitions, uint frame, NeighborhoodLotState state,
            NeighborhoodPhase toPhase, int toClass, NeighborhoodTransitionReason reason)
        {
            if (reason == NeighborhoodTransitionReason.None)
                throw new InvalidOperationException("A transition requires an explicit cause.");
            NeighborhoodTransition transition = new NeighborhoodTransition();
            transition.Frame = frame; transition.LotId = state.LotId; transition.BuildIndex = state.BuildIndex;
            transition.FromPhase = state.Phase; transition.ToPhase = toPhase;
            transition.FromClass = state.ResidentClass; transition.ToClass = toClass;
            transition.Reason = reason;
            transitions.Add(transition);
            state.Phase = toPhase;
            state.ResidentClass = toClass;
            state.LastReason = reason;
            state.LastTransitionFrame = frame;
        }

        private static void Record(List<NeighborhoodTransition> transitions, uint frame, NeighborhoodLotState state,
            NeighborhoodPhase toPhase, int toClass, NeighborhoodTransitionReason reason)
        {
            Move(transitions, frame, state, toPhase, toClass, reason);
        }
    }
}
