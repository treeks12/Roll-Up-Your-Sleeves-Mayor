using System;
using SC4Evolution.Mod.Economy;
using SC4Evolution.Simulation.Access;
using SC4Evolution.Simulation.Freight;

internal static class Program
{
    private static int _checks;

    private static EmploymentAccessObservation Access(int attempts, int completed, int failures, long ticks, int capacity, int filled)
    {
        EmploymentAccessObservation observation = new EmploymentAccessObservation();
        observation.OriginId = 1;
        observation.RouteAttempts = attempts; observation.TripsCompleted = completed; observation.ArrivalFailures = failures;
        observation.TravelTimeTicks = ticks; observation.JobCapacityAtDestinations = capacity; observation.FilledJobsAtDestinations = filled;
        return observation;
    }

    private static FreightOutflowObservation Freight(int attempts, int delivered, int failed, int stock, bool externalOpen)
    {
        FreightOutflowObservation observation = new FreightOutflowObservation();
        observation.OriginId = 2;
        observation.RouteAttempts = attempts; observation.DeliveriesCompleted = delivered;
        observation.TransferFailures = failed; observation.StockUnits = stock; observation.ExternalConnectionOpen = externalOpen;
        return observation;
    }

    private static int Main()
    {
        try
        {
            GoodConnection();
            Congestion();
            MissingExternalConnection();
            DestinationWithoutCapacity();
            RouteWithoutArrival();
            RoadExistenceAloneNeverScores();
            Validation();
            EffectBands();
            GradualEffects();
            AdapterOptIn();
            BatchOrder();
            Determinism();
            Console.WriteLine("PASS: " + _checks + " access/freight assertions; pure metrics + real adapter source, no native execution, no traffic AI touched.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }

    private static void GoodConnection()
    {
        EmploymentAccessMetric metric = new EmploymentAccessMetric(1000);
        // 10 attempts, 10 completed, comfortable time, half the destination capacity free.
        float good = metric.Score(Access(10, 10, 0, 5000, 100, 50));
        Equal(0.5f, good, "good connection: completion 1 * capacity share 0.5 * time 1");
        True(good >= 0.4999f && good <= 0.5001f, "good connection exact");
        // Fully free capacity at budget time: perfect score.
        Equal(1f, metric.Score(Access(10, 10, 0, 10000, 100, 0)), "perfect connection scores 1");
        True(metric.Score(Access(10, 9, 1, 10000, 100, 0)) < 1f, "one failure reduces the score");
        // Monotone in completed trips.
        float previous = -1f;
        for (int completed = 0; completed <= 10; completed++)
        {
            float score = metric.Score(Access(10, completed, 10 - completed, 10000, 100, 0));
            True(score >= previous, "score monotone in completed trips");
            previous = score;
        }
    }

    private static void Congestion()
    {
        EmploymentAccessMetric metric = new EmploymentAccessMetric(1000);
        // All trips complete but take double the budget: time factor 0 kills the score.
        Equal(0f, metric.Score(Access(10, 10, 0, 20000, 100, 0)), "double-budget travel time scores zero");
        // 1.5x budget: time factor 0.5.
        Equal(0.5f, metric.Score(Access(10, 10, 0, 15000, 100, 0)), "1.5x budget halves via time");
        // Congestion gradually, not a cliff: monotone in travel time.
        float previous = 1.01f;
        for (long ticks = 0; ticks <= 20000; ticks += 1000)
        {
            float score = metric.Score(Access(10, 10, 0, ticks, 100, 0));
            True(score <= previous + 0.0001f, "score monotone decreasing in travel time");
            previous = score;
        }
    }

    private static void MissingExternalConnection()
    {
        FreightOutflowMetric metric = new FreightOutflowMetric(100);
        // Deliveries happening, but stock positive and no external connection: zero.
        Equal(0f, metric.Score(Freight(10, 10, 0, 20, false)), "closed external connection with stock scores zero");
        // Closed connection but no stock: the internal flow still counts.
        True(metric.Score(Freight(10, 10, 0, 0, false)) > 0.9f, "closed connection without stock keeps internal score");
        // Open connection baseline.
        True(metric.Score(Freight(10, 10, 0, 20, true)) > 0f, "open connection with stock still scores");
    }

    private static void DestinationWithoutCapacity()
    {
        EmploymentAccessMetric metric = new EmploymentAccessMetric(1000);
        // Perfect trips, but destinations are 100% full: free capacity share 0.
        Equal(0f, metric.Score(Access(10, 10, 0, 5000, 100, 100)), "full destinations score zero");
        // No destination capacity observed at all: zero, never free access.
        Equal(0f, metric.Score(Access(10, 10, 0, 5000, 0, 0)), "no observed capacity scores zero");
        // Monotone in free capacity.
        float previous = -1f;
        for (int filled = 100; filled >= 0; filled -= 10)
        {
            float score = metric.Score(Access(10, 10, 0, 5000, 100, filled));
            True(score >= previous, "score monotone in free capacity");
            previous = score;
        }
    }

    private static void RouteWithoutArrival()
    {
        EmploymentAccessMetric employment = new EmploymentAccessMetric(1000);
        // Routes found (attempts) but nobody completed: zero access.
        Equal(0f, employment.Score(Access(10, 0, 10, 0, 100, 0)), "visual route without arrival is not access");
        FreightOutflowMetric freight = new FreightOutflowMetric(100);
        // Matches attempted but no goods delivered: zero outflow.
        Equal(0f, freight.Score(Freight(10, 0, 10, 0, true)), "route without delivery is not success");
        // Zero attempts: no measurement, zero score.
        Equal(0f, employment.Score(Access(0, 0, 0, 0, 100, 0)), "no attempts no access");
        Equal(0f, freight.Score(Freight(0, 0, 0, 0, true)), "no attempts no outflow");
    }

    private static void RoadExistenceAloneNeverScores()
    {
        EmploymentAccessMetric metric = new EmploymentAccessMetric(1000);
        // Roads exist (implied by attempts) but everything fails: still zero.
        Equal(0f, metric.Score(Access(50, 0, 50, 0, 100, 50)), "existing roads with total failure score zero");
        FreightOutflowMetric freight = new FreightOutflowMetric(100);
        Equal(0f, freight.Score(Freight(50, 0, 50, 10, true)), "existing roads without deliveries score zero");
    }

    private static void Validation()
    {
        EmploymentAccessMetric metric = new EmploymentAccessMetric(1000);
        Throws<ArgumentOutOfRangeException>(delegate { new EmploymentAccessMetric(0); }, "zero time budget rejected");
        Throws<ArgumentException>(delegate { metric.Score(Access(-1, 0, 0, 0, 10, 0)); }, "negative attempts rejected");
        Throws<ArgumentException>(delegate { metric.Score(Access(10, 6, 6, 0, 10, 0)); }, "outcomes above attempts rejected");
        Throws<ArgumentException>(delegate { metric.Score(Access(10, 10, 0, 0, 10, 11)); }, "filled above capacity rejected");
        FreightOutflowMetric freight = new FreightOutflowMetric(100);
        Throws<ArgumentOutOfRangeException>(delegate { new FreightOutflowMetric(0); }, "zero stock capacity rejected");
        Throws<ArgumentException>(delegate { freight.Score(Freight(-1, 0, 0, 0, true)); }, "negative freight attempts rejected");
        Throws<ArgumentException>(delegate { freight.Score(Freight(10, 6, 6, 0, true)); }, "freight outcomes above attempts rejected");
        Throws<ArgumentException>(delegate { freight.Score(Freight(10, 10, 0, -1, true)); }, "negative stock rejected");
    }

    private static void EffectBands()
    {
        AccessEffectBands bands = AccessEffectBands.Default;
        Equal(AccessEffectLevel.None, bands.Level(0.6f), "score 0.6 is no effect");
        Equal(AccessEffectLevel.Mild, bands.Level(0.4f), "score 0.4 is mild");
        Equal(AccessEffectLevel.Moderate, bands.Level(0.1f), "score 0.1 is moderate");
        Equal(AccessEffectLevel.Severe, bands.Level(0f), "score 0 is severe");
        Equal(1.25f, bands.ProductivityMultiplier(1f), "full score earns the full multiplier");
        Equal(1f, bands.ProductivityMultiplier(0f), "zero score keeps the floor multiplier");
        Equal(1.125f, bands.ProductivityMultiplier(0.5f), "multiplier is linear in the score");
        // Demand multiplier is the reciprocal policy: full access slightly suppresses new
        // demand, poor access raises it (people/businesses want what they lack is NOT the
        // model here — demand follows viability, so poor access lowers demand).
        True(bands.DemandMultiplier(1f) < bands.DemandMultiplier(0f), "poor access lowers demand multiplier");
        True(bands.DemandMultiplier(0.5f) >= bands.DemandMultiplier(1f), "demand multiplier monotone in score");
        Throws<ArgumentException>(delegate { new AccessEffectBands(0.2f, 0.4f, 1f, 1.25f); }, "severe above moderate rejected");
        Throws<ArgumentOutOfRangeException>(delegate { bands.Level(1.5f); }, "score above 1 rejected");
        Throws<ArgumentOutOfRangeException>(delegate { bands.ProductivityMultiplier(-0.1f); }, "negative score rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new AccessEffectBands(0.5f, 0.25f, 0f, 1.25f); }, "zero floor rejected");
    }

    private static void GradualEffects()
    {
        AccessEffectBands bands = AccessEffectBands.Default;
        float previous = bands.ProductivityMultiplier(0f);
        for (float score = 0f; score <= 1f; score += 0.05f)
        {
            float multiplier = bands.ProductivityMultiplier(score);
            True(multiplier >= previous, "productivity multiplier monotone in score");
            previous = multiplier;
        }
        // No cliff at band boundaries: multipliers are continuous even where levels jump.
        True(Math.Abs(bands.ProductivityMultiplier(0.5f) - bands.ProductivityMultiplier(0.4999f)) < 0.001f, "no cliff at moderate boundary");
        True(Math.Abs(bands.ProductivityMultiplier(0.25f) - bands.ProductivityMultiplier(0.2499f)) < 0.001f, "no cliff at severe boundary");
    }

    private static void AdapterOptIn()
    {
        True(AccessFreightSession.TryCreate(false, 1000, 100, AccessEffectBands.Default) == null, "disabled session is null");
        AccessFreightSession session = AccessFreightSession.TryCreate(true, 1000, 100, AccessEffectBands.Default);
        True(session != null, "enabled session exists");
        Equal(0.5f, session.EmploymentScore(Access(10, 10, 0, 5000, 100, 50)), "adapter employment score");
        Equal(AccessEffectLevel.Mild, session.EmploymentLevel(Access(10, 10, 0, 5000, 100, 75)), "adapter employment level");
        True(session.FreightScore(Freight(10, 10, 0, 0, true)) > 0.9f, "adapter freight score");
        Equal(AccessEffectLevel.None, session.FreightLevel(Freight(10, 10, 0, 0, true)), "adapter freight level");
        Throws<ArgumentNullException>(delegate { AccessFreightSession.TryCreate(true, 1000, 100, null); }, "adapter rejects null bands");
    }

    private static void BatchOrder()
    {
        AccessFreightSession session = AccessFreightSession.TryCreate(true, 1000, 100, AccessEffectBands.Default);
        EmploymentAccessObservation[] employment = new[]
        {
            Access(10, 10, 0, 5000, 100, 0),   // perfect
            Access(10, 0, 10, 0, 100, 50),     // zero
            Access(10, 10, 0, 5000, 100, 50)   // half
        };
        FreightOutflowObservation[] freight = new[]
        {
            Freight(10, 0, 10, 0, true),       // zero
            Freight(10, 10, 0, 0, true)        // full
        };
        AccessFreightBatch batch = session.Evaluate(employment, freight);
        Equal(3, batch.EmploymentScores.Length, "employment batch length");
        Equal(2, batch.FreightScores.Length, "freight batch length");
        Equal(1f, batch.EmploymentScores[0], "first origin perfect");
        Equal(0f, batch.EmploymentScores[1], "second origin zero");
        Equal(0.5f, batch.EmploymentScores[2], "third origin half");
        Equal(AccessEffectLevel.Severe, batch.EmploymentLevels[1], "levels follow scores in order");
        Equal(AccessEffectLevel.Severe, batch.FreightLevels[0], "freight level zero origin");
        Equal(AccessEffectLevel.None, batch.FreightLevels[1], "freight level full origin");
        // Per-origin results land on the right building: scores are position-aligned.
        True(batch.EmploymentScores[0] > batch.EmploymentScores[2] && batch.EmploymentScores[2] > batch.EmploymentScores[1], "order preserved");
        Throws<ArgumentNullException>(delegate { session.Evaluate(null, freight); }, "null employment batch rejected");
        Throws<ArgumentNullException>(delegate { session.Evaluate(employment, null); }, "null freight batch rejected");
    }

    private static void Determinism()
    {
        AccessFreightSession first = AccessFreightSession.TryCreate(true, 1000, 100, AccessEffectBands.Default);
        AccessFreightSession second = AccessFreightSession.TryCreate(true, 1000, 100, AccessEffectBands.Default);
        EmploymentAccessObservation access = Access(7, 5, 2, 8000, 40, 15);
        FreightOutflowObservation freight = Freight(9, 6, 3, 12, true);
        Equal(first.EmploymentScore(access), second.EmploymentScore(access), "employment score deterministic");
        Equal(first.FreightScore(freight), second.FreightScore(freight), "freight score deterministic");
        Equal((int)first.EmploymentLevel(access), (int)second.EmploymentLevel(access), "level deterministic");
    }

    private static void Equal(int expected, int actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(float expected, float actual, string label)
    {
        _checks++;
        if (Math.Abs(expected - actual) > 0.0001f) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(AccessEffectLevel expected, AccessEffectLevel actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void True(bool condition, string label)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(label + ": condition false.");
    }

    private static void Throws<TException>(Action action, string label) where TException : Exception
    {
        _checks++;
        try { action(); }
        catch (TException) { return; }
        catch (Exception error)
        {
            throw new InvalidOperationException(label + ": wrong exception type " + error.GetType().Name + ".");
        }
        throw new InvalidOperationException(label + ": no exception thrown.");
    }
}
