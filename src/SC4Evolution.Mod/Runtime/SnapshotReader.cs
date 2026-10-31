using System;
using System.Reflection;
using ColossalFramework;
using SC4Evolution.Core;

namespace SC4Evolution.Mod.Runtime
{
    internal static class SnapshotReader
    {
        // Read-only diagnostic. No replacement or mutation of the original multiplier here.
        private static readonly FieldInfo TaxMultiplier = typeof(EconomyManager).GetField("m_taxMultiplier", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static CitySnapshot Capture()
        {
            if (!Singleton<DistrictManager>.exists || !Singleton<EconomyManager>.exists || !Singleton<SimulationManager>.exists)
                return new CitySnapshot { DataValid = false, TrafficFlowPercent = -1, NativeTaxMultiplier = double.NaN };

            District city = Singleton<DistrictManager>.instance.m_districts.m_buffer[0];
            EconomyManager economy = Singleton<EconomyManager>.instance;
            SimulationManager simulation = Singleton<SimulationManager>.instance;
            CitySnapshot snapshot = new CitySnapshot
            {
                DataValid = true,
                CityName = simulation.m_metaData == null ? "Nova cidade" : (simulation.m_metaData.m_CityName ?? "Nova cidade"),
                SimulationDate = simulation.m_currentGameTime,
                Population = CheckedCount(city.m_populationData.m_finalCount),
                Happiness = city.m_finalHappiness,
                Health = city.m_residentialData.m_finalHealth,
                CrimePercent = city.m_finalCrimeRate,
                PollutionPercent = city.GetGroundPollution(),
                UnemploymentPercent = city.GetUnemployment(),
                Cash = economy.LastCashAmount / 100L,
                WeeklyBalance = economy.LastCashDelta / 100L,
                ElementaryNeed = city.GetEducation1Need(),
                ElementaryCapacity = city.GetEducation1Capacity(),
                ElementaryEducationAvailable = IsEducationUnlocked(ItemClass.Level.Level1),
                HighSchoolNeed = city.GetEducation2Need(),
                HighSchoolCapacity = city.GetEducation2Capacity(),
                HighSchoolEducationAvailable = IsEducationUnlocked(ItemClass.Level.Level2),
                UniversityNeed = city.GetEducation3Need(),
                UniversityCapacity = city.GetEducation3Capacity(),
                UniversityEducationAvailable = IsEducationUnlocked(ItemClass.Level.Level3),
                ElectricityUse = city.GetElectricityConsumption(),
                ElectricityCapacity = city.GetElectricityCapacity(),
                WaterUse = city.GetWaterConsumption(),
                WaterCapacity = city.GetWaterCapacity(),
                SewageUse = city.GetSewageAccumulation(),
                SewageCapacity = city.GetSewageCapacity(),
                GarbageStored = city.GetGarbageAmount(),
                GarbageCapacity = city.GetGarbageCapacity(),
                SickCitizens = city.GetSickCount(),
                HealthCapacity = city.GetHealCapacity(),
                HealthServiceAvailable = IsServiceUnlocked(ItemClass.Service.HealthCare),
                ResidentialAbandoned = city.m_residentialData.m_finalAbandonedCount,
                CommercialAbandoned = city.m_commercialData.m_finalAbandonedCount,
                IndustrialAbandoned = city.m_industrialData.m_finalAbandonedCount,
                OfficeAbandoned = city.m_officeData.m_finalAbandonedCount,
                ZonedBuildings = city.m_residentialData.m_finalBuildingCount + city.m_commercialData.m_finalBuildingCount + city.m_industrialData.m_finalBuildingCount + city.m_officeData.m_finalBuildingCount,
                TrafficFlowPercent = Singleton<VehicleManager>.exists ? (int)Singleton<VehicleManager>.instance.m_lastTrafficFlow : -1,
                OwnedTiles = Singleton<GameAreaManager>.exists ? Singleton<GameAreaManager>.instance.m_areaCount : 0,
                PurchasableTileLimit = Singleton<GameAreaManager>.exists ? Singleton<GameAreaManager>.instance.MaxAreaCount : 0,
                NativeTaxMultiplier = TaxMultiplier == null ? double.NaN : Convert.ToInt32(TaxMultiplier.GetValue(economy)) / 10000.0
            };

            if (Singleton<ZoneManager>.exists)
            {
                ZoneManager zones = Singleton<ZoneManager>.instance;
                snapshot.ResidentialDemand = zones.m_residentialDemand;
                snapshot.CommercialDemand = zones.m_commercialDemand;
                snapshot.WorkplaceDemand = zones.m_workplaceDemand;
            }
            return snapshot;
        }

        private static bool IsEducationUnlocked(ItemClass.Level level)
        {
            for (uint index = 0; index < PrefabCollection<BuildingInfo>.LoadedCount(); index++)
            {
                BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded(index);
                if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Education ||
                    info.m_class.m_level != level)
                    continue;
                if (info.m_UnlockMilestone == null || ToolsModifierControl.IsUnlocked(info.m_UnlockMilestone))
                    return true;
            }
            return false;
        }

        private static bool IsServiceUnlocked(ItemClass.Service service)
        {
            for (uint index = 0; index < PrefabCollection<BuildingInfo>.LoadedCount(); index++)
            {
                BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded(index);
                if (info == null || info.m_class == null || info.m_class.m_service != service)
                    continue;
                if (info.m_UnlockMilestone == null || ToolsModifierControl.IsUnlocked(info.m_UnlockMilestone))
                    return true;
            }
            return false;
        }

        private static int CheckedCount(uint value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }
    }
}
