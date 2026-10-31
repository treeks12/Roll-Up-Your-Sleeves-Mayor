using System;
using System.Reflection;
using HarmonyLib;

namespace SC4Evolution.Mod.Economy
{
    // Separate patch type is required: Harmony keys __state by declaring patch
    // type. Sharing the class-identity prefix's type would alias incompatible state.
    internal static class ResidentialCapacityMutationHooks
    {
        private sealed class State
        {
            internal EconomicBridge Owner;
            internal ResidentialCapacityBridge Capacity;
            internal int Session;
            internal ushort Building;
            internal ResidentialCapacityMutation Mutation;
        }
        internal static HarmonyMethod Prefix()
        { return new HarmonyMethod(typeof(ResidentialCapacityMutationHooks).GetMethod("Begin", BindingFlags.Static | BindingFlags.NonPublic)); }
        internal static HarmonyMethod Finalizer()
        { return new HarmonyMethod(typeof(ResidentialCapacityMutationHooks).GetMethod("End", BindingFlags.Static | BindingFlags.NonPublic)); }
        internal static HarmonyMethod PhasePrefix()
        { return new HarmonyMethod(typeof(ResidentialCapacityMutationHooks).GetMethod("BeginPhase", BindingFlags.Static | BindingFlags.NonPublic)); }
        internal static HarmonyMethod ReleasePrefix()
        { return new HarmonyMethod(typeof(ResidentialCapacityMutationHooks).GetMethod("BeginRelease", BindingFlags.Static | BindingFlags.NonPublic)); }
        private static void Begin(ushort __0, out State __state)
        { __state = Capture(__0, ResidentialCapacityMutationKind.Critical); }
        private static void BeginPhase(ushort __0, out State __state)
        { __state = Capture(__0, ResidentialCapacityMutationKind.Phase); }
        private static void BeginRelease(ushort __0, out State __state)
        {
            // The save barrier precedes class/capacity removal, including releases
            // invoked outside either residential simulation body.
            __state = Capture(__0, ResidentialCapacityMutationKind.Critical);
            ResidentialClassPatches.Released(__0);
        }
        private static State Capture(ushort building, ResidentialCapacityMutationKind kind)
        {
            EconomicBridge owner = EconomicDemandExtension.Active;
            if (owner == null || owner.Capacity == null) return null;
            State state = new State { Owner = owner, Capacity = owner.Capacity,
                Session = owner.DemandContextSession, Building = building };
            try { state.Mutation = state.Capacity.BeginMutation(building, kind); }
            catch (Exception error) { ResidentialClassPatches.DisarmCapacity(state.Capacity, "Native mutation barrier failed: " + error.Message); }
            return state;
        }
        private static Exception End(Exception __exception, State __state)
        {
            State state = __state;
            // Always return the exact token to its owner, even after an epoch or
            // active-bridge change. The bridge validates it; callers cannot debit
            // a different session's scope or leak an old scope by skipping End.
            if (state != null)
                try { state.Capacity.EndMutation(state.Mutation, state.Building, __exception); }
                catch (Exception error) { ResidentialClassPatches.DisarmCapacity(state.Capacity, "Native mutation finalization failed: " + error.Message); }
            return __exception;
        }
    }
}
