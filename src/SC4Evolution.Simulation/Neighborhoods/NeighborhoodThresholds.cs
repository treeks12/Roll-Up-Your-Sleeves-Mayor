using System;

namespace SC4Evolution.Simulation.Neighborhoods
{
    /// <summary>Every cycle threshold in one authored place (sprint 02). Frame counts mean
    /// model updates, not native frames; adapters choose the update cadence. Defaults are
    /// an authored hypothesis, injectable for tests or after evidence.</summary>
    public sealed class NeighborhoodThresholds
    {
        /// <summary>Attractiveness at or below this starts the vacancy grace counter on an
        /// occupied lot. A drop never vacates on the same update.</summary>
        public readonly float MaintenanceAttractiveness;
        /// <summary>Grace updates an occupied lot stays occupied while unattractive, before
        /// residents leave.</summary>
        public readonly int MaintenanceGraceUpdates;
        /// <summary>Attractiveness at or below this allows abandonment of a vacant lot.</summary>
        public readonly float AbandonmentAttractiveness;
        /// <summary>Vacancy updates after which an unattractive vacant lot is abandoned.</summary>
        public readonly int AbandonmentVacancyUpdates;
        /// <summary>Minimum attractiveness a vacant lot needs before reoccupation may start.</summary>
        public readonly float ReoccupationAttractiveness;
        /// <summary>Minimum attractiveness an abandoned lot needs before rehabilitation may
        /// start (at least the reoccupation bar, typically higher).</summary>
        public readonly float RehabilitationAttractiveness;
        /// <summary>Sustained-updates count of high attractiveness plus higher-class demand
        /// before resident replacement may happen on an occupied lot.</summary>
        public readonly int ReplacementSustainedUpdates;
        /// <summary>Attractiveness considered "high" for the replacement sustained condition.</summary>
        public readonly float ReplacementAttractiveness;

        public static NeighborhoodThresholds Default
        {
            get
            {
                return new NeighborhoodThresholds(40f, 3, 25f, 5, 55f, 65f, 4, 70f);
            }
        }

        public NeighborhoodThresholds(float maintenanceAttractiveness, int maintenanceGraceUpdates,
            float abandonmentAttractiveness, int abandonmentVacancyUpdates,
            float reoccupationAttractiveness, float rehabilitationAttractiveness,
            int replacementSustainedUpdates, float replacementAttractiveness)
        {
            ValidateRange(maintenanceAttractiveness, "maintenanceAttractiveness");
            ValidateRange(abandonmentAttractiveness, "abandonmentAttractiveness");
            ValidateRange(reoccupationAttractiveness, "reoccupationAttractiveness");
            ValidateRange(rehabilitationAttractiveness, "rehabilitationAttractiveness");
            ValidateRange(replacementAttractiveness, "replacementAttractiveness");
            if (maintenanceGraceUpdates < 1) throw new ArgumentOutOfRangeException("maintenanceGraceUpdates", "Grace must be at least one update.");
            if (abandonmentVacancyUpdates < 1) throw new ArgumentOutOfRangeException("abandonmentVacancyUpdates", "Abandonment needs at least one vacant update.");
            if (replacementSustainedUpdates < 1) throw new ArgumentOutOfRangeException("replacementSustainedUpdates", "Replacement needs at least one update.");
            if (abandonmentAttractiveness > maintenanceAttractiveness)
                throw new ArgumentException("Abandonment bar cannot exceed the maintenance bar.");
            MaintenanceAttractiveness = maintenanceAttractiveness;
            MaintenanceGraceUpdates = maintenanceGraceUpdates;
            AbandonmentAttractiveness = abandonmentAttractiveness;
            AbandonmentVacancyUpdates = abandonmentVacancyUpdates;
            ReoccupationAttractiveness = reoccupationAttractiveness;
            RehabilitationAttractiveness = rehabilitationAttractiveness;
            ReplacementSustainedUpdates = replacementSustainedUpdates;
            ReplacementAttractiveness = replacementAttractiveness;
        }

        private static void ValidateRange(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 100f)
                throw new ArgumentOutOfRangeException(name, "Thresholds live on 0..100.");
        }
    }
}
