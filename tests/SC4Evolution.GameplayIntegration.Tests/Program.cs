using System;
using SC4Evolution.Core;
using SC4Evolution.Core.Decisions;
using SC4Evolution.Mod.Economy;
using SC4Evolution.Simulation;
using SC4Evolution.Simulation.Access;
using SC4Evolution.Simulation.Employment;
using SC4Evolution.Simulation.Freight;
using SC4Evolution.Simulation.Neighborhoods;
using SC4Evolution.Simulation.Taxation;

internal static class Program
{
    private static int _checks;

    private static int Main()
    {
        try
        {
            OffIsNeutral();
            EmploymentIsolated();
            NeighborhoodIsolated();
            TaxationIsolated();
            AccessIsolated();
            DecisionsIsolated();
            DeferredIdentityFactory();
            Combination();
            Determinism();
            ValidationAndStaleFrames();
            Console.WriteLine("PASS: " + _checks + " gameplay integration assertions; pure opt-in orchestrator, no native writes.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static void OffIsNeutral()
    {
        GameplayIntegrationSession session = new GameplayIntegrationSession("city-a",
            new GameplayIntegrationConfig(GameplaySprint.None));
        GameplayIntegrationSnapshot snapshot = session.Apply(Sample("city-a", 1));
        True(snapshot.Employment == null, "OFF employment report absent");
        True(snapshot.Neighborhoods == null, "OFF neighborhood report absent");
        True(snapshot.Taxation == null, "OFF tax report absent");
        True(snapshot.AccessFreight == null, "OFF access report absent");
        Equal(0, snapshot.DecisionNotices.Length, "OFF notices empty");
        True(session.Employment == null && session.Neighborhoods == null && session.Taxation == null && session.AccessFreight == null && session.Decisions == null,
            "OFF does not construct sprint sessions");
    }

    private static void EmploymentIsolated()
    {
        GameplayIntegrationSession session = new GameplayIntegrationSession("city-a",
            new GameplayIntegrationConfig(GameplaySprint.Employment));
        GameplayIntegrationSnapshot snapshot = session.Apply(new GameplayIntegrationObservation("city-a", 1,
            Employment(), null, null, null, new GameplayDecisionObservation[0]));
        True(snapshot.Employment != null, "employment report exists");
        True(snapshot.Neighborhoods == null && snapshot.Taxation == null && snapshot.AccessFreight == null,
            "employment isolation");
        Equal((uint)1, snapshot.Employment.Frame, "employment frame");
    }

    private static void NeighborhoodIsolated()
    {
        GameplayIntegrationSession session = new GameplayIntegrationSession("city-a",
            new GameplayIntegrationConfig(GameplaySprint.Neighborhoods));
        GameplayIntegrationSnapshot snapshot = session.Apply(new GameplayIntegrationObservation("city-a", 1,
            null, Neighborhood(), null, null, new GameplayDecisionObservation[0]));
        True(snapshot.Neighborhoods != null, "neighborhood report exists");
        True(snapshot.Employment == null && snapshot.Taxation == null && snapshot.AccessFreight == null,
            "neighborhood isolation");
        Equal(1, snapshot.Neighborhoods.Lots.Length, "one neighborhood lot");
        Equal(0, snapshot.NeighborhoodLotsWithoutFactorSamples, "all factors sampled");
    }

    private static void TaxationIsolated()
    {
        GameplayIntegrationSession session = new GameplayIntegrationSession("city-a",
            new GameplayIntegrationConfig(GameplaySprint.Taxation));
        GameplayIntegrationSnapshot snapshot = session.Apply(new GameplayIntegrationObservation("city-a", 1,
            null, null, Taxation(1), null, new GameplayDecisionObservation[0]));
        True(snapshot.Taxation != null, "tax report exists");
        True(snapshot.Employment == null && snapshot.Neighborhoods == null && snapshot.AccessFreight == null,
            "tax isolation");
        Equal(1, snapshot.Taxation.Day, "tax day");
    }

    private static void AccessIsolated()
    {
        GameplayIntegrationSession session = new GameplayIntegrationSession("city-a",
            new GameplayIntegrationConfig(GameplaySprint.AccessFreight));
        GameplayIntegrationSnapshot snapshot = session.Apply(new GameplayIntegrationObservation("city-a", 1,
            null, null, null, Access(), new GameplayDecisionObservation[0]));
        True(snapshot.AccessFreight != null, "access report exists");
        True(snapshot.Employment == null && snapshot.Neighborhoods == null && snapshot.Taxation == null,
            "access isolation");
        Equal(1, snapshot.AccessFreight.EmploymentScores.Length, "one employment origin");
        Equal(1, snapshot.AccessFreight.FreightScores.Length, "one freight origin");
    }

    private static void DecisionsIsolated()
    {
        GameplayIntegrationSession session = new GameplayIntegrationSession("city-a",
            new GameplayIntegrationConfig(GameplaySprint.Decisions));
        GameplayIntegrationSnapshot snapshot = session.Apply(new GameplayIntegrationObservation("city-a", 1,
            null, null, null, null, new[] { Decision("tax:r", true) }));
        Equal(1, snapshot.DecisionNotices.Length, "decision notice emitted");
        Equal("Lower R$ rate", snapshot.DecisionNotices[0].Message, "decision message resolved");
        True(snapshot.Employment == null && snapshot.Neighborhoods == null && snapshot.Taxation == null && snapshot.AccessFreight == null,
            "decision isolation");
    }

    private static void Combination()
    {
        GameplaySprint all = GameplaySprint.Employment | GameplaySprint.Neighborhoods |
            GameplaySprint.Taxation | GameplaySprint.AccessFreight | GameplaySprint.Decisions;
        GameplayIntegrationSession session = new GameplayIntegrationSession("city-a",
            new GameplayIntegrationConfig(all));
        GameplayIntegrationSnapshot snapshot = session.Apply(new GameplayIntegrationObservation("city-a", 1,
            Employment(), Neighborhood(), Taxation(1), Access(), new[] { Decision("tax:r", true) }));
        True(snapshot.Employment != null && snapshot.Neighborhoods != null && snapshot.Taxation != null && snapshot.AccessFreight != null,
            "combined reports present");
        Equal(1, snapshot.DecisionNotices.Length, "combined notice present");
        Equal(all, snapshot.EnabledSprints, "combined flags preserved");
    }

    private static void DeferredIdentityFactory()
    {
        True(GameplayIntegrationSession.TryCreate(false, new GameplayIntegrationConfig(GameplaySprint.Employment)) == null,
            "disabled TryCreate returns null");
        GameplayIntegrationSession session = GameplayIntegrationSession.TryCreate(true,
            new GameplayIntegrationConfig(GameplaySprint.Employment));
        GameplayIntegrationSnapshot snapshot = session.Apply(new GameplayIntegrationObservation("bound-city", 1,
            Employment(), null, null, null, new GameplayDecisionObservation[0]));
        Equal("bound-city", snapshot.CityIdentity, "TryCreate binds first city identity");
        Throws<ArgumentException>(delegate
        {
            session.Apply(new GameplayIntegrationObservation("other-city", 2, Employment(), null, null, null,
                new GameplayDecisionObservation[0]));
        }, "TryCreate keeps bound identity");
    }

    private static void Determinism()
    {
        GameplayIntegrationConfig config = new GameplayIntegrationConfig(GameplaySprint.Employment | GameplaySprint.Decisions);
        GameplayIntegrationObservation observation = new GameplayIntegrationObservation("city-a", 1,
            Employment(), null, null, null, new[] { Decision("tax:r", true) });
        GameplayIntegrationSnapshot first = new GameplayIntegrationSession("city-a", config).Apply(observation);
        GameplayIntegrationSnapshot second = new GameplayIntegrationSession("city-a", config).Apply(observation);
        Equal(first.Employment.TotalUnemployed(), second.Employment.TotalUnemployed(), "employment deterministic");
        Equal(first.Employment.TotalImmigrationGranted(), second.Employment.TotalImmigrationGranted(), "immigration report deterministic");
        Equal(first.DecisionNotices[0].EventId, second.DecisionNotices[0].EventId, "decision id deterministic");
        Equal(first.DecisionNotices[0].Message, second.DecisionNotices[0].Message, "decision text deterministic");
    }

    private static void ValidationAndStaleFrames()
    {
        GameplayIntegrationSession session = new GameplayIntegrationSession("city-a",
            new GameplayIntegrationConfig(GameplaySprint.Employment));
        session.Apply(new GameplayIntegrationObservation("city-a", 10, Employment(), null, null, null,
            new GameplayDecisionObservation[0]));
        Throws<ArgumentException>(delegate
        {
            session.Apply(new GameplayIntegrationObservation("city-a", 9, Employment(), null, null, null,
                new GameplayDecisionObservation[0]));
        }, "stale frame rejected");
        Throws<ArgumentException>(delegate
        {
            session.Apply(new GameplayIntegrationObservation("other-city", 11, Employment(), null, null, null,
                new GameplayDecisionObservation[0]));
        }, "city identity rejected");
        Throws<ArgumentException>(delegate
        {
            new GameplayNeighborhoodObservation(new[] { Lot(2), Lot(1) }, new[] { 1, 0, 0 });
        }, "neighborhood order rejected");
        NeighborhoodFactors invalid = NeighborhoodFactors.Neutral();
        invalid.Traffic = float.NaN;
        Throws<ArgumentException>(delegate { Lot(1, invalid); }, "non-finite factor rejected");
        Throws<ArgumentException>(delegate
        {
            new GameplayAccessFreightObservation(new[] { AccessObservation(2), AccessObservation(1) }, new[] { FreightObservation(1) });
        }, "access identity order rejected");
        Throws<ArgumentException>(delegate
        {
            new GameplayIntegrationObservation("city-a", 1, null, null, null, null,
                new[] { Decision("z", true), Decision("a", true) });
        }, "decision order rejected");
    }

    private static GameplayIntegrationObservation Sample(string city, uint frame)
    {
        return new GameplayIntegrationObservation(city, frame, Employment(), Neighborhood(), Taxation(1), Access(),
            new[] { Decision("tax:r", true) });
    }

    private static GameplayEmploymentObservation Employment()
    {
        return new GameplayEmploymentObservation(new int[EmploymentSegments.WorkforceCells],
            new int[EmploymentSegments.Segments], new int[EmploymentSegments.Segments], 0, 0, 2, true);
    }

    private static GameplayNeighborhoodObservation Neighborhood()
    {
        return new GameplayNeighborhoodObservation(new[] { Lot(1) }, new[] { 1, 0, 0 });
    }

    private static GameplayNeighborhoodLotObservation Lot(ushort id)
    {
        return Lot(id, NeighborhoodFactors.Neutral());
    }

    private static GameplayNeighborhoodLotObservation Lot(ushort id, NeighborhoodFactors factors)
    {
        return new GameplayNeighborhoodLotObservation(id, (uint)id, 1, ResidentialStockPhase.Completed,
            1, 1, 0, 0, 0, 0, 0, 0, factors);
    }

    private static GameplayTaxObservation Taxation(int day)
    {
        int[] rates = new int[TaxAccounts.Accounts];
        long[] bases = new long[TaxAccounts.Accounts];
        long[] collected = new long[TaxAccounts.Accounts];
        rates[0] = 10; bases[0] = 100; collected[0] = 5;
        return new GameplayTaxObservation(day, rates, bases, collected);
    }

    private static GameplayAccessFreightObservation Access()
    {
        return new GameplayAccessFreightObservation(new[] { AccessObservation(1) },
            new[] { FreightObservation(1) });
    }

    private static EmploymentAccessObservation AccessObservation(ushort id)
    {
        EmploymentAccessObservation value = new EmploymentAccessObservation();
        value.OriginId = id; value.RouteAttempts = 10; value.TripsCompleted = 10;
        value.ArrivalFailures = 0; value.TravelTimeTicks = 5000;
        value.JobCapacityAtDestinations = 100; value.FilledJobsAtDestinations = 50;
        return value;
    }

    private static FreightOutflowObservation FreightObservation(ushort id)
    {
        FreightOutflowObservation value = new FreightOutflowObservation();
        value.OriginId = id; value.RouteAttempts = 10; value.DeliveriesCompleted = 10;
        value.TransferFailures = 0; value.StockUnits = 0; value.ExternalConnectionOpen = true;
        return value;
    }

    private static GameplayDecisionObservation Decision(string key, bool active)
    {
        AdvisorRecommendation recommendation = AdvisorRecommendation.Create(AdvisorId.Finance, "Finance",
            DecisionConsequence.TaxRevenueLoss, "Lower R$ rate", "Tax delta", 1, 0, "");
        return new GameplayDecisionObservation(key, active, recommendation, DecisionUrgency.Attention,
            "Finance Advisor", "{0}", "{0}");
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        _checks++;
        if (!object.Equals(expected, actual)) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
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
        catch (Exception error) { throw new InvalidOperationException(label + ": wrong exception " + error.GetType().Name + "."); }
        throw new InvalidOperationException(label + ": no exception thrown.");
    }
}
