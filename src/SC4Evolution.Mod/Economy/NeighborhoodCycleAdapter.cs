using System;
using System.Collections.Generic;
using SC4Evolution.Simulation;
using SC4Evolution.Simulation.Neighborhoods;

namespace SC4Evolution.Mod.Economy
{
    /// <summary>Explicit opt-in session for the neighborhood cycle (sprint 02).
    /// No Harmony/game dependencies: the same source is compiled into the isolated harness.
    ///
    /// INTEGRATION CONTRACT (wired after the sprint-03 merge; EconomicBridge owns config and
    /// is not modified by this sprint): on the simulation thread, call Observe with the
    /// published ResidentialStockSnapshot, per-lot attractiveness factors and city demand by
    /// class. Accessible-employment factors will come from the sprint-01/sprint-05 outputs;
    /// services/pollution/traffic/land-value sampling still needs native grounding
    /// (documented as pending). The published report feeds advisors and news; this adapter
    /// applies nothing natively — abandonment, reoccupation and replacement stay reports
    /// until a native application contract is proven. Persisted bytes use the model codec
    /// and a future envelope extension file, not a new save key. The stock snapshot carries
    /// no building level, so Level stays 0 here until level sampling is added natively;
    /// level and class never derive from each other in the model either way.</summary>
    public sealed class NeighborhoodCycleSession
    {
        private readonly NeighborhoodModel _model;
        private readonly NeighborhoodThresholds _thresholds;

        private NeighborhoodCycleSession(NeighborhoodModel model, NeighborhoodThresholds thresholds)
        {
            _model = model;
            _thresholds = thresholds;
        }

        public NeighborhoodThresholds Thresholds
        {
            get { return _thresholds; }
        }

        public static NeighborhoodCycleSession TryCreate(bool enabled,
            NeighborhoodAttractiveness attractiveness, NeighborhoodThresholds thresholds)
        {
            if (!enabled) return null;
            if (attractiveness == null) throw new ArgumentNullException("attractiveness");
            if (thresholds == null) throw new ArgumentNullException("thresholds");
            return new NeighborhoodCycleSession(new NeighborhoodModel(attractiveness, thresholds), thresholds);
        }

        public static NeighborhoodCycleSession TryRestore(bool enabled,
            NeighborhoodAttractiveness attractiveness, NeighborhoodThresholds thresholds, byte[] persisted)
        {
            if (!enabled) return null;
            if (attractiveness == null) throw new ArgumentNullException("attractiveness");
            if (thresholds == null) throw new ArgumentNullException("thresholds");
            return new NeighborhoodCycleSession(
                NeighborhoodModel.Restore(attractiveness, thresholds, persisted), thresholds);
        }

        public byte[] Save()
        {
            return _model.Save();
        }

        /// <summary>Converts stock observations into cycle inputs. Factors come from the
        /// caller per lot id; unknown ids use the provided neutral bundle and are counted,
        /// so missing sampling is visible instead of silent.</summary>
        public NeighborhoodReport Observe(uint frame, ResidentialStockSnapshot stock,
            IDictionary<ushort, NeighborhoodFactors> factorsByLot, int[] demandByClass,
            out int lotsWithoutFactorSamples)
        {
            if (stock == null) throw new ArgumentNullException("stock");
            if (factorsByLot == null) throw new ArgumentNullException("factorsByLot");
            if (demandByClass == null) throw new ArgumentNullException("demandByClass");
            ResidentialStockLot[] stockLots = stock.Lots;
            List<NeighborhoodLotInput> lots = new List<NeighborhoodLotInput>(stockLots.Length);
            lotsWithoutFactorSamples = 0;
            for (int i = 0; i < stockLots.Length; i++)
            {
                ResidentialStockLot lot = stockLots[i];
                NeighborhoodFactors factors;
                if (!factorsByLot.TryGetValue(lot.BuildingId, out factors))
                {
                    factors = NeighborhoodFactors.Neutral();
                    lotsWithoutFactorSamples++;
                }
                NeighborhoodLotInput input = new NeighborhoodLotInput();
                input.LotId = lot.BuildingId;
                input.BuildIndex = lot.ObservedBuildIndex;
                input.Level = 0;
                input.ObservedStockClass = lot.LotClass;
                input.StockPhase = lot.Phase;
                input.HasLiveResidents = lot.SettledPeople > 0 || lot.IncomingPeople > 0;
                input.Factors = factors;
                lots.Add(input);
            }
            return _model.Apply(new NeighborhoodUpdateInput(frame, lots, demandByClass));
        }
    }
}
