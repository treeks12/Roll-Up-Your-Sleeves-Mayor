using System;
using System.Collections.Generic;
using SC4Evolution.Simulation;

internal static class Program
{
    private static int _checks;
    private static int Main()
    {
        try
        {
            EmptyAndUnknownClass(); ConstructionDoesNotDisappearOnCompletion(); PeopleAreNotHouseholdCapacity();
            DistinctPhasesAndClasses(); PooledIdentityAndIsolation(); InvalidReferencesRemainVisible();
            ValidateInputs(); LargeTotals();
            Console.WriteLine("PASS: " + _checks + " residential stock assertions in 8 synthetic scenarios; no nominal capacity or native execution.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }

    private static ResidentialStockLot Lot(ushort id, int cls, ResidentialStockPhase phase,
        int homes = 1, int empty = 1, int live = 0, int other = 0, int settled = 0, int incoming = 0,
        int unlinked = 0, int invalid = 0, uint index = 0)
    { return new ResidentialStockLot(id, index, cls, phase, homes, empty, live, other, settled, incoming, unlinked, invalid); }
    private static ResidentialStockSnapshot Stock(uint frame, params ResidentialStockLot[] lots)
    { return new ResidentialStockSnapshot(frame, lots); }
    private static void Equal<T>(T expected, T actual, string name)
    { _checks++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(name + ": expected " + expected + ", got " + actual); }
    private static void Throws(Action action, string name)
    { _checks++; try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected rejected input: " + name); }

    private static void EmptyAndUnknownClass()
    {
        ResidentialStockSnapshot empty = Stock(0);
        Equal(0, empty.Totals.Lots, "empty city"); Equal(0L, empty.Totals.HomeUnits, "no fabricated homes");
        ResidentialStockSnapshot unbound = Stock(7, Lot(1, 0, ResidentialStockPhase.Completed));
        Equal(1, unbound.GetBucket(0, ResidentialStockPhase.Completed).Lots, "unbound kept distinct");
        Equal(0, unbound.GetBucket(1, ResidentialStockPhase.Completed).Lots, "unbound is not Low");
        Equal(7u, unbound.Frame, "frame retained");
    }
    private static void ConstructionDoesNotDisappearOnCompletion()
    {
        ResidentialStockSnapshot before = Stock(10, Lot(1, 1, ResidentialStockPhase.Constructing, 4, 4));
        ResidentialStockSnapshot after = Stock(11, Lot(1, 1, ResidentialStockPhase.Completed, 4, 4));
        Equal(4L, before.Totals.EmptyHomeUnits, "empty construction exists before people");
        Equal(before.Totals.HomeUnits, after.Totals.HomeUnits, "completion retains stock");
        Equal(0L, after.Totals.SettledPeople, "completion creates no people");
        Equal(0, after.GetBucket(1, ResidentialStockPhase.Constructing).Lots, "phase changed");
        Equal(1, after.GetBucket(1, ResidentialStockPhase.Completed).Lots, "completed once");
        Equal(1, before.GetBucket(1, ResidentialStockPhase.Constructing).Lots, "old frame immutable");
    }
    private static void PeopleAreNotHouseholdCapacity()
    {
        ResidentialStockSnapshot arrivals = Stock(20, Lot(1, 1, ResidentialStockPhase.Completed, 3, 1, 2, 0, 2, 4));
        Equal(3L, arrivals.Totals.HomeUnits, "three households");
        Equal(1L, arrivals.Totals.EmptyHomeUnits, "one truly empty unit");
        Equal(2L, arrivals.Totals.LiveHomeUnits, "incoming occupies a unit");
        Equal(2L, arrivals.Totals.SettledPeople, "settled individuals");
        Equal(4L, arrivals.Totals.IncomingPeople, "moving-in individuals separate");
        ResidentialStockSnapshot settled = Stock(21, Lot(1, 1, ResidentialStockPhase.Completed, 3, 1, 2, 0, 6));
        Equal(arrivals.Totals.HomeUnits, settled.Totals.HomeUnits, "arrival does not add stock");
        Equal(6L, settled.Totals.SettledPeople, "flag transition to settled");
        Equal(0L, settled.Totals.IncomingPeople, "no duplicate incoming people");
        // There is deliberately no HomeUnits*5 conversion or nominal person-capacity field.
    }
    private static void DistinctPhasesAndClasses()
    {
        List<ResidentialStockLot> lots = new List<ResidentialStockLot>(); ushort id = 0;
        for (int cls = 0; cls <= 3; cls++)
            for (int phase = 1; phase <= 4; phase++) lots.Add(Lot(++id, cls, (ResidentialStockPhase)phase));
        ResidentialStockSnapshot stock = new ResidentialStockSnapshot(30, lots);
        Equal(16, stock.Totals.Lots, "all class/phase buckets");
        for (int cls = 0; cls <= 3; cls++)
            for (int phase = 1; phase <= 4; phase++)
            { Equal(1, stock.GetBucket(cls, (ResidentialStockPhase)phase).Lots, "distinct bucket"); Equal(1L, stock.GetBucket(cls, (ResidentialStockPhase)phase).EmptyHomeUnits, "per-bucket homes"); }
        Equal(16L, stock.Totals.HomeUnits, "inventory includes unavailable separately");
    }
    private static void PooledIdentityAndIsolation()
    {
        ResidentialStockLot first = Lot(5, 3, ResidentialStockPhase.Completed, index: 99);
        ResidentialStockLot[] input = { first };
        ResidentialStockSnapshot before = new ResidentialStockSnapshot(40, input);
        input[0] = Lot(6, 1, ResidentialStockPhase.Constructing);
        ResidentialStockLot[] returned = before.Lots; returned[0] = input[0];
        Equal((ushort)5, before.Lots[0].BuildingId, "caller cannot replace observation");
        Equal(99u, before.Lots[0].ObservedBuildIndex, "index is observation only");
        ResidentialStockSnapshot released = Stock(41);
        Equal(0, released.Totals.Lots, "released stock absent");
        ResidentialStockSnapshot reused = Stock(42, Lot(5, 1, ResidentialStockPhase.Constructing, index: 100));
        Equal(0, reused.GetBucket(3, ResidentialStockPhase.Completed).Lots, "no inherited old class");
        Equal(100u, reused.Lots[0].ObservedBuildIndex, "new index");
        Equal(1, before.GetBucket(3, ResidentialStockPhase.Completed).Lots, "historical frame unchanged");
        Throws(() => Stock(42, first, reused.Lots[0]), "one building cannot appear twice in a frame");
    }
    private static void InvalidReferencesRemainVisible()
    {
        ResidentialStockSnapshot stock = Stock(50, Lot(1, 1, ResidentialStockPhase.Completed, 3, 1, 1, 1, 2, 1, 1, 2));
        Equal(1L, stock.Totals.EmptyHomeUnits, "stale or dead members are not vacancy");
        Equal(1L, stock.Totals.OtherNonemptyHomeUnits, "nonempty without live resident");
        Equal(2L, stock.Totals.InvalidMemberReferences, "broken or duplicate references visible");
        Equal(1L, stock.Totals.PeopleWithoutHomeUnitReference, "home assignment without unit visible");
        // An inconsistent native binding is observed; it is never patched into a plausible capacity.
        ResidentialStockSnapshot orphan = Stock(51, Lot(1, 1, ResidentialStockPhase.Completed, 0, 0, 0, 0, 2, 0, 2));
        Equal(2L, orphan.Totals.PeopleWithoutHomeUnitReference, "two orphaned home bindings");
        Equal(0L, orphan.Totals.HomeUnits, "no fabricated unit for orphan");
    }
    private static void ValidateInputs()
    {
        Throws(() => Lot(0, 1, ResidentialStockPhase.Completed), "zero building");
        Throws(() => Lot(1, -1, ResidentialStockPhase.Completed), "negative class");
        Throws(() => Lot(1, 4, ResidentialStockPhase.Completed), "unknown class");
        Throws(() => Lot(1, 1, 0), "zero phase");
        Throws(() => Lot(1, 1, (ResidentialStockPhase)5), "unknown phase");
        Throws(() => Lot(1, 1, ResidentialStockPhase.Completed, -1), "negative count");
        Throws(() => Lot(1, 1, ResidentialStockPhase.Completed, 1, 1, 1), "household partition");
        Throws(() => Lot(1, 1, ResidentialStockPhase.Completed, unlinked: 1), "unlinked exceeds people");
        Throws(() => new ResidentialStockSnapshot(0, null), "null observations");
        Throws(() => Stock(0, new ResidentialStockLot[] { null }), "null lot");
        ResidentialStockSnapshot stock = Stock(0);
        Throws(() => stock.GetBucket(4, ResidentialStockPhase.Completed), "invalid query class");
        Throws(() => stock.GetBucket(0, 0), "invalid query phase");
    }
    private static void LargeTotals()
    {
        ResidentialStockSnapshot stock = Stock(uint.MaxValue,
            Lot(1, 1, ResidentialStockPhase.Completed, int.MaxValue, int.MaxValue, settled: int.MaxValue),
            Lot(2, 1, ResidentialStockPhase.Completed, int.MaxValue, int.MaxValue, settled: int.MaxValue));
        Equal(4294967294L, stock.Totals.HomeUnits, "no int total wrap");
        Equal(4294967294L, stock.GetBucket(1, ResidentialStockPhase.Completed).SettledPeople, "no bucket wrap");
        Equal(uint.MaxValue, stock.Frame, "full frame range");
    }
}
