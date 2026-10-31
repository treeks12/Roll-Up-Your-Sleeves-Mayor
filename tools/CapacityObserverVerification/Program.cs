using System;
using System.Reflection;
using System.Runtime.Serialization;
using ColossalFramework.Math;
using SC4Evolution.Mod.Economy;
using SC4Evolution.Simulation;

internal static class Program
{
    private static int _checks;
    private static readonly MethodInfo Scan = typeof(ResidentialCapacityObserver).GetMethod("InspectChain", BindingFlags.Static | BindingFlags.NonPublic);
    private static int Main()
    {
        try
        {
            MethodInfo native = typeof(ResidentialBuildingAI).GetMethod("CalculateHomeCount", new[] { typeof(ItemClass.Level), typeof(Randomizer), typeof(int), typeof(int) });
            Check(native != null && native.IsPublic && native.IsVirtual && native.DeclaringType == typeof(ResidentialBuildingAI) && native.ReturnType == typeof(int), "exact public native home-count API");
            PrefabBinding();
            CitizenUnit[] units = new CitizenUnit[8]; Citizen[] people = new Citizen[12];
            units[1] = Home(); AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.None, 1, 1);
            for (uint i = 1; i <= 5; i++) { people[i].m_flags = Citizen.Flags.Created; people[i].m_homeBuilding = 7; }
            units[1].m_citizen0 = 1; units[1].m_citizen1 = 2; units[1].m_citizen2 = 3; units[1].m_citizen3 = 4; units[1].m_citizen4 = 5;
            AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.None, 1, 1); // five people still one HOME
            people[1].m_flags |= Citizen.Flags.MovingIn;
            AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.None, 1, 1); // MovingIn never changes capacity
            people[1].m_flags |= Citizen.Flags.Dead;
            AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.None, 1, 1); // nominal HOME, not a resident census
            units[1] = Home(); units[1].m_nextUnit = 2; units[2] = Home();
            AssertScan(units, people, 1, 2, ResidentialCapacityObservationReason.None, 2, 2);
            AssertScan(units, people, 1, 3, ResidentialCapacityObservationReason.HomeCountMismatch, 2, 2);
            AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.TooManyHomeUnits, 2, 2);
            AssertScan(units, people, 0, 1, ResidentialCapacityObservationReason.MissingHomeChain, 0, 0);
            AssertScan(units, people, 8, 1, ResidentialCapacityObservationReason.UnitIndexOutsideBuffer, 0, 0);
            units[2].m_nextUnit = 1;
            AssertScan(units, people, 1, 3, ResidentialCapacityObservationReason.UnitCycle, 2, 2);
            units[1].m_nextUnit = 8;
            AssertScan(units, people, 1, 2, ResidentialCapacityObservationReason.UnitIndexOutsideBuffer, 1, 1);
            units[1] = Home(); units[1].m_flags = CitizenUnit.Flags.Home;
            AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.UnitNotCreated, 0, 1);
            units[1] = Home(); units[1].m_building = 8;
            AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.UnitOwnerMismatch, 0, 1);
            units[1] = Home(); units[1].m_flags |= CitizenUnit.Flags.Work;
            AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.UnexpectedUnitType, 0, 1);
            units[1] = Home(); units[1].m_flags = CitizenUnit.Flags.Created | CitizenUnit.Flags.Hotel;
            AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.UnexpectedUnitType, 0, 1);
            units[1] = Home(); units[1].m_citizen0 = 12;
            AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.CitizenIndexOutsideBuffer, 1, 1);
            units[1].m_citizen0 = 6;
            AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.CitizenNotCreated, 1, 1);
            units[1].m_citizen0 = 1; people[1].m_homeBuilding = 8;
            AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.CitizenHomeMismatch, 1, 1);
            people[1].m_homeBuilding = 7; units[1].m_citizen1 = 1;
            AssertScan(units, people, 1, 1, ResidentialCapacityObservationReason.DuplicateCitizen, 1, 1);
            units[1] = Home(); units[2] = Home(); units[1].m_nextUnit = 2; units[1].m_citizen0 = 1; units[2].m_citizen0 = 1;
            AssertScan(units, people, 1, 2, ResidentialCapacityObservationReason.DuplicateCitizen, 2, 2);
            CitizenUnit[] large = new CitizenUnit[1025];
            for (int i = 1; i < large.Length; i++) { large[i] = Home(); large[i].m_nextUnit = i == large.Length - 1 ? 0 : (uint)(i + 1); }
            AssertScan(large, people, 1, 2, ResidentialCapacityObservationReason.TooManyHomeUnits, 3, 3);
            AssertScan(large, people, 1, 1024, ResidentialCapacityObservationReason.None, 1024, 1024);
            Console.WriteLine("PASS: " + _checks + " isolated observer checks using real native Citizen/CitizenUnit structs. No manager/native AI invocation or city proof.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static CitizenUnit Home() { CitizenUnit value = new CitizenUnit(); value.m_flags = CitizenUnit.Flags.Created | CitizenUnit.Flags.Home; value.m_building = 7; return value; }
    private static void PrefabBinding()
    {
        // Managed objects only: no Unity constructor, native AI call or manager.
        // Reproduce the reference mismatch which HOME-count equality cannot expose.
        BuildingInfo low = (BuildingInfo)FormatterServices.GetUninitializedObject(typeof(BuildingInfo));
        BuildingInfo other = (BuildingInfo)FormatterServices.GetUninitializedObject(typeof(BuildingInfo));
        ResidentialBuildingAI ai = (ResidentialBuildingAI)FormatterServices.GetUninitializedObject(typeof(ResidentialBuildingAI));
        ResidentialBuildingAI replacement = (ResidentialBuildingAI)FormatterServices.GetUninitializedObject(typeof(ResidentialBuildingAI));
        MethodInfo guard = typeof(ResidentialCapacityObserver).GetMethod("CoherentPrefabBinding", BindingFlags.Static | BindingFlags.NonPublic);
        low.m_buildingAI = ai; ai.m_info = other;
        Equal(false, guard.Invoke(null, new object[] { ai, low }), "Low prefab with AI backpointer to Other is incoherent");
        ResidentialHomeCountBounds lowHomes = ResidentialNominalCapacity.GetHomeCountBounds(ResidentialNominalCategory.LowGeneric, 0, 1, 1);
        ResidentialHomeCountBounds otherHomes = ResidentialNominalCapacity.GetHomeCountBounds(ResidentialNominalCategory.Other, 0, 1, 1);
        Equal(1L, lowHomes.MinimumHomes, "Low1x1 minimum is one HOME"); Equal(1L, lowHomes.MaximumHomes, "Low1x1 maximum is one HOME");
        Equal(1L, otherHomes.MinimumHomes, "Other1x1 minimum is also one HOME"); Equal(1L, otherHomes.MaximumHomes, "Other1x1 maximum is also one HOME");
        Equal(13L, ResidentialNominalCapacity.ComputeNominalQ(ResidentialNominalCategory.LowGeneric, 1), "Low nominal13Q");
        Equal(5L, ResidentialNominalCapacity.ComputeNominalQ(ResidentialNominalCategory.Other, 1), "Other nominal5Q despite same HOME count");
        ai.m_info = low;
        Equal(true, guard.Invoke(null, new object[] { ai, low }), "coherent bidirectional binding accepted by reference guard");
        low.m_buildingAI = replacement; replacement.m_info = low;
        Equal(false, guard.Invoke(null, new object[] { ai, low }), "captured AI replaced on same prefab is incoherent");
        Equal(true, guard.Invoke(null, new object[] { replacement, low }), "new captured AI can have coherent binding");
        replacement.m_info = other;
        Equal(false, guard.Invoke(null, new object[] { replacement, low }), "late backpointer change invalidates reference guard");
        Equal(false, guard.Invoke(null, new object[] { null, low }), "null AI fails without Unity equality operator");
        Equal(false, guard.Invoke(null, new object[] { ai, null }), "null prefab fails without Unity equality operator");
    }
    private static void AssertScan(CitizenUnit[] units, Citizen[] people, uint root, int expected, ResidentialCapacityObservationReason reason, int homes, int inspected)
    {
        CitizenUnit[] originalUnits = (CitizenUnit[])units.Clone(); Citizen[] originalPeople = (Citizen[])people.Clone();
        object[] args = { units, people, (ushort)7, root, expected, 0, 0 };
        Equal(reason, Scan.Invoke(null, args), "coherence reason"); Equal(homes, args[5], "observed HOME units"); Equal(inspected, args[6], "bounded inspection count");
        for (int i = 0; i < units.Length; i++) Check(units[i].Equals(originalUnits[i]), "native unit struct unchanged");
        for (int i = 0; i < people.Length; i++) Check(people[i].Equals(originalPeople[i]), "native citizen struct unchanged");
    }
    private static void Equal(object expected, object actual, string name) { _checks++; if (!object.Equals(expected, actual)) throw new Exception(name + ": expected " + expected + ", got " + actual); }
    private static void Check(bool valid, string name) { _checks++; if (!valid) throw new Exception(name); }
}
