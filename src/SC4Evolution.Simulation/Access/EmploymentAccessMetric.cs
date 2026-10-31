using System;

namespace SC4Evolution.Simulation.Access
{
    /// <summary>Employment access observation for one origin (a residential building or
    /// district aggregate) toward job destinations (sprint 05). Every quantity is observed
    /// separately, because each answers a different question:
    /// - RouteAttempts: routing queries issued (a route found is not a trip made);
    /// - TripsCompleted: journeys actually finished (arrival at the destination);
    /// - ArrivalFailures: journeys that failed to arrive (stuck/despawned);
    /// - TravelTimeTicks: observed total travel time of completed trips (native tick units);
    /// - JobCapacityAtDestinations: built workplace capacity reachable in the observation;
    /// - FilledJobsAtDestinations: occupied jobs at those destinations.
    /// The metric divides completed trips by attempts, so a visual route with no completed
    /// journey can never count as access; capacity the residents cannot reach does not
    /// inflate the score either.</summary>
    public struct EmploymentAccessObservation
    {
        public ushort OriginId;
        public int RouteAttempts;
        public int TripsCompleted;
        public int ArrivalFailures;
        public long TravelTimeTicks;
        public int JobCapacityAtDestinations;
        public int FilledJobsAtDestinations;
    }

    /// <summary>Deterministic employment access metric (sprint 05). The score is the product
    /// of three clamped factors on 0..1: completion rate (trips / attempts), reachable free
    /// capacity share (capacity - filled, relative to capacity, guarded against zero) and a
    /// time penalty (average ticks per completed trip against a budget; at or below budget
    /// the factor is 1, at double the budget it reaches 0). All thresholds are injected:
    /// no native constant is invented here.</summary>
    public sealed class EmploymentAccessMetric
    {
        public const long DefaultTimeBudgetTicks = 5 * 60 * 60; // authored placeholder: 5 in-game hours
        private readonly long _timeBudgetTicks;

        public EmploymentAccessMetric(long timeBudgetTicks)
        {
            if (timeBudgetTicks <= 0) throw new ArgumentOutOfRangeException("timeBudgetTicks", "Time budget must be positive.");
            _timeBudgetTicks = timeBudgetTicks;
        }

        public long TimeBudgetTicks
        {
            get { return _timeBudgetTicks; }
        }

        /// <summary>Score 0..1 for one origin. Unknown observations (zero attempts or zero
        /// destination capacity) score 0: absence of evidence is never free access.</summary>
        public float Score(EmploymentAccessObservation observation)
        {
            if (observation.RouteAttempts < 0 || observation.TripsCompleted < 0 || observation.ArrivalFailures < 0 ||
                observation.TravelTimeTicks < 0 || observation.JobCapacityAtDestinations < 0 || observation.FilledJobsAtDestinations < 0)
                throw new ArgumentException("Access observations cannot be negative.", "observation");
            if (observation.TripsCompleted + observation.ArrivalFailures > observation.RouteAttempts)
                throw new ArgumentException("Outcomes cannot exceed route attempts.", "observation");
            if (observation.FilledJobsAtDestinations > observation.JobCapacityAtDestinations)
                throw new ArgumentException("Filled jobs cannot exceed destination capacity.", "observation");
            if (observation.RouteAttempts == 0 || observation.JobCapacityAtDestinations == 0) return 0f;
            float completion = (float)observation.TripsCompleted / observation.RouteAttempts;
            int freeCapacity = observation.JobCapacityAtDestinations - observation.FilledJobsAtDestinations;
            float capacityShare = (float)freeCapacity / observation.JobCapacityAtDestinations;
            float averageTicks = observation.TripsCompleted == 0
                ? (float)_timeBudgetTicks * 2f
                : (float)((double)observation.TravelTimeTicks / observation.TripsCompleted);
            float timeFactor;
            if (averageTicks <= _timeBudgetTicks) timeFactor = 1f;
            else if (averageTicks >= _timeBudgetTicks * 2) timeFactor = 0f;
            else timeFactor = 1f - (float)((averageTicks - _timeBudgetTicks) / (double)_timeBudgetTicks);
            float score = completion * capacityShare * timeFactor;
            if (score < 0f) return 0f;
            if (score > 1f) return 1f;
            return score;
        }
    }
}
