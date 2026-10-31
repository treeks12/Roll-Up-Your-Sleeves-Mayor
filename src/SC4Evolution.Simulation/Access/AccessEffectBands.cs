using System;

namespace SC4Evolution.Simulation.Access
{
    public enum AccessEffectLevel : byte { None = 0, Mild = 1, Moderate = 2, Severe = 3 }

    /// <summary>Maps a 0..1 access/outflow score to gradual effect levels and the resulting
    /// productivity/demand multipliers (sprint 05). Effects apply to the correct side:
    /// employment access affects properties/business attractiveness of the residential
    /// origin; freight outflow affects the producing business. Multipliers are linear in the
    /// score — gradual by construction — and the bands are injected, single place, no magic
    /// numbers scattered. A score of exactly 0 (no observation) still yields a nonzero
    /// floor multiplier only when the caller configures one: absence of measurement is
    /// never silently treated as good or bad; the default floor is 1 (no effect) until
    /// native grounding decides otherwise.</summary>
    public sealed class AccessEffectBands
    {
        public readonly float ModerateBelow;
        public readonly float SevereBelow;
        public readonly float FloorMultiplier;
        public readonly float FullMultiplier;

        public static AccessEffectBands Default
        {
            get { return new AccessEffectBands(0.5f, 0.25f, 1f, 1.25f); }
        }

        public AccessEffectBands(float moderateBelow, float severeBelow, float floorMultiplier, float fullMultiplier)
        {
            Validate(moderateBelow, "moderateBelow");
            Validate(severeBelow, "severeBelow");
            if (severeBelow >= moderateBelow)
                throw new ArgumentException("The severe band must open below the moderate band.");
            if (float.IsNaN(floorMultiplier) || float.IsInfinity(floorMultiplier) || floorMultiplier <= 0f)
                throw new ArgumentOutOfRangeException("floorMultiplier", "Floor multiplier must be positive.");
            if (float.IsNaN(fullMultiplier) || float.IsInfinity(fullMultiplier) || fullMultiplier <= 0f)
                throw new ArgumentOutOfRangeException("fullMultiplier", "Full multiplier must be positive.");
            ModerateBelow = moderateBelow;
            SevereBelow = severeBelow;
            FloorMultiplier = floorMultiplier;
            FullMultiplier = fullMultiplier;
        }

        public AccessEffectLevel Level(float score)
        {
            Validate(score, "score");
            if (score >= ModerateBelow) return AccessEffectLevel.None;
            if (score >= SevereBelow) return AccessEffectLevel.Mild;
            if (score > 0f) return AccessEffectLevel.Moderate;
            return AccessEffectLevel.Severe;
        }

        /// <summary>Linear multiplier between floor (score 0) and full (score 1). With the
        /// default floor 1 and full 1.25, a fully served origin earns a quarter more and a
        /// cut-off origin keeps its baseline: gradual, never a cliff.</summary>
        public float ProductivityMultiplier(float score)
        {
            Validate(score, "score");
            return FloorMultiplier + (FullMultiplier - FloorMultiplier) * score;
        }

        /// <summary>Occupancy/demand multiplier for the affected side. Poor access reduces
        /// demand gradually toward the floor; the floor itself is policy, not accident.</summary>
        public float DemandMultiplier(float score)
        {
            Validate(score, "score");
            float demandFloor = 1f / FloorMultiplier;
            float demandFull = 1f / FullMultiplier;
            return demandFloor + (demandFull - demandFloor) * score;
        }

        private static void Validate(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(name, "Scores live on 0..1.");
        }
    }
}
