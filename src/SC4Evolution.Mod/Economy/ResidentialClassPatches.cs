using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using ColossalFramework;
using ColossalFramework.Math;
using HarmonyLib;
using ICities;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Economy
{
    internal static class ResidentialClassPatchDependency
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(EconomicBridge owner) { ResidentialClassPatches.Install(owner); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Remove(EconomicBridge owner) { ResidentialClassPatches.Remove(owner); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void InstallMetadata(EconomicBridge owner) { ResidentialUpgradeIdentityPatches.Acquire(owner); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void RemoveMetadata(EconomicBridge owner) { ResidentialUpgradeIdentityPatches.Release(owner); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool CapacityHooksInstalled(EconomicBridge owner) { return ResidentialClassPatches.CapacityHooksInstalled(owner); }
    }

    // One shared metadata hook with counted passive/effects ownership. This remains
    // active for a retained Shadow ledger without enabling any class effects.
    internal static class ResidentialUpgradeIdentityPatches
    {
        private const string Id = "SC4Evolution.ResidentialLotIdentity.v1";
        private static readonly object Sync = new object();
        private static readonly Dictionary<EconomicBridge, int> Owners = new Dictionary<EconomicBridge, int>();
        private static bool _installed;
        internal static void Acquire(EconomicBridge owner)
        {
            lock (Sync)
            {
                if (!_installed)
                {
                    MethodInfo target = typeof(BuildingManager).GetMethod("UpdateBuildingInfo", new Type[] { typeof(ushort), typeof(Building).MakeByRefType(), typeof(BuildingInfo) });
                    if (BuildConfig.applicationVersion != "1.21.1-f9" || target == null) throw new InvalidOperationException("Residential upgrade identity boundary differs.");
                    Harmony harmony = new Harmony(Id);
                    try
                    {
                        harmony.Patch(target,
                            prefix: new HarmonyMethod(typeof(ResidentialClassPatches).GetMethod("BeforeUpgrade", BindingFlags.NonPublic | BindingFlags.Static)),
                            postfix: new HarmonyMethod(typeof(ResidentialClassPatches).GetMethod("AfterUpgrade", BindingFlags.NonPublic | BindingFlags.Static)));
                        ResidentialClassPatches.InstallCapacityUpgradeHooks(harmony, target);
                        _installed = true;
                    }
                    catch { harmony.UnpatchAll(Id); throw; }
                }
                int count; Owners.TryGetValue(owner, out count); Owners[owner] = count + 1;
            }
        }
        internal static void Release(EconomicBridge owner)
        {
            ResidentialClassPatches.DisarmCapacity(owner == null ? null : owner.Capacity, "Residential upgrade hooks are being removed.");
            lock (Sync)
            {
                int count;
                if (!Owners.TryGetValue(owner, out count)) return;
                if (count > 1) Owners[owner] = count - 1; else Owners.Remove(owner);
                if (Owners.Count == 0 && _installed) { new Harmony(Id).UnpatchAll(Id); _installed = false; }
            }
        }
    }

    // Metadata-only hooks remain useful when Shadow retains a previously saved ledger.
    public sealed class ResidentialClassBuildingLifecycle : BuildingExtensionBase
    {
        public override void OnBuildingCreated(ushort id) { Clear(id); }
        public override void OnBuildingReleased(ushort id) { Clear(id); }
        public override void OnBuildingRelocated(ushort id) { Clear(id); }
        private static void Clear(ushort id)
        {
            EconomicBridge bridge = EconomicDemandExtension.Active;
            if (bridge != null && bridge.Classes != null) bridge.Classes.Released(id);
        }
    }

    internal static class ResidentialClassPatches
    {
        private const string Id = "SC4Evolution.ResidentialLotClass.v1";
        private static bool _installed;
        private static readonly object Sync = new object();
        private static readonly HashSet<EconomicBridge> Owners = new HashSet<EconomicBridge>();
        private static readonly MethodInfo Redevelopment = typeof(PrivateBuildingAI).GetMethod("SimulationStep", new Type[] { typeof(ushort), typeof(Building).MakeByRefType(), typeof(Building.Frame).MakeByRefType() });
        private static readonly MethodInfo Select = typeof(BuildingManager).GetMethod("GetRandomBuildingInfo", new Type[] { typeof(Randomizer).MakeByRefType(), typeof(ItemClass.Service), typeof(ItemClass.SubService), typeof(ItemClass.Level), typeof(int), typeof(int), typeof(BuildingInfo.ZoningMode), typeof(int) });
        private static readonly MethodInfo Create = typeof(BuildingManager).GetMethod("CreateBuilding", new Type[] { typeof(ushort).MakeByRefType(), typeof(Randomizer).MakeByRefType(), typeof(BuildingInfo), typeof(Vector3), typeof(float), typeof(int), typeof(uint) });
        private static readonly MethodInfo Upgrade = typeof(BuildingManager).GetMethod("UpdateBuildingInfo", new Type[] { typeof(ushort), typeof(Building).MakeByRefType(), typeof(BuildingInfo) });
        private static readonly MethodInfo NativeRoll = typeof(Randomizer).GetMethod("Int32", new Type[] { typeof(uint) });
        private static readonly MethodInfo HistoricalUpgrade = typeof(PrivateBuildingAI).GetMethod("BuildingUpgraded", new Type[] { typeof(ushort), typeof(Building).MakeByRefType() });
        private static readonly MethodInfo ResidentialPhase = typeof(ResidentialBuildingAI).GetMethod("SimulationStep", new Type[] { typeof(ushort), typeof(Building).MakeByRefType(), typeof(Building.Frame).MakeByRefType() });
        private static readonly MethodInfo ReleaseBuilding = typeof(BuildingManager).GetMethod("ReleaseBuilding", new Type[] { typeof(ushort) });
        private static readonly MethodInfo StartUpgrading = typeof(PrivateBuildingAI).GetMethod("StartUpgrading", new Type[] { typeof(ushort), typeof(Building).MakeByRefType() });
        [ThreadStatic] private static Attempt _current;
        private sealed class Attempt
        {
            internal ResidentialClassBridge Bridge;
            internal ResidentialLotIdentity OldIdentity;
            internal BuildingInfo Prefab;
            internal uint NewIndex;
            internal ulong OldGeneration;
            internal ResidentialClass Class;
            internal bool Pending;
            internal EconomicBridge Owner;
            internal ResidentialCapacityBridge Capacity;
            internal ResidentialCapacityIntent CapacityIntent;
            internal ResidentialCapacityAttempt CapacityAttempt;
            internal ResidentialCapacityMutation Mutation;
            internal int Session, Roll, Threshold;
            internal ushort OldBuilding;
            internal bool GateObserved;
            internal bool NativeAllowed { get { return Roll < Threshold; } }
        }

        internal static bool CapacityHooksInstalled(EconomicBridge owner)
        { lock (Sync) return _installed && Owners.Contains(owner) && HistoricalUpgrade != null && ResidentialPhase != null && ReleaseBuilding != null && StartUpgrading != null; }
        internal static void DisarmCapacity(ResidentialCapacityBridge capacity, string reason)
        { if (capacity != null) try { capacity.ConfirmHookSet(capacity.SessionEpoch, false, reason); } catch { } }
        internal static void InstallCapacityUpgradeHooks(Harmony harmony, MethodInfo update)
        {
            if (HistoricalUpgrade == null || StartUpgrading == null) throw new MissingMethodException("Historical residential upgrade boundary is unavailable.");
            ResidentialGrowthNativeBoundary.RequireStartUpgrading(StartUpgrading);
            harmony.Patch(update, prefix: ResidentialCapacityMutationHooks.Prefix(), finalizer: ResidentialCapacityMutationHooks.Finalizer());
            harmony.Patch(HistoricalUpgrade, prefix: ResidentialCapacityMutationHooks.Prefix(), finalizer: ResidentialCapacityMutationHooks.Finalizer());
            harmony.Patch(StartUpgrading, prefix: ResidentialCapacityMutationHooks.Prefix(), finalizer: ResidentialCapacityMutationHooks.Finalizer());
        }

        private static HarmonyMethod Hook(string name) { return new HarmonyMethod(typeof(ResidentialClassPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)); }
        private static ResidentialClassBridge Active()
        { EconomicBridge bridge = EconomicDemandExtension.Active; return bridge == null ? null : bridge.Classes; }

        internal static void Install(EconomicBridge owner)
        {
            lock (Sync)
            {
            if (_installed) { if (Owners.Add(owner)) ResidentialUpgradeIdentityPatches.Acquire(owner); return; }
            if (BuildConfig.applicationVersion != "1.21.1-f9" || Redevelopment == null || Select == null || Create == null || Upgrade == null)
                throw new InvalidOperationException("Required residential class boundaries differ from CS1 1.21.1-f9.");
            ResidentialGrowthNativeBoundary.RequireRedevelopment(Redevelopment);
            if (ResidentialPhase == null) throw new MissingMethodException("Residential phase observation boundary is unavailable.");
            ResidentialGrowthNativeBoundary.RequireResidentialPhase(ResidentialPhase);
            ResidentialGrowthNativeBoundary.RequireRelease(ReleaseBuilding);
            Harmony harmony = new Harmony(Id);
            ResidentialUpgradeIdentityPatches.Acquire(owner);
            try
            {
                // Validation and transpilation finish before effects can be published.
                harmony.Patch(Redevelopment, prefix: Hook("BeginAttempt"), transpiler: Hook("TranspileRedevelopment"), finalizer: Hook("EndAttempt"));
                harmony.Patch(Create, prefix: ResidentialCapacityCreationHooks.Prefix(), postfix: Hook("Created"), finalizer: ResidentialCapacityCreationHooks.Finalizer());
                harmony.Patch(ReleaseBuilding, prefix: ResidentialCapacityMutationHooks.ReleasePrefix(), finalizer: ResidentialCapacityMutationHooks.Finalizer());
                harmony.Patch(typeof(Citizen).GetMethod("SetHome", new Type[] { typeof(uint), typeof(ushort), typeof(uint) }), postfix: Hook("HomeChanged"));
                harmony.Patch(typeof(Citizen).GetMethod("SetHotel", new Type[] { typeof(uint), typeof(ushort), typeof(uint) }), postfix: Hook("HomeChanged"));
                harmony.Patch(typeof(CitizenUnit).GetMethod("SetWealthLevel", new Type[] { typeof(Citizen.Wealth) }), postfix: Hook("HomeUnitChanged"));
                harmony.Patch(ResidentialPhase, prefix: ResidentialCapacityMutationHooks.PhasePrefix(), finalizer: ResidentialCapacityMutationHooks.Finalizer());
                _installed = true;
                Owners.Add(owner);
            }
            catch { harmony.UnpatchAll(Id); ResidentialUpgradeIdentityPatches.Release(owner); throw; }
            }
        }
        internal static void Remove(EconomicBridge owner)
        {
            DisarmCapacity(owner == null ? null : owner.Capacity, "Residential admission hooks are being removed.");
            lock (Sync)
            {
                if (Owners.Remove(owner)) ResidentialUpgradeIdentityPatches.Release(owner);
                if (Owners.Count != 0) return;
                if (_installed) { new Harmony(Id).UnpatchAll(Id); _installed = false; }
                _current = null;
            }
        }

        private static void BeginAttempt(ushort buildingID, out Attempt __state)
        {
            __state = _current;
            EconomicBridge owner = EconomicDemandExtension.Active;
            _current = new Attempt { Bridge = Active(), Owner = owner, Capacity = owner == null ? null : owner.Capacity,
                Session = owner == null ? 0 : owner.DemandContextSession, OldBuilding = buildingID };
            if (_current.Capacity != null)
                try { _current.Mutation = _current.Capacity.BeginMutation(buildingID, ResidentialCapacityMutationKind.Phase); }
                catch (Exception error) { DisarmCapacity(_current.Capacity, "Redevelopment phase barrier failed: " + error.Message); }
        }
        private static Exception EndAttempt(Exception __exception, Attempt __state)
        {
            Attempt attempt = _current; _current = __state;
            if (attempt != null && attempt.Capacity != null)
            {
                try
                {
                    if (attempt.CapacityIntent != null)
                        try { attempt.Capacity.EndIntent(attempt.CapacityIntent); }
                        catch (Exception error) { DisarmCapacity(attempt.Capacity, "Redevelopment intent cleanup failed: " + error.Message); }
                }
                finally
                {
                    try { attempt.Capacity.EndMutation(attempt.Mutation, attempt.OldBuilding, __exception); }
                    catch (Exception error) { DisarmCapacity(attempt.Capacity, "Redevelopment phase finalization failed: " + error.Message); }
                }
            }
            return __exception;
        }
        private static bool CapacityCurrent(Attempt attempt)
        {
            return attempt != null && attempt.Owner != null && ReferenceEquals(attempt.Owner, EconomicDemandExtension.Active)
                && attempt.Session == attempt.Owner.DemandContextSession && ReferenceEquals(attempt.Capacity, attempt.Owner.Capacity)
                && attempt.Capacity != null && attempt.Capacity.SessionEpoch == attempt.Session;
        }
        private static bool AdmitRedevelopment(int roll, int threshold, ushort building, ref Building data, DistrictPolicies.CityPlanning policies)
        {
            Attempt attempt = _current;
            if (attempt == null) return roll < threshold;
            attempt.Roll = roll; attempt.Threshold = threshold; attempt.GateObserved = true;
            if (!CapacityCurrent(attempt) || attempt.OldBuilding != building || !attempt.Capacity.ModeRequested) return roll < threshold;
            if ((data.m_flags & Building.Flags.Collapsed) != 0 && (policies & DistrictPolicies.CityPlanning.NoRebuild) != 0) return roll < threshold;
            BuildingInfo info = data.Info;
            if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Residential) return roll < threshold;
            try
            {
                attempt.CapacityIntent = attempt.Capacity.BeginIntent(attempt.Owner.Published, roll, threshold, info.m_class.GetZone(), 0);
                if (attempt.CapacityIntent == null || attempt.CapacityIntent.Decision == ResidentialCapacityIntentDecision.NativeFallback) return roll < threshold;
                return attempt.CapacityIntent.Decision == ResidentialCapacityIntentDecision.Continue;
            }
            catch (Exception error) { DisarmCapacity(attempt.Capacity, "Redevelopment admission failed before native lookup: " + error.Message); return roll < threshold; }
        }

        private static IEnumerable<CodeInstruction> TranspileRedevelopment(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> body = new List<CodeInstruction>(instructions);
            int select = -1, create = -1, selections = 0, creations = 0, draw = -1;
            for (int i = 0; i < body.Count; i++)
            {
                if (Calls(body[i], Select)) { select = i; selections++; }
                if (Calls(body[i], Create)) { create = i; creations++; }
                if (Calls(body[i], NativeRoll) && i > 0 && (body[i - 1].opcode == OpCodes.Ldc_I4_S || body[i - 1].opcode == OpCodes.Ldc_I4)
                    && Convert.ToInt32(body[i - 1].operand) == 100) { if (draw != -1) throw new InvalidOperationException("Ambiguous redevelopment demand draw."); draw = i; }
            }
            if (selections != 1 || creations != 1 || select + 10 >= create)
                throw new InvalidOperationException("Expected exactly one ordered redevelopment selection/creation pair.");
            ValidateRedevelopmentDraw(body, draw, select);
            object policyLocal = null;
            for (int i = 0; i < draw; ++i) if (LocalIndex(body[i]) == 14) { policyLocal = body[i].operand; break; }
            if (policyLocal == null) throw new InvalidOperationException("Native NoRebuild policy local is unavailable.");
            // The supported native body null-tests the selected local before OR-ing
            // Demolishing into the old building. Reject any changed ordering/body.
            if (body[select + 1].opcode != OpCodes.Stloc_S || body[select + 2].opcode != OpCodes.Ldloc_S ||
                !Equals(body[select + 1].operand, body[select + 2].operand) ||
                (body[select + 3].opcode != OpCodes.Brfalse && body[select + 3].opcode != OpCodes.Brfalse_S) ||
                body[select + 4].opcode != OpCodes.Ldarg_2 || body[select + 5].opcode != OpCodes.Dup ||
                body[select + 6].opcode != OpCodes.Ldfld || ((FieldInfo)body[select + 6].operand).Name != "m_flags" ||
                body[select + 7].opcode != OpCodes.Ldc_I4 || !Equals(body[select + 7].operand, (int)Building.Flags.Demolishing) ||
                body[select + 8].opcode != OpCodes.Or || body[select + 9].opcode != OpCodes.Stfld ||
                !Equals(body[select + 6].operand, body[select + 9].operand))
                throw new InvalidOperationException("Redevelopment null-check-before-Demolishing guard failed.");
            Label exit = (Label)body[select + 3].operand;
            bool exitsAfterCreate = false;
            for (int i = create + 1; i < body.Count; i++)
                if (body[i].labels.Contains(exit) && body[i].opcode == OpCodes.Ret) exitsAfterCreate = true;
            if (!exitsAfterCreate || body[select].blocks.Count != 0) throw new InvalidOperationException("Redevelopment rejection exit changed.");
            body[create].opcode = OpCodes.Call;
            body[create].operand = typeof(ResidentialClassPatches).GetMethod("CommitRedevelopment", BindingFlags.Static | BindingFlags.NonPublic);
            CodeInstruction id = new CodeInstruction(OpCodes.Ldarg_1);
            id.labels.AddRange(body[select].labels); body[select].labels.Clear();
            body[select].opcode = OpCodes.Call;
            body[select].operand = typeof(ResidentialClassPatches).GetMethod("SelectRedevelopment", BindingFlags.Static | BindingFlags.NonPublic);
            body.Insert(select, id); body.Insert(select + 1, new CodeInstruction(OpCodes.Ldarg_2));
            CodeInstruction branch = body[draw + 2];
            CodeInstruction first = new CodeInstruction(OpCodes.Ldarg_1);
            first.labels.AddRange(branch.labels); branch.labels.Clear(); branch.opcode = OpCodes.Brfalse;
            body.InsertRange(draw + 2, new CodeInstruction[] { first, new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldloc_S, policyLocal),
                new CodeInstruction(OpCodes.Call, typeof(ResidentialClassPatches).GetMethod("AdmitRedevelopment", BindingFlags.Static | BindingFlags.NonPublic)) });
            return body;
        }
        private static bool Calls(CodeInstruction instruction, MethodInfo method)
        { return (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, method); }
        private static int LocalIndex(CodeInstruction instruction)
        {
            if (instruction.opcode != OpCodes.Ldloc_S && instruction.opcode != OpCodes.Ldloc && instruction.opcode != OpCodes.Stloc_S && instruction.opcode != OpCodes.Stloc) return -1;
            LocalBuilder builder = instruction.operand as LocalBuilder; if (builder != null) return builder.LocalIndex;
            LocalVariableInfo variable = instruction.operand as LocalVariableInfo; if (variable != null) return variable.LocalIndex;
            return Convert.ToInt32(instruction.operand);
        }
        private static void ValidateRedevelopmentDraw(List<CodeInstruction> body, int draw, int selection)
        {
            if (draw < 3 || draw + 7 >= selection || LocalIndex(body[draw - 3]) != 8 || body[draw - 2].opcode != OpCodes.Ldflda
                || ((FieldInfo)body[draw - 2].operand).DeclaringType != typeof(SimulationManager) || ((FieldInfo)body[draw - 2].operand).Name != "m_randomizer"
                || LocalIndex(body[draw + 1]) != 10 || (body[draw + 2].opcode != OpCodes.Bge && body[draw + 2].opcode != OpCodes.Bge_S)
                || LocalIndex(body[draw + 3]) != 9 || body[draw + 4].opcode != OpCodes.Ldfld || ((FieldInfo)body[draw + 4].operand).Name != "m_lastBuildIndex"
                || LocalIndex(body[draw + 5]) != 8 || body[draw + 6].opcode != OpCodes.Ldfld || ((FieldInfo)body[draw + 6].operand).Name != "m_currentBuildIndex"
                || (body[draw + 7].opcode != OpCodes.Bne_Un && body[draw + 7].opcode != OpCodes.Bne_Un_S)
                || !SameBranchTarget(body, body[draw + 2], body[draw + 7]))
                throw new InvalidOperationException("Native redevelopment roll/threshold/build-index guard differs.");
            for (int i = draw - 3; i <= draw + 7; ++i) if (body[i].blocks.Count != 0)
                throw new InvalidOperationException("Redevelopment gate crosses an unsupported exception region.");
        }
        private static bool SameBranchTarget(List<CodeInstruction> body, CodeInstruction first, CodeInstruction second)
        {
            // Harmony's MethodBodyReader creates a distinct label for each
            // branch, even when both labels belong to the same instruction.
            // Require one unambiguous destination per label, then compare the
            // actual target instruction rather than those per-branch label IDs.
            CodeInstruction firstTarget = UniqueBranchTarget(body, first.operand);
            CodeInstruction secondTarget = UniqueBranchTarget(body, second.operand);
            return firstTarget != null && ReferenceEquals(firstTarget, secondTarget);
        }
        private static CodeInstruction UniqueBranchTarget(List<CodeInstruction> body, object operand)
        {
            if (!(operand is Label)) return null;
            Label label = (Label)operand;
            CodeInstruction target = null;
            foreach (CodeInstruction instruction in body)
                foreach (Label defined in instruction.labels)
                    if (defined.Equals(label))
                    {
                        if (target != null) return null;
                        target = instruction;
                    }
            return target;
        }

        private static BuildingInfo SelectRedevelopment(BuildingManager manager, ref Randomizer randomizer,
            ItemClass.Service service, ItemClass.SubService subService, ItemClass.Level level, int width, int length,
            BuildingInfo.ZoningMode zoningMode, int style, ushort oldBuilding, ref Building oldData)
        {
            BuildingInfo prefab = manager.GetRandomBuildingInfo(ref randomizer, service, subService, level, width, length, zoningMode, style);
            Attempt attempt = _current;
            EconomicBridge owner = EconomicDemandExtension.Active;
            if (owner != null && owner.Capacity != null && owner.Capacity.ModeRequested && service == ItemClass.Service.Residential)
            {
                if (prefab == null) return null;
                if (!CapacityCurrent(attempt) || !attempt.GateObserved || attempt.CapacityIntent == null)
                    return attempt != null && attempt.GateObserved && !attempt.NativeAllowed ? null : prefab;
                int finalLength = length;
                if ((zoningMode == BuildingInfo.ZoningMode.CornerLeft && prefab.m_zoningMode == BuildingInfo.ZoningMode.CornerRight)
                    || (zoningMode == BuildingInfo.ZoningMode.CornerRight && prefab.m_zoningMode == BuildingInfo.ZoningMode.CornerLeft)) finalLength = width;
                try
                {
                    uint index = Singleton<SimulationManager>.instance.m_currentBuildIndex;
                    attempt.CapacityAttempt = attempt.Capacity.TryReserveRedevelopment(attempt.CapacityIntent, oldBuilding, prefab, oldData.m_position, finalLength, index);
                    if (attempt.CapacityAttempt == null || attempt.CapacityAttempt.Decision == ResidentialCapacityAttemptDecision.NativeFallback)
                        return attempt.NativeAllowed ? prefab : null;
                    if (attempt.CapacityAttempt.Decision != ResidentialCapacityAttemptDecision.Reserved) return null;
                    attempt.Prefab = prefab; attempt.NewIndex = index; attempt.Pending = true;
                    return prefab;
                }
                catch (Exception error) { DisarmCapacity(attempt.Capacity, "Redevelopment reservation failed before demolition: " + error.Message); return attempt.NativeAllowed ? prefab : null; }
            }
            if (attempt == null || attempt.Bridge == null || prefab == null || service != ItemClass.Service.Residential) return prefab;
            attempt.Pending = false;
            EconomicBridge bridge = EconomicDemandExtension.Active;
            if (bridge == null || !ReferenceEquals(bridge.Classes, attempt.Bridge)) return prefab;
            ResidentialClassChoice choice = attempt.Bridge.ChooseAt(bridge.Published, oldData.m_position);
            if (choice.Kind == ResidentialChoiceKind.NativeFallback) return prefab;
            if (choice.Kind == ResidentialChoiceKind.ControlledReject) return null;
            ResidentialLotIdentity identity;
            if (!ResidentialClassBridge.TryIdentity(oldBuilding, out identity) ||
                (oldData.m_flags & Building.Flags.Demolishing) != 0) return prefab;
            attempt.OldIdentity = identity; attempt.Prefab = prefab; attempt.Class = choice.Class;
            attempt.OldGeneration = attempt.Bridge.Generation(identity);
            attempt.NewIndex = Singleton<SimulationManager>.instance.m_currentBuildIndex; attempt.Pending = true;
            return prefab;
        }

        private static bool CommitRedevelopment(BuildingManager manager, out ushort building, ref Randomizer randomizer,
            BuildingInfo info, Vector3 position, float angle, int length, uint buildIndex)
        {
            Attempt attempt = _current;
            if (attempt != null && attempt.Pending && attempt.CapacityAttempt != null && attempt.CapacityAttempt.Decision == ResidentialCapacityAttemptDecision.Reserved)
                return CommitCapacityRedevelopment(manager, out building, ref randomizer, info, position, angle, length, buildIndex, attempt);
            bool accepted = attempt != null && attempt.Pending && ReferenceEquals(attempt.Prefab, info) && attempt.NewIndex == buildIndex &&
                attempt.OldIdentity != null &&
                attempt.OldIdentity.OriginXBits == ResidentialClassBridge.FloatBits(position.x) &&
                attempt.OldIdentity.OriginZBits == ResidentialClassBridge.FloatBits(position.z) &&
                attempt.Bridge.MatchesAttempt(attempt.OldIdentity, attempt.OldGeneration);
            // Commit-only: after Demolishing there is deliberately no new veto or RNG.
            building = 0;
            try
            {
                bool result = manager.CreateBuilding(out building, ref randomizer, info, position, angle, length, buildIndex);
                if (result && accepted)
                    try { attempt.Bridge.Commit(building, buildIndex, info, attempt.Class); }
                    catch (Exception error) { attempt.Bridge.Disable("Class commit failed after native redevelopment: " + error.Message); }
                return result;
            }
            finally
            {
                // The global create finalizer observes unbound/direct stock and
                // defers controlled stock until the reserved wrapper commits it.
                if (attempt != null) attempt.Pending = false;
            }
        }

        private static bool CommitCapacityRedevelopment(BuildingManager manager, out ushort building, ref Randomizer randomizer,
            BuildingInfo info, Vector3 position, float angle, int length, uint buildIndex, Attempt scope)
        {
            // Reservation already happened before native Demolishing. Never re-veto,
            // reselect, or consult current demand after this irreversible boundary.
            bool created = false; building = 0;
            try
            {
                ResidentialCapacityCreationContext.Frame marker = ResidentialCapacityCreationContext.Enter(scope.Owner, scope.Capacity, scope.CapacityAttempt);
                try { created = manager.CreateBuilding(out building, ref randomizer, info, position, angle, length, buildIndex); }
                finally { ResidentialCapacityCreationContext.Exit(marker); }
                if (created)
                {
                    bool marked = NewLotGrowthGate.MarkCapacityCreated(scope.Capacity, scope.CapacityAttempt);
                    try { if (marked && !scope.Capacity.CommitNative(scope.CapacityAttempt, building, buildIndex, info)) DisarmCapacity(scope.Capacity, "Native redevelopment requires capacity reconciliation."); }
                    catch (Exception error) { DisarmCapacity(scope.Capacity, "Redevelopment commit failed after native creation: " + error.Message); }
                }
                return created;
            }
            finally
            {
                scope.Pending = false;
                if (!created)
                {
                    if (NewLotGrowthGate.NativeBuildingExists(manager, building))
                    {
                        NewLotGrowthGate.MarkCapacityCreated(scope.Capacity, scope.CapacityAttempt);
                        DisarmCapacity(scope.Capacity, "Native redevelopment left a live out-ID without success; reconciliation required.");
                    }
                    else try { scope.Capacity.Cancel(scope.CapacityAttempt); } catch (Exception error) { DisarmCapacity(scope.Capacity, "Redevelopment cancellation failed: " + error.Message); }
                }
            }
        }

        internal static void ObserveCapacity(ResidentialCapacityBridge capacity, ushort building, Exception nativeError)
        {
            if (nativeError != null) DisarmCapacity(capacity, "Native residential mutation threw; capacity reconciliation required.");
            try { capacity.ObserveChanged(building); }
            catch (Exception error) { DisarmCapacity(capacity, "Residential mutation observation failed: " + error.Message); }
        }

        private static void BeforeUpgrade(ushort building, out ResidentialLotIdentity __state)
        { ResidentialClassBridge bridge = Active(); __state = bridge == null ? null : bridge.BeforeUpgrade(building); }
        private static void AfterUpgrade(ResidentialLotIdentity __state)
        { ResidentialClassBridge bridge = Active(); if (bridge != null) bridge.AfterUpgrade(__state); }
        private static void Created(bool __result, ushort building)
        { if (__result) Released(building); }
        internal static void Released(ushort building)
        {
            EconomicBridge owner = EconomicDemandExtension.Active;
            if (owner != null && owner.Capacity != null)
                try { owner.Capacity.BeforeRelease(building); } catch (Exception error) { DisarmCapacity(owner.Capacity, "Capacity release observation failed: " + error.Message); }
            ResidentialClassBridge bridge = Active(); if (bridge != null) bridge.Released(building);
        }
        private static void HomeChanged(ref Citizen __instance)
        { ResidentialClassBridge bridge = Active(); if (bridge != null) bridge.Mirror(ref __instance); }
        private static void HomeUnitChanged(ref CitizenUnit __instance)
        { ResidentialClassBridge bridge = Active(); if (bridge != null) bridge.MirrorUnit(ref __instance); }
    }
}
