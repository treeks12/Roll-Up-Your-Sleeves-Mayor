using System;
using System.Collections;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using SC4Evolution.Simulation;

namespace SC4Evolution.Mod.Economy
{
    /// <summary>One simulation-thread observation. People, households and jobs keep distinct units.</summary>
    public sealed class CityCensus
    {
        private readonly int[] _residents, _workers, _capacity, _filled;
        private readonly int[] _lotResidents, _lotWorkers, _unboundResidents, _unboundWorkers;
        public uint Frame { get; private set; }
        public DateTime SimulationDate { get; private set; }
        public int HomeHouseholds { get; private set; }
        public int OccupiedHomeHouseholds { get; private set; }
        public int UnmappedJobCapacity { get; private set; }
        public int UnmappedFilledJobs { get; private set; }
        public ResidentialStockSnapshot ResidentialStock { get; private set; }
        public int[] ResidentsByBootstrapWealth { get { return (int[])_residents.Clone(); } }
        // Wealth-major, then education 0..3; actual non-student young/adult residents.
        public int[] WorkforceByBootstrapWealthEducation { get { return (int[])_workers.Clone(); } }
        public int[] ResidentsByLotClass { get { return (int[])_lotResidents.Clone(); } }
        public int[] WorkforceByLotClassEducation { get { return (int[])_lotWorkers.Clone(); } }
        public int[] UnboundResidentsByNativeWealth { get { return (int[])_unboundResidents.Clone(); } }
        public int[] UnboundWorkforceByNativeWealthEducation { get { return (int[])_unboundWorkers.Clone(); } }
        // CS$, CS$$, CS$$$, CO$$, CO$$$, IR, ID, IM, IHT: provisional native-category adapters.
        public int[] JobCapacityBySegment { get { return (int[])_capacity.Clone(); } }
        public int[] FilledJobsBySegment { get { return (int[])_filled.Clone(); } }

        private CityCensus(int[] residents, int[] workers, int[] capacity, int[] filled,
            int homes, int occupied, int unmappedCapacity, int unmappedFilled,
            int[] lotResidents, int[] lotWorkers, int[] unboundResidents, int[] unboundWorkers, ResidentialStockSnapshot stock)
        {
            _residents = residents; _workers = workers; _capacity = capacity; _filled = filled;
            _lotResidents = lotResidents; _lotWorkers = lotWorkers; _unboundResidents = unboundResidents; _unboundWorkers = unboundWorkers;
            HomeHouseholds = homes; OccupiedHomeHouseholds = occupied;
            UnmappedJobCapacity = unmappedCapacity; UnmappedFilledJobs = unmappedFilled;
            Frame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
            SimulationDate = Singleton<SimulationManager>.instance.m_currentGameTime;
            if (stock == null || stock.Frame != Frame) throw new InvalidOperationException("Residential stock must describe the same simulation frame.");
            ResidentialStock = stock;
        }

        internal static CityCensus Capture(ResidentialClassBridge classes = null)
        {
            if (!Singleton<CitizenManager>.exists || !Singleton<BuildingManager>.exists ||
                !Singleton<DistrictManager>.exists || !Singleton<SimulationManager>.exists)
                throw new InvalidOperationException("City managers are not ready.");
            CitizenManager citizens = Singleton<CitizenManager>.instance;
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            Citizen[] people = citizens.m_citizens.m_buffer;
            CitizenUnit[] units = citizens.m_units.m_buffer;
            int[] lotClasses = classes == null ? null : classes.CensusClasses();
            int[] residents = new int[3], workers = new int[12], capacity = new int[9], filled = new int[9];
            int[] lotResidents = new int[3], lotWorkers = new int[12], unboundResidents = new int[3], unboundWorkers = new int[12];
            int[] segments = new int[buildings.Length];
            ResidentialStockPhase[] stockPhases = new ResidentialStockPhase[buildings.Length];
            StockCounts[] stockCounts = new StockCounts[buildings.Length];
            BitArray referencedPeople = new BitArray(people.Length);
            for (int i = 0; i < segments.Length; i++) segments[i] = -1;
            int homes = 0, occupied = 0, unmappedCapacity = 0, unmappedFilled = 0;
            for (int id = 1; id < buildings.Length; id++)
            {
                Building building = buildings[id];
                stockPhases[id] = StockPhase(building);
                if (!IsUsable(building)) continue;
                BuildingInfo info = building.Info;
                PrivateBuildingAI ai = info == null ? null : info.m_buildingAI as PrivateBuildingAI;
                if (ai == null) continue;
                int segment = SegmentFor(info.m_class, (ItemClass.Level)building.m_level);
                segments[id] = segment;
                int a, b, c, d;
                ai.CalculateWorkplaceCount((ItemClass.Level)building.m_level, new Randomizer((uint)id),
                    building.Width, building.Length, out a, out b, out c, out d);
                // Native AdjustWorkplaceCount marks Industry 4.0 as a policy-display effect.
                // Restore that display field immediately so observing cannot add policy effects.
                DistrictManager districts = Singleton<DistrictManager>.instance;
                byte district = districts.GetDistrict(building.m_position);
                DistrictPolicies.CityPlanning effects = districts.m_districts.m_buffer[district].m_cityPlanningPoliciesEffect;
                try { ai.AdjustWorkplaceCount((ushort)id, ref building, ref a, ref b, ref c, ref d); }
                finally { districts.m_districts.m_buffer[district].m_cityPlanningPoliciesEffect = effects; }
                int jobs = checked(a + b + c + d);
                if (jobs < 0) throw new InvalidOperationException("Negative native workplace capacity.");
                if (segment >= 0) capacity[segment] = checked(capacity[segment] + jobs);
                else unmappedCapacity = checked(unmappedCapacity + jobs);
            }
            // A HOME unit is one household slot, regardless of its five citizen fields.
            for (int id = 1; id < units.Length; id++)
            {
                CitizenUnit unit = units[id];
                if ((unit.m_flags & (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home)) !=
                    (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home) || unit.m_building == 0 ||
                    unit.m_building >= buildings.Length) continue;
                if (stockPhases[unit.m_building] != 0)
                    ObserveStockHome(unit, people, referencedPeople, ref stockCounts[unit.m_building]);
                if (!IsUsable(buildings[unit.m_building])) continue;
                homes++;
                for (int member = 0; member < 5; member++)
                {
                    uint person = unit.GetCitizen(member);
                    if (person != 0 && person < people.Length && IsResident(people[person]) &&
                        people[person].m_homeBuilding == unit.m_building)
                    { occupied++; break; }
                }
            }
            for (int id = 1; id < people.Length; id++)
            {
                Citizen person = people[id];
                int home = person.m_homeBuilding;
                if (IsLiveResidentIncludingIncoming(person) && home != 0 && home < stockPhases.Length && stockPhases[home] != 0)
                {
                    if ((person.m_flags & Citizen.Flags.MovingIn) != 0) stockCounts[home].IncomingPeople++;
                    else stockCounts[home].SettledPeople++;
                    if (!referencedPeople[id]) stockCounts[home].UnlinkedPeople++;
                }
                if (!IsResident(person)) continue;
                int wealth = Math.Max(0, Math.Min(2, (int)person.WealthLevel));
                int education = Math.Max(0, Math.Min(3, (int)person.EducationLevel));
                // Count created live residents even if temporarily homeless; exclude tourists/dummies.
                residents[wealth]++;
                int lotClass = lotClasses == null || person.m_homeBuilding >= lotClasses.Length ? 0 : lotClasses[person.m_homeBuilding];
                bool bound = lotClass != 0;
                int controlled = 0;
                if (bound) { controlled = lotClass - 1; lotResidents[controlled]++; }
                else unboundResidents[wealth]++;
                Citizen.AgeGroup age = Citizen.GetAgeGroup(person.Age);
                if ((person.m_flags & Citizen.Flags.Student) == 0 &&
                    (age == Citizen.AgeGroup.Young || age == Citizen.AgeGroup.Adult))
                {
                    workers[wealth * 4 + education]++;
                    if (bound) lotWorkers[controlled * 4 + education]++; else unboundWorkers[wealth * 4 + education]++;
                }
                if ((person.m_flags & Citizen.Flags.Student) != 0 || person.m_workBuilding == 0) continue;
                int work = person.m_workBuilding;
                if (work >= buildings.Length || !IsUsable(buildings[work])) continue;
                int segment = segments[work];
                if (segment >= 0) filled[segment]++;
                else unmappedFilled++;
            }
            if (classes != null) classes.SetUnboundCount(unboundResidents[0] + unboundResidents[1] + unboundResidents[2]);
            List<ResidentialStockLot> stockLots = new List<ResidentialStockLot>();
            for (int id = 1; id < stockPhases.Length; id++)
            {
                if (stockPhases[id] == 0) continue;
                StockCounts count = stockCounts[id];
                stockLots.Add(new ResidentialStockLot((ushort)id, buildings[id].m_buildIndex,
                    lotClasses == null ? 0 : lotClasses[id], stockPhases[id], count.Homes, count.EmptyHomes, count.LiveHomes,
                    count.OtherHomes, count.SettledPeople, count.IncomingPeople, count.UnlinkedPeople, count.InvalidReferences));
            }
            ResidentialStockSnapshot stock = new ResidentialStockSnapshot(Singleton<SimulationManager>.instance.m_currentFrameIndex, stockLots);
            return new CityCensus(residents, workers, capacity, filled, homes, occupied, unmappedCapacity, unmappedFilled,
                lotResidents, lotWorkers, unboundResidents, unboundWorkers, stock);
        }

        private struct StockCounts
        {
            internal int Homes, EmptyHomes, LiveHomes, OtherHomes;
            internal int SettledPeople, IncomingPeople, UnlinkedPeople, InvalidReferences;
        }

        private static ResidentialStockPhase StockPhase(Building building)
        {
            if ((building.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created) return 0;
            BuildingInfo info = building.Info;
            if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Residential ||
                !(info.m_buildingAI is ResidentialBuildingAI)) return 0;
            const Building.Flags unavailable = Building.Flags.Abandoned | Building.Flags.Collapsed | Building.Flags.Demolishing;
            if ((building.m_flags & unavailable) != 0) return ResidentialStockPhase.Unavailable;
            if ((building.m_flags & Building.Flags.Upgrading) != 0) return ResidentialStockPhase.Upgrading;
            return (building.m_flags & Building.Flags.Completed) != 0 ? ResidentialStockPhase.Completed : ResidentialStockPhase.Constructing;
        }

        private static void ObserveStockHome(CitizenUnit unit, Citizen[] people, BitArray referenced, ref StockCounts counts)
        {
            bool nonempty = false, live = false;
            counts.Homes++;
            for (int member = 0; member < 5; member++)
            {
                uint id = unit.GetCitizen(member);
                if (id == 0) continue;
                nonempty = true;
                if (id >= people.Length || (people[id].m_flags & Citizen.Flags.Created) == 0 || people[id].m_homeBuilding != unit.m_building)
                { counts.InvalidReferences++; continue; }
                if (referenced[(int)id]) counts.InvalidReferences++;
                referenced[(int)id] = true;
                if (!IsLiveResidentIncludingIncoming(people[id])) continue;
                live = true;
            }
            if (!nonempty) counts.EmptyHomes++;
            else if (live) counts.LiveHomes++;
            else counts.OtherHomes++;
        }

        private static bool IsLiveResidentIncludingIncoming(Citizen person)
        {
            const Citizen.Flags excluded = Citizen.Flags.Dead | Citizen.Flags.Tourist | Citizen.Flags.DummyTraffic;
            return (person.m_flags & (Citizen.Flags.Created | excluded)) == Citizen.Flags.Created;
        }

        private static bool IsResident(Citizen person)
        {
            const Citizen.Flags excluded = Citizen.Flags.Dead | Citizen.Flags.Tourist | Citizen.Flags.DummyTraffic | Citizen.Flags.MovingIn;
            return (person.m_flags & (Citizen.Flags.Created | excluded)) == Citizen.Flags.Created;
        }

        private static bool IsUsable(Building building)
        {
            const Building.Flags excluded = Building.Flags.Deleted | Building.Flags.Abandoned | Building.Flags.Collapsed | Building.Flags.Demolishing;
            return (building.m_flags & (Building.Flags.Created | excluded)) == Building.Flags.Created;
        }

        internal static int SegmentFor(ItemClass category, ItemClass.Level level)
        {
            if (category.m_service == ItemClass.Service.Commercial)
                return Math.Max(0, Math.Min(2, (int)level));
            if (category.m_service == ItemClass.Service.Office) return level < ItemClass.Level.Level3 ? 3 : 4;
            if (category.m_service != ItemClass.Service.Industrial) return -1;
            if (category.m_subService == ItemClass.SubService.IndustrialFarming) return 5;
            if (category.m_subService == ItemClass.SubService.IndustrialGeneric)
                return 6 + Math.Max(0, Math.Min(2, (int)level));
            return -1;
        }
    }
}
