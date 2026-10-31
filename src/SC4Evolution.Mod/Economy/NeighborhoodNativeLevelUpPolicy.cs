using System;

namespace SC4Evolution.Mod.Economy
{
    /// <summary>
    /// Pure, bounded part of the native neighborhood application proof.  The game
    /// supplies the native target/progress and a 0..100 attractiveness score; this
    /// policy may slow an upgrade and may keep a building at its current level, but
    /// never invents a lower level or increases native progress.
    /// </summary>
    public struct NeighborhoodNativeGrowthInput
    {
        public int CurrentLevel;
        public int TargetLevel;
        public int SecondaryProgress;
        public int PrimaryProgress;
        public float Attractiveness;
    }

    public struct NeighborhoodNativeGrowthOutput
    {
        public int TargetLevel;
        public int SecondaryProgress;
        public int PrimaryProgress;
    }

    public static class NeighborhoodNativeLevelUpPolicy
    {
        private const int MaximumProgress = 15;
        private const float UpgradeGate = 50f;

        public static NeighborhoodNativeGrowthOutput ApplyResidential(NeighborhoodNativeGrowthInput input)
        {
            return Apply(input);
        }

        public static NeighborhoodNativeGrowthOutput ApplyCommercial(NeighborhoodNativeGrowthInput input)
        {
            return Apply(input);
        }

        private static NeighborhoodNativeGrowthOutput Apply(NeighborhoodNativeGrowthInput input)
        {
            Validate(input);
            float score = Clamp(input.Attractiveness, 0f, 100f);
            NeighborhoodNativeGrowthOutput output = new NeighborhoodNativeGrowthOutput();
            output.TargetLevel = input.TargetLevel;
            output.SecondaryProgress = ClampProgress(input.SecondaryProgress);
            output.PrimaryProgress = ScaleProgress(input.PrimaryProgress, score);

            // The native AI remains responsible for all downgrade/abandonment
            // decisions.  This hook only prevents an upgrade when the native lot
            // evidence is materially unattractive.
            if (score < UpgradeGate && output.TargetLevel > input.CurrentLevel)
                output.TargetLevel = input.CurrentLevel;
            return output;
        }

        private static int ScaleProgress(int progress, float score)
        {
            int bounded = ClampProgress(progress);
            if (score >= 100f || bounded == 0) return bounded;
            return ClampProgress((int)Math.Round((double)bounded * score / 100d, MidpointRounding.AwayFromZero));
        }

        private static int ClampProgress(int value)
        {
            return value < 0 ? 0 : (value > MaximumProgress ? MaximumProgress : value);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("Attractiveness must be finite.");
            return value < min ? min : (value > max ? max : value);
        }

        private static void Validate(NeighborhoodNativeGrowthInput input)
        {
            if (input.CurrentLevel < 0 || input.CurrentLevel > 5)
                throw new ArgumentOutOfRangeException("CurrentLevel");
            if (input.TargetLevel < 0 || input.TargetLevel > 5)
                throw new ArgumentOutOfRangeException("TargetLevel");
        }
    }
}
