using System;
using System.Collections.Generic;
using System.Threading;
using ColossalFramework;
using ColossalFramework.Math;
using SC4Evolution.Simulation;

namespace SC4Evolution.Mod.Economy
{
    internal enum ResidentialCapacityObservationStatus { Unknown = 0, Known = 1 }
    internal enum ResidentialCapacityObservationPhase { Unknown = 0, Constructing = 1, Completed = 2, Upgrading = 3, Unavailable = 4 }
    internal enum ResidentialCapacityObservationReason
    {
        None, NotSimulationThread, UnsupportedGameVersion, ManagersUnavailable, InvalidBuildingId,
        NotCreated, BuildingUnavailable, UpgradeInProgress, BuildingSnapshotMismatch, IdentityUnavailable,
        UnsupportedAI, AIPrefabMismatch, InvalidCategoryOrGeometry, NativeHomeCountOutsideProfile, HomeCountExceedsUnitBuffer, MissingHomeChain,
        UnitIndexOutsideBuffer, UnitCycle, UnitNotCreated, UnitOwnerMismatch, UnexpectedUnitType,
        CitizenIndexOutsideBuffer, CitizenNotCreated, CitizenHomeMismatch, DuplicateCitizen,
        TooManyHomeUnits, HomeCountMismatch, ObservationChanged, ReadFailed
    }

    // Contains values and an immutable Simulation identity, never native buffers/AI/prefab references.
    // Known certifies only this local observation. Global patch compatibility is not certified.
    internal sealed class ResidentialCapacityObservation
    {
        internal ResidentialCapacityObservationStatus Status { get; private set; }
        internal ResidentialCapacityObservationReason Reason { get; private set; }
        internal string Detail { get; private set; }
        internal ResidentialCapacityObservationPhase Phase { get; private set; }
        internal ushort BuildingId { get; private set; }
        internal uint Frame { get; private set; }
        internal ResidentialLotIdentity Identity { get; private set; }
        internal ResidentialNominalCategory Category { get; private set; }
        internal int NativeLevel { get; private set; }
        internal int ExpectedHomeUnits { get; private set; }
        internal int ObservedHomeUnits { get; private set; }
        internal int InspectedUnits { get; private set; }
        internal long CapacityQ { get; private set; }
        internal string Compatibility { get { return "Uncertified"; } }
        internal string ProfileName { get { return ResidentialNominalCapacity.PolicyName; } }
        internal uint ProfileVersion { get { return ResidentialNominalCapacity.PolicyVersion; } }
        internal byte[] ProfileHash { get { return ResidentialNominalCapacity.PolicyHash; } }

        internal ResidentialCapacityObservation(ResidentialCapacityObservationStatus status, ResidentialCapacityObservationReason reason,
            string detail, ResidentialCapacityObservationPhase phase, ushort id, uint frame, ResidentialLotIdentity identity,
            ResidentialNominalCategory category, int nativeLevel, int expected, int observed, int inspected, long capacityQ)
        {
            Status = status; Reason = reason; Detail = detail; Phase = phase; BuildingId = id; Frame = frame;
            Identity = identity; Category = category; NativeLevel = nativeLevel; ExpectedHomeUnits = expected;
            ObservedHomeUnits = observed; InspectedUnits = inspected; CapacityQ = capacityQ;
        }
    }

    internal static class ResidentialCapacityObserver
    {
        private sealed class Reading
        {
            internal readonly ushort Id;
            internal uint Frame;
            internal ResidentialLotIdentity Identity;
            internal ResidentialCapacityObservationPhase Phase;
            internal ResidentialNominalCategory Category;
            internal int NativeLevel = -1, Expected = -1, Observed, Inspected;
            internal Reading(ushort id) { Id = id; }
            internal ResidentialCapacityObservation Result(ResidentialCapacityObservationReason reason, string detail)
            {
                bool known = reason == ResidentialCapacityObservationReason.None;
                return new ResidentialCapacityObservation(known ? ResidentialCapacityObservationStatus.Known : ResidentialCapacityObservationStatus.Unknown,
                    reason, detail, Phase, Id, Frame, Identity, Category, NativeLevel, Expected, Observed, Inspected,
                    known ? ResidentialNominalCapacity.ComputeNominalQ(Category, Observed) : 0);
            }
        }

        internal static ResidentialCapacityObservation Observe(ushort buildingId, ref Building data)
        {
            Reading reading = new Reading(buildingId);
            // The only native access before the thread gate is the manager/thread
            // metadata needed to establish that gate. Do not inspect data/buffers here.
            if (!Singleton<SimulationManager>.exists ||
                !ReferenceEquals(Thread.CurrentThread, Singleton<SimulationManager>.instance.m_simulationThread))
                return reading.Result(ResidentialCapacityObservationReason.NotSimulationThread, "No simulation-thread authority; building and citizen buffers were not read.");
            try
            {
                if (BuildConfig.applicationVersion != "1.21.1-f9")
                    return reading.Result(ResidentialCapacityObservationReason.UnsupportedGameVersion, "Verified native observation requires CS1 1.21.1-f9.");
                SimulationManager simulation = Singleton<SimulationManager>.instance;
                reading.Frame = simulation.m_currentFrameIndex;
                if (!Singleton<BuildingManager>.exists || !Singleton<CitizenManager>.exists)
                    return reading.Result(ResidentialCapacityObservationReason.ManagersUnavailable, "Building or citizen manager unavailable.");
                BuildingManager manager = Singleton<BuildingManager>.instance;
                CitizenManager citizens = Singleton<CitizenManager>.instance;
                Building[] buildings = manager.m_buildings.m_buffer;
                CitizenUnit[] units = citizens.m_units.m_buffer;
                Citizen[] people = citizens.m_citizens.m_buffer;
                if (buildings == null || units == null || people == null)
                    return reading.Result(ResidentialCapacityObservationReason.ManagersUnavailable, "Native buffers unavailable.");
                if (buildingId == 0 || buildingId >= buildings.Length)
                    return reading.Result(ResidentialCapacityObservationReason.InvalidBuildingId, "Building ID is outside the live buffer.");
                Building before = buildings[buildingId];
                if ((before.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created)
                    return reading.Result(ResidentialCapacityObservationReason.NotCreated, "Building is not a live Created identity.");
                if (!SameBuilding(data, before))
                    return reading.Result(ResidentialCapacityObservationReason.BuildingSnapshotMismatch, "Supplied building observation does not match the live identity/phase/chain root.");
                const Building.Flags unavailable = Building.Flags.Abandoned | Building.Flags.Collapsed | Building.Flags.Demolishing;
                if ((before.m_flags & unavailable) != 0)
                    reading.Phase = ResidentialCapacityObservationPhase.Unavailable;
                else if ((before.m_flags & (Building.Flags.Upgrading | Building.Flags.Downgrading)) != 0)
                    reading.Phase = ResidentialCapacityObservationPhase.Upgrading;
                else reading.Phase = (before.m_flags & Building.Flags.Completed) == 0 ? ResidentialCapacityObservationPhase.Constructing : ResidentialCapacityObservationPhase.Completed;
                BuildingInfo info = before.Info;
                if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Residential ||
                    info.m_buildingAI == null || info.m_buildingAI.GetType() != typeof(ResidentialBuildingAI))
                    return reading.Result(ResidentialCapacityObservationReason.UnsupportedAI, "Only the exact native ResidentialBuildingAI on a residential prefab is supported.");
                ResidentialBuildingAI ai = (ResidentialBuildingAI)info.m_buildingAI;
                if (!CoherentPrefabBinding(ai, info))
                    return reading.Result(ResidentialCapacityObservationReason.AIPrefabMismatch, "Native AI and prefab do not point to each other.");
                ResidentialLotIdentity identity;
                if (!ResidentialClassBridge.TryIdentity(buildingId, out identity))
                    return reading.Result(ResidentialCapacityObservationReason.IdentityUnavailable, "Class-bridge full identity could not be validated.");
                reading.Identity = identity;
                reading.Category = info.m_class.isResidentialLowGeneric ? ResidentialNominalCategory.LowGeneric :
                    info.m_class.isResidentialLowEco ? ResidentialNominalCategory.LowEco : ResidentialNominalCategory.Other;
                reading.NativeLevel = before.m_level;
                if (reading.Phase == ResidentialCapacityObservationPhase.Unavailable)
                    return reading.Result(ResidentialCapacityObservationReason.BuildingUnavailable, "Retained ledger capacity, rather than a fresh native estimate, must govern unavailable stock.");
                if (reading.Phase == ResidentialCapacityObservationPhase.Upgrading)
                    return reading.Result(ResidentialCapacityObservationReason.UpgradeInProgress, "Native upgrade/downgrade may be between prefab and unit updates.");
                ResidentialHomeCountBounds bounds;
                try { bounds = ResidentialNominalCapacity.GetHomeCountBounds(reading.Category, reading.NativeLevel, before.Width, before.Length); }
                catch (ArgumentException error) { return reading.Result(ResidentialCapacityObservationReason.InvalidCategoryOrGeometry, error.GetType().Name); }

                // Verified public native API. Randomizer is passed by value and seeded
                // only from this real building ID, exactly as native Create/Loaded do.
                int expected = ai.CalculateHomeCount((ItemClass.Level)before.m_level,
                    new Randomizer((uint)buildingId), before.Width, before.Length);
                reading.Expected = expected;
                if (expected < bounds.MinimumHomes || expected > bounds.MaximumHomes)
                    return reading.Result(ResidentialCapacityObservationReason.NativeHomeCountOutsideProfile, "Native calculation is outside its supported rounding envelope.");
                if (expected >= units.Length)
                    return reading.Result(ResidentialCapacityObservationReason.HomeCountExceedsUnitBuffer, "Native HOME count cannot fit in the available unit buffer excluding index0.");
                ResidentialCapacityObservationReason reason = InspectChain(units, people, buildingId, before.m_citizenUnits, expected,
                    out reading.Observed, out reading.Inspected);
                if (reason != ResidentialCapacityObservationReason.None)
                    return reading.Result(reason, "HOME chain is not coherent; no capacity estimate was emitted.");

                ResidentialLotIdentity afterIdentity;
                if (!ReferenceEquals(simulation, Singleton<SimulationManager>.instance) ||
                    !ReferenceEquals(manager, Singleton<BuildingManager>.instance) || !ReferenceEquals(citizens, Singleton<CitizenManager>.instance) ||
                    !ReferenceEquals(buildings, manager.m_buildings.m_buffer) || !ReferenceEquals(units, citizens.m_units.m_buffer) || !ReferenceEquals(people, citizens.m_citizens.m_buffer) ||
                    simulation.m_currentFrameIndex != reading.Frame || !ReferenceEquals(Thread.CurrentThread, simulation.m_simulationThread) ||
                    !SameBuilding(before, buildings[buildingId]) || !SameBuilding(data, buildings[buildingId]) ||
                    !ResidentialClassBridge.TryIdentity(buildingId, out afterIdentity) || !SameIdentity(identity, afterIdentity))
                    return reading.Result(ResidentialCapacityObservationReason.ObservationChanged, "Managers, identity, phase or unit root changed during observation.");
                if (!CoherentPrefabBinding(ai, info))
                    return reading.Result(ResidentialCapacityObservationReason.AIPrefabMismatch, "Native AI/prefab binding changed during observation.");
                return reading.Result(ResidentialCapacityObservationReason.None, "Supported native HOME count and local chain agree; global patch compatibility remains uncertified.");
            }
            catch (Exception error)
            { return reading.Result(ResidentialCapacityObservationReason.ReadFailed, error.GetType().Name); }
        }

        // Kept as a bounded value-only boundary so verification can use local arrays
        // of the real native structs without constructing a city or native managers.
        private static ResidentialCapacityObservationReason InspectChain(CitizenUnit[] units, Citizen[] people, ushort building,
            uint root, int expected, out int homes, out int inspected)
        {
            homes = 0; inspected = 0;
            if (root == 0) return ResidentialCapacityObservationReason.MissingHomeChain;
            HashSet<uint> visited = new HashSet<uint>(), members = new HashSet<uint>();
            uint next = root;
            const CitizenUnit.Flags categoryFlags = CitizenUnit.Flags.Home | CitizenUnit.Flags.Work | CitizenUnit.Flags.Visit |
                CitizenUnit.Flags.Vehicle | CitizenUnit.Flags.Student | CitizenUnit.Flags.Hotel;
            while (next != 0)
            {
                if (next >= units.Length) return ResidentialCapacityObservationReason.UnitIndexOutsideBuffer;
                if (!visited.Add(next)) return ResidentialCapacityObservationReason.UnitCycle;
                // Every accepted unit must be a HOME, so one extra unit suffices
                // to detect mismatch; traversal cannot grow to the global buffer size.
                if (inspected > expected) return ResidentialCapacityObservationReason.TooManyHomeUnits;
                CitizenUnit unit = units[next]; inspected++;
                if ((unit.m_flags & CitizenUnit.Flags.Created) == 0) return ResidentialCapacityObservationReason.UnitNotCreated;
                if (unit.m_building != building) return ResidentialCapacityObservationReason.UnitOwnerMismatch;
                if ((unit.m_flags & categoryFlags) != CitizenUnit.Flags.Home) return ResidentialCapacityObservationReason.UnexpectedUnitType;
                homes++;
                if (homes > expected) return ResidentialCapacityObservationReason.TooManyHomeUnits;
                for (int slot = 0; slot < 5; slot++)
                {
                    uint person = unit.GetCitizen(slot);
                    if (person == 0) continue;
                    if (person >= people.Length) return ResidentialCapacityObservationReason.CitizenIndexOutsideBuffer;
                    if (!members.Add(person)) return ResidentialCapacityObservationReason.DuplicateCitizen;
                    Citizen citizen = people[person];
                    if ((citizen.m_flags & Citizen.Flags.Created) == 0) return ResidentialCapacityObservationReason.CitizenNotCreated;
                    if (citizen.m_homeBuilding != building) return ResidentialCapacityObservationReason.CitizenHomeMismatch;
                }
                next = unit.m_nextUnit;
            }
            return homes == expected ? ResidentialCapacityObservationReason.None : ResidentialCapacityObservationReason.HomeCountMismatch;
        }
        private static bool SameBuilding(Building left, Building right)
        {
            return left.m_flags == right.m_flags && left.m_buildIndex == right.m_buildIndex && left.m_level == right.m_level &&
                left.m_citizenUnits == right.m_citizenUnits && Bits(left.m_position.x) == Bits(right.m_position.x) &&
                Bits(left.m_position.y) == Bits(right.m_position.y) && Bits(left.m_position.z) == Bits(right.m_position.z) &&
                left.Width == right.Width && left.Length == right.Length && ReferenceEquals(left.Info, right.Info);
        }
        private static bool CoherentPrefabBinding(ResidentialBuildingAI ai, BuildingInfo info)
        {
            // Native CalculateHomeCount/GetHomeReason read ai.m_info, while the
            // observation classifies info.m_class. Both directions must agree.
            return !ReferenceEquals(ai, null) && !ReferenceEquals(info, null) &&
                ReferenceEquals(ai.m_info, info) && ReferenceEquals(info.m_buildingAI, ai);
        }
        private static bool SameIdentity(ResidentialLotIdentity left, ResidentialLotIdentity right)
        {
            return right != null && left.BuildingId == right.BuildingId && left.NativeBuildIndex == right.NativeBuildIndex &&
                left.OriginXBits == right.OriginXBits && left.OriginZBits == right.OriginZBits && left.PrefabName == right.PrefabName &&
                left.Service == right.Service && left.SubService == right.SubService && left.Width == right.Width && left.Length == right.Length;
        }
        private static int Bits(float value) { return BitConverter.ToInt32(BitConverter.GetBytes(value), 0); }
    }
}
