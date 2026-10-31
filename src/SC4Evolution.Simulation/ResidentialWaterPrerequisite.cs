using System;

namespace SC4Evolution.Simulation
{
    public enum ResidentialWaterEvidence : byte { Unknown = 0, Unserved = 1, Served = 2 }
    [Flags]
    public enum ResidentialClassMask : byte { None = 0, Low = 1, Medium = 2, High = 4, All = 7 }
    public enum ResidentialChoiceKind : byte { NativeFallback = 0, ControlledReject = 1, Selected = 2 }

    public sealed class ResidentialClassChoice
    {
        public ResidentialChoiceKind Kind { get; private set; }
        public ResidentialClass Class { get; private set; }
        public ResidentialClassMask Mask { get; private set; }
        public ResidentialWaterEvidence Evidence { get; private set; }
        internal ResidentialClassChoice(ResidentialChoiceKind kind, ResidentialClass selected, ResidentialClassMask mask, ResidentialWaterEvidence evidence)
        { Kind = kind; Class = selected; Mask = mask; Evidence = evidence; }
    }

    /// <summary>Only the universal cap-zero prerequisite; no CS1-to-SC4 stage conversion.</summary>
    public static class ResidentialWaterPrerequisite
    {
        public static ResidentialClassChoice Choose(DemandNodeState[] states, ResidentialWaterProfile profile, ResidentialWaterEvidence evidence)
        {
            if (evidence < ResidentialWaterEvidence.Unknown || evidence > ResidentialWaterEvidence.Served) throw new ArgumentOutOfRangeException("evidence");
            if (profile == null || evidence == ResidentialWaterEvidence.Unknown) return NativeFallback();
            ResidentialClassMask mask = ResidentialClassMask.All;
            if (evidence == ResidentialWaterEvidence.Unserved)
            {
                if (profile.LowMaximumStage == 0) mask &= ~ResidentialClassMask.Low;
                if (profile.MediumMaximumStage == 0) mask &= ~ResidentialClassMask.Medium;
                if (profile.HighMaximumStage == 0) mask &= ~ResidentialClassMask.High;
            }
            return ChoosePermitted(states, mask, evidence);
        }

        public static ResidentialClassChoice ChoosePermitted(DemandNodeState[] states, ResidentialClassMask mask, ResidentialWaterEvidence evidence)
        {
            if (states == null) throw new ArgumentNullException("states");
            if ((mask & ~ResidentialClassMask.All) != 0) throw new ArgumentOutOfRangeException("mask");
            if (evidence < ResidentialWaterEvidence.Unknown || evidence > ResidentialWaterEvidence.Served) throw new ArgumentOutOfRangeException("evidence");
            for (int index = 0; index < 3; index++)
            {
                uint key = (uint)(0x21010 + index * 0x10); int count = 0;
                foreach (DemandNodeState state in states)
                    if (state.Key == key)
                    {
                        count++;
                        if (float.IsNaN(state.ActiveDemand) || float.IsInfinity(state.ActiveDemand) || state.NormalizedDemand < -1000 || state.NormalizedDemand > 1000)
                            return NativeFallback();
                    }
                if (count != 1) return NativeFallback();
            }
            int best = 0; ResidentialClass selected = 0;
            // Explicit developer-key order, independent of caller array order.
            for (int index = 0; index < 3; index++)
            {
                if ((mask & (ResidentialClassMask)(1 << index)) == 0) continue;
                uint key = (uint)(0x21010 + index * 0x10);
                foreach (DemandNodeState state in states)
                    if (state.Key == key)
                    {
                        if (state.ActiveDemand > 0 && state.NormalizedDemand > best)
                        { best = state.NormalizedDemand; selected = (ResidentialClass)(index + 1); }
                    }
            }
            return new ResidentialClassChoice(selected == 0 ? ResidentialChoiceKind.ControlledReject : ResidentialChoiceKind.Selected, selected, mask, evidence);
        }

        public static ResidentialClassChoice NativeFallback()
        { return new ResidentialClassChoice(ResidentialChoiceKind.NativeFallback, 0, ResidentialClassMask.None, ResidentialWaterEvidence.Unknown); }

        public static bool IsSupportedNativeQueryPosition(float x, float y, float z)
        {
            if (!Finite(x) || !Finite(y) || !Finite(z) || x < -4896f || x >= 4896f || z < -4896f || z >= 4896f) return false;
            // Also reject float32 rounding into a clamped edge cell.
            float gridX = x / 38.25f + 128f, gridZ = z / 38.25f + 128f;
            return gridX >= 0f && gridX < 256f && gridZ >= 0f && gridZ < 256f;
        }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
