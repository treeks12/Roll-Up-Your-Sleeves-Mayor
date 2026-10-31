using System;
using System.Threading;
using ColossalFramework;
using ICities;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Simulation.Neighborhoods;
using UnityEngine;

namespace SC4Evolution.Mod.Economy
{
    /// <summary>
    /// Native Sprint 02 application proof.  CS1 discovers ILevelUpExtension
    /// implementations through LevelUpWrapper; no manual registration in the
    /// LoadingExtension is needed.  The extension remains inert unless the
    /// published gameplay snapshot explicitly contains Neighborhoods.
    /// </summary>
    public sealed class NativeNeighborhoodLevelUpExtension : LevelUpExtensionBase
    {
        private static readonly NeighborhoodAttractiveness Attractiveness = NeighborhoodAttractiveness.Default;
        private static readonly ImmaterialResourceManager.Resource[] ServiceResources =
        {
            ImmaterialResourceManager.Resource.HealthCare,
            ImmaterialResourceManager.Resource.FireDepartment,
            ImmaterialResourceManager.Resource.PoliceDepartment,
            ImmaterialResourceManager.Resource.EducationElementary,
            ImmaterialResourceManager.Resource.EducationHighSchool,
            ImmaterialResourceManager.Resource.EducationUniversity,
            ImmaterialResourceManager.Resource.DeathCare,
            ImmaterialResourceManager.Resource.PublicTransport,
            ImmaterialResourceManager.Resource.Entertainment
        };

        private static int _observations;
        private static int _applications;
        private static int _lastBuildingId = -1;
        private static int _lastLandValue;
        private static int _lastPollution;
        private static int _lastServices;
        private static int _lastAttractiveness;
        private static int _samplingErrorLogged;
        private static int _observationLogged;

        /// <summary>Number of callbacks with valid native lot evidence.</summary>
        public static int NativeObservations { get { return Read(ref _observations); } }

        /// <summary>Number of callbacks that changed target/progress.</summary>
        public static int NativeApplications { get { return Read(ref _applications); } }

        public static int LastBuildingId { get { return Read(ref _lastBuildingId); } }
        public static int LastLandValue { get { return Read(ref _lastLandValue); } }
        public static int LastPollution { get { return Read(ref _lastPollution); } }
        public static int LastServices { get { return Read(ref _lastServices); } }
        public static int LastAttractiveness { get { return Read(ref _lastAttractiveness); } }

        public override void OnCreated(ILevelUp levelUp)
        {
            base.OnCreated(levelUp);
            ResetCounters();
        }

        public override void OnReleased()
        {
            ResetCounters();
            base.OnReleased();
        }

        public override ResidentialLevelUp OnCalculateResidentialLevelUp(
            ResidentialLevelUp levelUp, int averageEducation, int landValue, ushort buildingID,
            Service service, SubService subService, Level currentLevel)
        {
            NeighborhoodNativeGrowthOutput output;
            NativeMetrics metrics;
            if (!TryApply(levelUp.targetLevel, levelUp.educationProgress, levelUp.landValueProgress,
                currentLevel, buildingID, out output, out metrics))
                return levelUp;

            levelUp.targetLevel = (Level)output.TargetLevel;
            levelUp.educationProgress = output.SecondaryProgress;
            levelUp.landValueProgress = output.PrimaryProgress;
            return levelUp;
        }

        public override CommercialLevelUp OnCalculateCommercialLevelUp(
            CommercialLevelUp levelUp, int averageWealth, int landValue, ushort buildingID,
            Service service, SubService subService, Level currentLevel)
        {
            NeighborhoodNativeGrowthOutput output;
            NativeMetrics metrics;
            if (!TryApply(levelUp.targetLevel, levelUp.wealthProgress, levelUp.landValueProgress,
                currentLevel, buildingID, out output, out metrics))
                return levelUp;

            levelUp.targetLevel = (Level)output.TargetLevel;
            levelUp.wealthProgress = output.SecondaryProgress;
            levelUp.landValueProgress = output.PrimaryProgress;
            return levelUp;
        }

        private static bool TryApply(Level targetLevel, int educationProgress, int primaryProgress,
            Level currentLevel, ushort buildingID, out NeighborhoodNativeGrowthOutput output,
            out NativeMetrics metrics)
        {
            output = default(NeighborhoodNativeGrowthOutput);
            metrics = default(NativeMetrics);
            if (!NeighborhoodGameplayEnabled()) return false;
            if (!TryReadMetrics(buildingID, out metrics)) return false;

            NeighborhoodNativeGrowthInput input = new NeighborhoodNativeGrowthInput();
            input.CurrentLevel = (int)currentLevel;
            input.TargetLevel = (int)targetLevel;
            input.SecondaryProgress = educationProgress;
            input.PrimaryProgress = primaryProgress;
            input.Attractiveness = metrics.Attractiveness;
            try
            {
                output = NeighborhoodNativeLevelUpPolicy.ApplyResidential(input);
            }
            catch (Exception error)
            {
                if (Interlocked.Exchange(ref _samplingErrorLogged, 1) == 0)
                    ModRuntime.Log("Native neighborhood level-up policy skipped: " + error.Message);
                return false;
            }

            Interlocked.Increment(ref _observations);
            Interlocked.Exchange(ref _lastBuildingId, buildingID);
            Interlocked.Exchange(ref _lastLandValue, metrics.LandValue);
            Interlocked.Exchange(ref _lastPollution, metrics.Pollution);
            Interlocked.Exchange(ref _lastServices, metrics.Services);
            Interlocked.Exchange(ref _lastAttractiveness, Mathf.RoundToInt(metrics.Attractiveness));
            if (Interlocked.Exchange(ref _observationLogged, 1) == 0)
                ModRuntime.Log("Native neighborhood level-up active: lot=" + buildingID +
                    ", landValue=" + metrics.LandValue + ", services=" + metrics.Services +
                    ", pollution=" + metrics.Pollution + ", attractiveness=" +
                    Mathf.RoundToInt(metrics.Attractiveness));
            if (output.TargetLevel != input.TargetLevel || output.SecondaryProgress != input.SecondaryProgress ||
                output.PrimaryProgress != input.PrimaryProgress)
                Interlocked.Increment(ref _applications);
            return true;
        }

        private static bool NeighborhoodGameplayEnabled()
        {
            EconomicBridge bridge = ModRuntime.CurrentEconomicBridge;
            GameplayIntegrationSnapshot snapshot = bridge == null ? null : bridge.PublishedGameplay;
            return snapshot != null && (snapshot.EnabledSprints & GameplaySprint.Neighborhoods) != GameplaySprint.None;
        }

        private static bool TryReadMetrics(ushort buildingID, out NativeMetrics metrics)
        {
            metrics = default(NativeMetrics);
            try
            {
                if (!Singleton<BuildingManager>.exists || !Singleton<ImmaterialResourceManager>.exists ||
                    !Singleton<NaturalResourceManager>.exists)
                    return false;
                BuildingManager manager = Singleton<BuildingManager>.instance;
                if (manager == null || manager.m_buildings == null || buildingID >= manager.m_buildings.m_buffer.Length)
                    return false;
                Building data = manager.m_buildings.m_buffer[buildingID];
                if ((data.m_flags & Building.Flags.Created) == 0 ||
                    (data.m_flags & (Building.Flags.Deleted | Building.Flags.Abandoned | Building.Flags.Collapsed)) != 0)
                    return false;

                ImmaterialResourceManager immaterial = Singleton<ImmaterialResourceManager>.instance;
                int localLandValue;
                immaterial.CheckLocalResource(ImmaterialResourceManager.Resource.LandValue, data.m_position, out localLandValue);
                int serviceTotal = 0;
                for (int i = 0; i < ServiceResources.Length; i++)
                {
                    int local;
                    immaterial.CheckLocalResource(ServiceResources[i], data.m_position, out local);
                    serviceTotal += NormalizeService(local);
                }

                byte groundPollution;
                Singleton<NaturalResourceManager>.instance.CheckPollution(data.m_position, out groundPollution);
                metrics.LandValue = ClampPercent(localLandValue);
                metrics.Services = serviceTotal / ServiceResources.Length;
                metrics.Pollution = 100 - (groundPollution * 100 / 255);

                NeighborhoodFactors factors = NeighborhoodFactors.Neutral();
                // Employment, taxes and congestion have no stable native per-lot
                // signal yet. Neutral means unknown and is intentionally visible
                // in the application boundary rather than fabricated from an
                // aggregate city value.
                factors.Services = metrics.Services;
                factors.Pollution = metrics.Pollution;
                factors.LandValue = metrics.LandValue;
                metrics.Attractiveness = Attractiveness.Evaluate(factors);
                return true;
            }
            catch (Exception error)
            {
                if (Interlocked.Exchange(ref _samplingErrorLogged, 1) == 0)
                    ModRuntime.Log("Native neighborhood level-up sample skipped: " + error.Message);
                return false;
            }
        }

        private static int NormalizeService(int value)
        {
            if (value <= 0) return 0;
            if (value >= 500) return 100;
            return value * 100 / 500;
        }

        private static int ClampPercent(int value)
        {
            return value < 0 ? 0 : (value > 100 ? 100 : value);
        }

        private static void ResetCounters()
        {
            Interlocked.Exchange(ref _observations, 0);
            Interlocked.Exchange(ref _applications, 0);
            Interlocked.Exchange(ref _lastBuildingId, -1);
            Interlocked.Exchange(ref _lastLandValue, 0);
            Interlocked.Exchange(ref _lastPollution, 0);
            Interlocked.Exchange(ref _lastServices, 0);
            Interlocked.Exchange(ref _lastAttractiveness, 0);
            Interlocked.Exchange(ref _samplingErrorLogged, 0);
            Interlocked.Exchange(ref _observationLogged, 0);
        }

        private static int Read(ref int value)
        {
            return Interlocked.CompareExchange(ref value, 0, 0);
        }

        private struct NativeMetrics
        {
            public int LandValue;
            public int Pollution;
            public int Services;
            public float Attractiveness;
        }
    }
}
