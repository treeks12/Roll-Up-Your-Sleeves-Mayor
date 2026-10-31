using System;
using System.Reflection;
using HarmonyLib;

namespace SC4Evolution.Mod.Economy
{
    // Only the reserved wrapper's one native call may lend its attempt to the
    // global create barrier. Nested calls are distinguished by the core's
    // single-use CreateScopeClaimed flag, never by inventing another token.
    internal static class ResidentialCapacityCreationContext
    {
        internal sealed class Frame
        {
            internal EconomicBridge Owner;
            internal ResidentialCapacityBridge Capacity;
            internal ResidentialCapacityAttempt Attempt;
            internal int Session;
            internal Frame Previous;
        }
        [ThreadStatic] private static Frame _current;
        internal static Frame Enter(EconomicBridge owner, ResidentialCapacityBridge capacity, ResidentialCapacityAttempt attempt)
        {
            Frame frame = new Frame { Owner = owner, Capacity = capacity, Attempt = attempt,
                Session = capacity.SessionEpoch, Previous = _current };
            _current = frame; return frame;
        }
        internal static void Exit(Frame frame)
        {
            if (ReferenceEquals(_current, frame)) _current = frame.Previous;
            else ResidentialClassPatches.DisarmCapacity(frame.Capacity, "Controlled native creation scope lost its TLS owner.");
        }
        internal static ResidentialCapacityAttempt Current(EconomicBridge owner, ResidentialCapacityBridge capacity)
        {
            Frame frame = _current;
            return frame != null && ReferenceEquals(frame.Owner, owner) && ReferenceEquals(frame.Capacity, capacity)
                && ReferenceEquals(owner, EconomicDemandExtension.Active) && frame.Session == owner.DemandContextSession
                && frame.Session == capacity.SessionEpoch ? frame.Attempt : null;
        }
    }

    // Separate declaring type avoids aliasing Harmony __state with the class
    // identity hooks. Prefix never reads the uninitialized native out argument.
    internal static class ResidentialCapacityCreationHooks
    {
        private sealed class State
        {
            internal BuildingManager Manager;
            internal ResidentialCapacityBridge Capacity;
            internal ResidentialCapacityMutation Mutation;
        }
        internal static HarmonyMethod Prefix()
        { return new HarmonyMethod(typeof(ResidentialCapacityCreationHooks).GetMethod("Begin", BindingFlags.Static | BindingFlags.NonPublic)); }
        internal static HarmonyMethod Finalizer()
        { return new HarmonyMethod(typeof(ResidentialCapacityCreationHooks).GetMethod("End", BindingFlags.Static | BindingFlags.NonPublic)); }
        private static void Begin(BuildingManager __instance, out State __state)
        {
            EconomicBridge owner = EconomicDemandExtension.Active;
            __state = null;
            if (owner == null || owner.Capacity == null) return;
            State state = new State { Manager = __instance, Capacity = owner.Capacity };
            __state = state;
            try { state.Mutation = state.Capacity.BeginCreateMutation(ResidentialCapacityCreationContext.Current(owner, state.Capacity)); }
            catch (Exception error) { ResidentialClassPatches.DisarmCapacity(state.Capacity, "Native creation barrier failed: " + error.Message); }
        }
        private static Exception End(ushort building, bool __result, Exception __exception, State __state)
        {
            State state = __state;
            if (state != null)
            {
                Exception diagnostic = __exception;
                if (diagnostic == null && !__result && NewLotGrowthGate.NativeBuildingExists(state.Manager, building))
                    diagnostic = new InvalidOperationException("Native create returned false with a live out-ID.");
                try { state.Capacity.EndMutation(state.Mutation, building, diagnostic); }
                catch (Exception error) { ResidentialClassPatches.DisarmCapacity(state.Capacity, "Native creation finalization failed: " + error.Message); }
            }
            return __exception;
        }
    }
}
