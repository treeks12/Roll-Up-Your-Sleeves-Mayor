using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace SC4Evolution.Mod.Economy
{
    internal static class IncomingDemandDependency
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(EconomicBridge owner) { IncomingResidentDemandPatches.Install(owner); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Remove(EconomicBridge owner) { IncomingResidentDemandPatches.Remove(owner); }
    }

    internal static class IncomingResidentDemandPatches
    {
        private const string Id = "SC4Evolution.ResidentialDemandContext.v1";
        private static readonly object Sync = new object();
        private static readonly HashSet<EconomicBridge> Owners = new HashSet<EconomicBridge>();
        private static bool _installed;
        private static bool _faulted;
        private static readonly MethodInfo Construction = FindTarget("CalculateResidentialDemand");
        private static readonly MethodInfo Incoming = FindTarget("CalculateIncomingResidentDemand");
        private static readonly MethodInfo SharedCallback = typeof(ZoneManager).Assembly.GetType("DemandWrapper", true)
            .GetMethod("OnCalculateResidentialDemand", new Type[] { typeof(int).MakeByRefType() });
        private static MethodInfo FindTarget(string name)
        {
            MethodInfo method = typeof(ZoneManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                null, new Type[] { typeof(District).MakeByRefType() }, null);
            if (method == null || method.ReturnType != typeof(int) || method.IsStatic) throw new MissingMethodException("Residential demand calculator signature changed: " + name);
            return method;
        }
        internal static void Install(EconomicBridge owner)
        {
            lock (Sync)
            {
                if (_faulted) throw new InvalidOperationException("Residential demand hook cleanup was incomplete; native fallback requires an application restart before reactivation.");
                if (_installed) { Owners.Add(owner); return; }
                if (BuildConfig.applicationVersion != "1.21.1-f9" || SharedCallback == null) throw new InvalidOperationException("Residential demand context is verified only for CS1 1.21.1-f9.");
                Harmony harmony = new Harmony(Id);
                try
                {
                    Patch(harmony, Construction, "ConstructionPrefix");
                    Patch(harmony, Incoming, "IncomingPrefix");
                    _installed = true; Owners.Add(owner);
                }
                catch
                {
                    try { harmony.UnpatchAll(Id); }
                    catch { _faulted = true; }
                    throw;
                }
            }
        }
        private static void Patch(Harmony harmony, MethodInfo target, string prefix)
        {
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(IncomingResidentDemandPatches).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)),
                transpiler: new HarmonyMethod(typeof(IncomingResidentDemandPatches).GetMethod("ValidateBody", BindingFlags.NonPublic | BindingFlags.Static)),
                finalizer: new HarmonyMethod(typeof(IncomingResidentDemandPatches).GetMethod("FinalizeCall", BindingFlags.NonPublic | BindingFlags.Static)));
        }
        internal static void Remove(EconomicBridge owner)
        {
            lock (Sync)
            {
                Owners.Remove(owner);
                if (!_installed || Owners.Count != 0) return;
                try { new Harmony(Id).UnpatchAll(Id); }
                catch { _faulted = true; throw; }
                finally { _installed = false; }
            }
        }
        private static IEnumerable<CodeInstruction> ValidateBody(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> body = new List<CodeInstruction>(instructions);
            int callbacks = 0;
            foreach (CodeInstruction instruction in body)
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, SharedCallback)) callbacks++;
            if (callbacks != 1) throw new InvalidOperationException("Expected exactly one shared residential callback in each native calculator; found " + callbacks + ".");
            return body; // Guard only; no native instruction/RNG/formula changes.
        }
        private static void ConstructionPrefix(out IncomingResidentDemandContext.Frame __state)
        { __state = Begin(ResidentialDemandKind.Construction); }
        private static void IncomingPrefix(out IncomingResidentDemandContext.Frame __state)
        { __state = Begin(ResidentialDemandKind.Incoming); }
        private static IncomingResidentDemandContext.Frame Begin(ResidentialDemandKind kind)
        {
            EconomicBridge owner = EconomicDemandExtension.Active;
            return IncomingResidentDemandContext.Enter(owner == null ? null : owner.DemandContextBinding, kind);
        }
        private static Exception FinalizeCall(Exception __exception, IncomingResidentDemandContext.Frame __state)
        { return IncomingResidentDemandContext.Finish(__exception, __state); }
    }
}
