using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using ColossalFramework.Math;
using CitiesHarmony.API;
using HarmonyLib;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Economy
{
    // Keep all HarmonyLib signatures out of lifecycle/IUserMod classes. The official
    // provider check does not subscribe, download, or contact a remote service.
    internal static class GrowthGateDependency
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Available() { return HarmonyHelper.IsHarmonyInstalled; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(EconomicBridge owner) { NewLotGrowthGate.Install(owner); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Remove(EconomicBridge owner) { NewLotGrowthGate.Remove(owner); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool CapacityHooksInstalled(EconomicBridge owner) { return NewLotGrowthGate.CapacityHooksInstalled(owner); }
    }

    internal static class NewLotGrowthGate
    {
        private const string HarmonyId = "SC4Evolution.NewLotGrowthGate.v1";
        private static readonly object Sync = new object();
        private static readonly HashSet<EconomicBridge> Owners = new HashSet<EconomicBridge>();
        private static bool _installed;
        private static readonly MethodInfo Target = typeof(ZoneBlock).GetMethod("SimulationStep", new Type[] { typeof(ushort) });
        private static readonly MethodInfo Create = typeof(BuildingManager).GetMethod("CreateBuilding", new Type[]
        {
            typeof(ushort).MakeByRefType(), typeof(Randomizer).MakeByRefType(), typeof(BuildingInfo),
            typeof(Vector3), typeof(float), typeof(int), typeof(uint)
        });
        private static readonly MethodInfo Wrapper = typeof(NewLotGrowthGate).GetMethod("CreateEligibleBuilding", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo NativeRoll = typeof(Randomizer).GetMethod("Int32", new Type[] { typeof(uint) });
        [ThreadStatic] private static GrowthAttempt _current;
        private sealed class GrowthAttempt
        {
            internal EconomicBridge Owner;
            internal ResidentialCapacityBridge Capacity;
            internal ResidentialCapacityIntent Intent;
            internal int Session, Roll, Threshold;
            internal ushort Block;
            internal bool GateObserved;
            internal bool NativeAllowed { get { return Roll < Threshold; } }
        }

        internal static bool CapacityHooksInstalled(EconomicBridge owner)
        { lock (Sync) return _installed && Owners.Contains(owner); }

        internal static void Install(EconomicBridge owner)
        {
            lock (Sync)
            {
            if (_installed) { Owners.Add(owner); return; }
            if (BuildConfig.applicationVersion != "1.21.1-f9")
                throw new InvalidOperationException("New-lot gate is verified only for CS1 1.21.1-f9.");
            if (Target == null || Create == null || Wrapper == null) throw new MissingMethodException("New-lot growth boundary is unavailable.");
            ResidentialGrowthNativeBoundary.RequireZoneBlock(Target);
            // Harmony will run the transpiler before installing the replacement. It must
            // identify exactly one call; an unexpected body fails before effects engage.
            Harmony harmony = new Harmony(HarmonyId);
            try
            {
                harmony.Patch(Target, prefix: Hook("BeginAttempt"), transpiler: Hook("Transpile"), finalizer: Hook("EndAttempt"));
                _installed = true;
                Owners.Add(owner);
            }
            catch
            {
                harmony.UnpatchAll(HarmonyId);
                throw;
            }
            }
        }

        internal static void Remove(EconomicBridge owner)
        {
            Disarm(owner == null ? null : owner.Capacity, "New-lot admission hooks are being removed.");
            lock (Sync)
            {
                Owners.Remove(owner);
                if (!_installed || Owners.Count != 0) return;
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
                _installed = false;
            }
        }

        private static HarmonyMethod Hook(string name) { return new HarmonyMethod(typeof(NewLotGrowthGate).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }
        private static void BeginAttempt(ushort blockID, out GrowthAttempt __state)
        {
            __state = _current;
            EconomicBridge owner = EconomicDemandExtension.Active;
            _current = new GrowthAttempt { Owner = owner, Capacity = owner == null ? null : owner.Capacity,
                Session = owner == null ? 0 : owner.DemandContextSession, Block = blockID };
        }
        private static Exception EndAttempt(Exception __exception, GrowthAttempt __state)
        {
            GrowthAttempt attempt = _current;
            _current = __state;
            if (__exception != null && attempt != null) Disarm(attempt.Capacity, "Native ZoneBlock execution threw; capacity reconciliation required.");
            if (attempt != null && attempt.Capacity != null && attempt.Intent != null)
                try { attempt.Capacity.EndIntent(attempt.Intent); }
                catch (Exception error) { Disarm(attempt.Capacity, "New-lot intent cleanup failed: " + error.Message); }
            return __exception;
        }
        private static bool Current(GrowthAttempt attempt, EconomicBridge owner)
        {
            return attempt != null && ReferenceEquals(attempt.Owner, owner) && ReferenceEquals(owner, EconomicDemandExtension.Active)
                && owner != null && attempt.Session == owner.DemandContextSession && ReferenceEquals(attempt.Capacity, owner.Capacity)
                && attempt.Capacity != null && attempt.Capacity.SessionEpoch == attempt.Session;
        }
        private static void Disarm(ResidentialCapacityBridge capacity, string reason)
        {
            if (capacity != null) try { capacity.ConfirmHookSet(capacity.SessionEpoch, false, reason); } catch { }
        }
        private static bool AdmitCandidate(int roll, int nativeThreshold, ItemClass.Zone zone, ushort blockID)
        {
            GrowthAttempt attempt = _current;
            if (attempt == null) return roll < nativeThreshold;
            attempt.Roll = roll; attempt.Threshold = nativeThreshold; attempt.GateObserved = true;
            EconomicBridge owner = EconomicDemandExtension.Active;
            if (!Current(attempt, owner) || attempt.Block != blockID || !attempt.Capacity.ModeRequested) return roll < nativeThreshold;
            try
            {
                attempt.Intent = attempt.Capacity.BeginIntent(owner.Published, roll, nativeThreshold, zone, blockID);
                if (attempt.Intent == null || attempt.Intent.Decision == ResidentialCapacityIntentDecision.NativeFallback) return roll < nativeThreshold;
                return attempt.Intent.Decision == ResidentialCapacityIntentDecision.Continue;
            }
            catch (Exception error) { Disarm(attempt.Capacity, "New-lot admission failed before native lookup: " + error.Message); return roll < nativeThreshold; }
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> body = new List<CodeInstruction>(instructions);
            int matches = 0, draw = -1;
            for (int i = 0; i < body.Count; ++i)
                if (Calls(body[i], NativeRoll) && i > 0 && Integer(body[i - 1], 100)) { if (draw != -1) throw new InvalidOperationException("Ambiguous native growth draw."); draw = i; }
            ValidateNativeDraw(body, draw);
            foreach (CodeInstruction instruction in body)
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, Create)) matches++;
            if (matches != 1) throw new InvalidOperationException("Expected one ZoneBlock growable creation call, found " + matches + ". Native gameplay retained.");
            foreach (CodeInstruction instruction in body)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, Create))
                {
                    // Existing stack already contains BuildingManager followed by the exact
                    // original parameters. Preserve labels and exception blocks in place.
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = Wrapper;
                }
            }
            CodeInstruction branch = body[draw + 2];
            CodeInstruction zone = new CodeInstruction(OpCodes.Ldloc_S, body[draw + 7].operand);
            zone.labels.AddRange(branch.labels); branch.labels.Clear();
            branch.opcode = OpCodes.Brtrue;
            body.InsertRange(draw + 2, new CodeInstruction[] { zone, new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, typeof(NewLotGrowthGate).GetMethod("AdmitCandidate", BindingFlags.Static | BindingFlags.NonPublic)) });
            return body;
        }
        private static bool Calls(CodeInstruction instruction, MethodInfo method)
        { return (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, method); }
        private static bool Integer(CodeInstruction instruction, int value)
        { return (instruction.opcode == OpCodes.Ldc_I4 || instruction.opcode == OpCodes.Ldc_I4_S) && Convert.ToInt32(instruction.operand) == value; }
        private static int Local(CodeInstruction instruction)
        {
            if (instruction.opcode == OpCodes.Ldloc_0) return 0;
            if (instruction.opcode != OpCodes.Ldloc_S && instruction.opcode != OpCodes.Ldloc && instruction.opcode != OpCodes.Stloc_S && instruction.opcode != OpCodes.Stloc) return -1;
            LocalBuilder builder = instruction.operand as LocalBuilder; if (builder != null) return builder.LocalIndex;
            LocalVariableInfo variable = instruction.operand as LocalVariableInfo; if (variable != null) return variable.LocalIndex;
            return Convert.ToInt32(instruction.operand);
        }
        private static void ValidateNativeDraw(List<CodeInstruction> body, int draw)
        {
            if (draw < 7 || draw + 11 >= body.Count || body[draw - 7].opcode != OpCodes.Ldarg_0 || Local(body[draw - 6]) != 12
                || body[draw - 5].opcode != OpCodes.Call || ((MethodInfo)body[draw - 5].operand).Name != "IsGoodPlace"
                || (body[draw - 4].opcode != OpCodes.Stloc_S && body[draw - 4].opcode != OpCodes.Stloc) || Local(body[draw - 4]) != 35
                || body[draw - 3].opcode != OpCodes.Call || ((MethodInfo)body[draw - 3].operand).DeclaringType != typeof(ColossalFramework.Singleton<SimulationManager>)
                || body[draw - 2].opcode != OpCodes.Ldflda || ((FieldInfo)body[draw - 2].operand).DeclaringType != typeof(SimulationManager)
                || ((FieldInfo)body[draw - 2].operand).Name != "m_randomizer" || Local(body[draw + 1]) != 10
                || (body[draw + 2].opcode != OpCodes.Blt && body[draw + 2].opcode != OpCodes.Blt_S)
                || Local(body[draw + 3]) != 35 || (body[draw + 4].opcode != OpCodes.Brfalse && body[draw + 4].opcode != OpCodes.Brfalse_S)
                || Local(body[draw + 5]) != 0 || body[draw + 6].opcode != OpCodes.Ldfld || ((FieldInfo)body[draw + 6].operand).Name != "m_goodAreaFound"
                || Local(body[draw + 7]) != 6 || !Integer(body[draw + 8], 1024) || body[draw + 9].opcode != OpCodes.Stelem_I2
                || body[draw + 10].opcode != OpCodes.Ret || Local(body[draw + 11]) != 35
                || !body[draw + 10].labels.Contains((Label)body[draw + 4].operand) || !body[draw + 11].labels.Contains((Label)body[draw + 2].operand))
                throw new InvalidOperationException("Native draw/threshold/branch/rejection boundary differs.");
            for (int i = draw - 7; i <= draw + 11; ++i) if (body[i].blocks.Count != 0)
                throw new InvalidOperationException("Native growth gate crosses an unsupported exception region.");
        }

        internal static bool CreateEligibleBuilding(BuildingManager manager, out ushort building, ref Randomizer randomizer,
            BuildingInfo info, Vector3 position, float angle, int length, uint buildIndex)
        {
            EconomicBridge bridge = EconomicDemandExtension.Active;
            ResidentialCapacityBridge capacity = bridge == null ? null : bridge.Capacity;
            if (capacity != null && capacity.ModeRequested && info != null && info.m_class != null && info.m_class.m_service == ItemClass.Service.Residential)
                return CreateCapacityBuilding(manager, out building, ref randomizer, info, position, angle, length, buildIndex, bridge, capacity);
            SegmentedDemandSnapshot snapshot = bridge == null ? null : bridge.Published;
            if (bridge != null && !bridge.CanUseDemand(snapshot)) snapshot = null;
            if (bridge != null && bridge.IsLotClassStage && info != null && info.m_class.m_service == ItemClass.Service.Residential)
            {
                ResidentialClassChoice choice = bridge.Classes == null ? ResidentialWaterPrerequisite.NativeFallback() : bridge.Classes.ChooseAt(snapshot, position);
                if (choice.Kind == ResidentialChoiceKind.ControlledReject) { building = 0; return false; }
                bool created = manager.CreateBuilding(out building, ref randomizer, info, position, angle, length, buildIndex);
                if (created && choice.Kind == ResidentialChoiceKind.Selected)
                    try { bridge.Classes.Commit(building, buildIndex, info, choice.Class); }
                    catch (Exception error) { bridge.Classes.Disable("Class commit failed after native new-lot creation: " + error.Message); }
                return created;
            }
            if (snapshot != null && snapshot.Converged && snapshot.GameplayEnabled && info != null)
            {
                uint key = 0;
                if (info.m_class.m_service == ItemClass.Service.Residential)
                {
                    int wealth = info.m_class.m_level == ItemClass.Level.Level1 ? 0 :
                        (info.m_class.m_level <= ItemClass.Level.Level3 ? 1 : 2);
                    key = (uint)(0x21010 + wealth * 0x10);
                }
                else
                {
                    int segment = CityCensus.SegmentFor(info.m_class, info.m_class.m_level);
                    uint[] keys = { 0x23110, 0x23120, 0x23130, 0x23320, 0x23330, 0x24100, 0x24200, 0x24300, 0x24400 };
                    if (segment >= 0) key = keys[segment];
                }
                int demand;
                if (key != 0 && snapshot.TryGetDemand(key, out demand) && demand <= 0)
                { building = 0; return false; }
            }
            return manager.CreateBuilding(out building, ref randomizer, info, position, angle, length, buildIndex);
        }

        private static bool CreateCapacityBuilding(BuildingManager manager, out ushort building, ref Randomizer randomizer,
            BuildingInfo info, Vector3 position, float angle, int length, uint buildIndex, EconomicBridge owner, ResidentialCapacityBridge capacity)
        {
            GrowthAttempt scope = _current; ResidentialCapacityAttempt attempt = null;
            if (Current(scope, owner) && scope.GateObserved && scope.Intent != null)
                try { attempt = capacity.TryReserveNew(scope.Intent, info, position, length, buildIndex); }
                catch (Exception error) { Disarm(capacity, "New-lot reservation failed: " + error.Message); }
            if (attempt == null || attempt.Decision == ResidentialCapacityAttemptDecision.NativeFallback)
            {
                // Prospective fallback reuses the captured comparison, without replaying
                // any lookup RNG or manufacturing a controlled class/capacity commit.
                if (scope != null && scope.GateObserved && !scope.NativeAllowed) { building = 0; return false; }
                bool returned = false, native = false; building = 0;
                try
                {
                    native = manager.CreateBuilding(out building, ref randomizer, info, position, angle, length, buildIndex);
                    returned = true;
                    return native;
                }
                finally
                {
                    if (!returned) Disarm(capacity, "Native fallback creation threw; unbound stock requires reconciliation.");
                    if (NativeBuildingExists(manager, building))
                    {
                        if (!native) Disarm(capacity, "Native fallback left a live out-ID without success; unbound stock requires reconciliation.");
                        // Global create finalization owns unbound observation;
                        // doing it here could race a surrounding mutation scope.
                    }
                }
            }
            if (attempt.Decision != ResidentialCapacityAttemptDecision.Reserved) { building = 0; return false; }
            bool created = false;
            building = 0;
            try
            {
                ResidentialCapacityCreationContext.Frame marker = ResidentialCapacityCreationContext.Enter(owner, capacity, attempt);
                try { created = manager.CreateBuilding(out building, ref randomizer, info, position, angle, length, buildIndex); }
                finally { ResidentialCapacityCreationContext.Exit(marker); }
                if (created)
                {
                    // Mark writes the irreversible flag before any validation/lock.
                    bool marked = MarkCapacityCreated(capacity, attempt);
                    try { if (marked && !capacity.CommitNative(attempt, building, buildIndex, info)) Disarm(capacity, "New-lot native success requires capacity reconciliation."); }
                    catch (Exception error) { Disarm(capacity, "New-lot commit failed after native creation: " + error.Message); }
                }
                return created;
            }
            finally
            {
                if (!created)
                {
                    // Native AI/callbacks may throw after allocation but before the
                    // boolean return. A live out-ID is unresolved stock, never refund.
                    if (NativeBuildingExists(manager, building))
                    {
                        MarkCapacityCreated(capacity, attempt);
                        Disarm(capacity, "Native new-lot call did not return success but left a live out-ID; reconciliation required.");
                    }
                    else try { capacity.Cancel(attempt); } catch (Exception error) { Disarm(capacity, "New-lot reservation cancellation failed: " + error.Message); }
                }
            }
        }
        internal static bool NativeBuildingExists(BuildingManager manager, ushort id)
        {
            return manager != null && id != 0 && id < manager.m_buildings.m_buffer.Length
                && (manager.m_buildings.m_buffer[id].m_flags & Building.Flags.Created) != 0;
        }
        internal static bool MarkCapacityCreated(ResidentialCapacityBridge capacity, ResidentialCapacityAttempt attempt)
        {
            try { capacity.MarkNativeCreated(attempt); return true; }
            catch (Exception error) { Disarm(capacity, "Native creation mark requires reconciliation: " + error.Message); return false; }
        }
    }
}
