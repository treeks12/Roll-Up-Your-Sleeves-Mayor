using System;
using SC4Evolution.Simulation.Employment;

namespace SC4Evolution.Mod.Economy
{
    /// <summary>Explicit opt-in session for the segmented employment model (sprint 01).
    /// No Harmony/game dependencies: the same source is compiled into the isolated harness.
    ///
    /// INTEGRATION CONTRACT (to be wired after the sprint-03 merge; EconomicBridge owns the
    /// config read and is not modified by this sprint): EconomicBridge calls TryCreate only
    /// when its explicit employment opt-in is configured, constructs inputs from CityCensus
    /// arrays on the simulation thread (WorkforceByLotClassEducation when residents are
    /// bound, WorkforceByBootstrapWealthEducation otherwise; JobCapacity/FilledBySegment;
    /// empty HOME units from ResidentialStock; the best current entry-path observation) and
    /// publishes the returned report for advisors/UI. This adapter never writes money,
    /// population, demand or any native state; grants are reports, not arrivals.</summary>
    public sealed class EmploymentSegmentSession
    {
        private readonly EmploymentMarket _market;

        private EmploymentSegmentSession(EmploymentMarket market)
        {
            _market = market;
        }

        public EmploymentQualification Qualification
        {
            get { return _market.Qualification; }
        }

        public static EmploymentSegmentSession TryCreate(bool enabled, EmploymentQualification qualification)
        {
            if (!enabled) return null;
            if (qualification == null) throw new ArgumentNullException("qualification");
            return new EmploymentSegmentSession(new EmploymentMarket(qualification));
        }

        public EmploymentMarketReport Apply(uint frame, int[] workforceByCell, int[] jobCapacityBySegment,
            int[] jobFilledBySegment, int unmappedJobCapacity, int unmappedFilledJobs,
            int availableHomeUnits, bool entryPathOpen)
        {
            EmploymentMarketInput input = new EmploymentMarketInput(frame, workforceByCell,
                jobCapacityBySegment, jobFilledBySegment, unmappedJobCapacity, unmappedFilledJobs,
                availableHomeUnits, entryPathOpen);
            return _market.Apply(input);
        }
    }
}
