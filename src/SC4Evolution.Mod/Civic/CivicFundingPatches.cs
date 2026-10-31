using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using ColossalFramework;
using HarmonyLib;
using SC4Evolution.Mod.Economy;
using UnityEngine;

namespace SC4Evolution.Mod.Civic
{
    internal static class CivicFundingNativeShape
    {
        private const string AssemblyHash = "DF557994A03C93D8BB4A14F80B1FBCC0FB29D547658442445D078D069BCDD9FE";
        internal delegate void EnsureDelegate(BuildingAI ai, ushort id, ref Building data, int home, int work, int visit, int student, int hotel);
        private static EnsureDelegate _ensure;
        internal static void Require()
        {
            if (BuildConfig.applicationVersion != "1.21.1-f9") throw new InvalidOperationException("Civic funding supports only installed CS1 1.21.1-f9.");
            using (FileStream stream = File.OpenRead(typeof(PlayerBuildingAI).Assembly.Location))
            using (SHA256 hash = SHA256.Create())
                if (BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "") != AssemblyHash) throw new InvalidOperationException("Civic native assembly hash differs.");
            MethodInfo method = typeof(BuildingAI).GetMethod("EnsureCitizenUnits", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new Type[] { typeof(ushort), typeof(Building).MakeByRefType(), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) }, null);
            if (method == null || method.ReturnType != typeof(void) || method.IsStatic) throw new MissingMethodException("EnsureCitizenUnits native signature differs.");
            _ensure = (EnsureDelegate)Delegate.CreateDelegate(typeof(EnsureDelegate), method);
        }
        internal static void EnsureVisits(BuildingAI ai, ushort id, ref Building data, int target)
        {
            if (_ensure == null) throw new InvalidOperationException("Civic native allocation was not prepared.");
            HospitalAI hospital = (HospitalAI)ai;
            _ensure(ai, id, ref data, 0, checked(hospital.m_workPlaceCount0 + hospital.m_workPlaceCount1 + hospital.m_workPlaceCount2 + hospital.m_workPlaceCount3), target, 0, 0);
        }
    }

    internal sealed class CivicFundingHookLease : IDisposable
    {
        internal readonly int Generation;
        private int _released;
        internal bool Released { get { return Interlocked.CompareExchange(ref _released, 0, 0) != 0; } }
        internal CivicFundingHookLease(int generation) { Generation = generation; }
        internal bool Current { get { return _released == 0 && CivicFundingPatches.Current(Generation); } }
        internal bool CanBind { get { return _released == 0 && CivicFundingPatches.CanBind(Generation); } }
        internal bool TryBind(CivicFundingBridge owner) { return CivicFundingPatches.TryBind(this, owner); }
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) CivicFundingPatches.Release(Generation); }
    }

    // No provider operation is performed under Sync. In-progress or reentrant
    // preparation is unavailable; callbacks can never wait for their own install.
    internal static class CivicFundingPatches
    {
        private const string Id = "SC4Evolution.CivicFunding.SC4LocalV1";
        private static readonly object Sync = new object();
        private static bool _installed, _changing, _faulted;
        private static int _leases, _generation, _inflight;
        private sealed class PatchPlan
        {
            internal MethodInfo Target;
            internal string Prefix, Postfix, Transpiler, Finalizer;
        }
        private sealed class Scope
        {
            internal CivicFundingBridge Owner;
            internal CivicFundingSettings Settings;
            internal int Session;
            internal BuildingManager Manager;
            internal Building[] Buffer;
            internal CivicFundingCycle Cycle;
            internal Scope Previous;
            internal bool Ended;
            internal bool Admitted;
            internal int Generation;
        }
        [ThreadStatic] private static Scope _scope;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static CivicFundingHookLease Acquire()
        {
            lock (Sync)
            {
                if (_changing || _faulted) throw new InvalidOperationException("Civic hook preparation is busy or cleanup previously failed.");
                if (_installed) { _leases++; return new CivicFundingHookLease(_generation); }
                _changing = true;
            }
            bool installed = false, patchAttempted = false;
            try
            {
                if (!GrowthGateDependency.Available()) throw new InvalidOperationException("Official CitiesHarmony is unavailable.");
                ResidentialCapacityProviderActivation activation; string reason;
                if (!ResidentialCapacityCompatibility.TryActivateOfficialProviderCopies(out activation, out reason) || activation == null)
                    throw new InvalidOperationException("Official provider copies could not be prepared: " + reason);
                List<PatchPlan> plans = Plans();
                foreach (PatchPlan plan in plans)
                {
                    Patches existing = Harmony.GetPatchInfo(plan.Target);
                    if (existing != null && existing.Owners.Count != 0) throw new InvalidOperationException("Civic target has unsupported existing patches: " + plan.Target);
                }
                Harmony harmony = new Harmony(Id);
                patchAttempted = true;
                foreach (PatchPlan plan in plans) harmony.Patch(plan.Target, Hook(plan.Prefix), Hook(plan.Postfix), Hook(plan.Transpiler), Hook(plan.Finalizer));
                installed = true;
                lock (Sync) { _installed = true; _leases = 1; _generation++; return new CivicFundingHookLease(_generation); }
            }
            catch
            {
                try { if (patchAttempted) new Harmony(Id).UnpatchAll(Id); }
                catch { lock (Sync) _faulted = true; }
                throw;
            }
            finally { lock (Sync) { _changing = false; if (!installed) _installed = false; } }
        }
        internal static bool Current(int generation) { lock (Sync) return _installed && !_changing && !_faulted && _generation == generation; }
        internal static bool CanBind(int generation) { lock (Sync) return _inflight == 0 && _installed && !_changing && !_faulted && _generation == generation; }
        internal static bool TryBind(CivicFundingHookLease lease, CivicFundingBridge owner)
        { lock (Sync) return !lease.Released && CanBind(lease.Generation) && _leases > 0 && CivicFundingBridge.TryBindActive(owner); }
        private static bool TryAdmit(CivicFundingBridge owner, int session, out int generation)
        {
            lock (Sync)
            {
                generation = _generation;
                if (!_installed || _changing || _faulted || !ReferenceEquals(CivicFundingBridge.Active, owner) || !owner.Ready || owner.Session != session) return false;
                _inflight = checked(_inflight + 1); return true;
            }
        }
        private static bool RetainBody(Scope parent)
        { lock (Sync) { if (!parent.Admitted || parent.Ended || parent.Owner == null || parent.Session != parent.Owner.Session || !_installed || parent.Generation != _generation || _inflight <= 0) return false; _inflight = checked(_inflight + 1); return true; } }
        private static void LeaveBody(int generation)
        { lock (Sync) { if (generation != _generation || _inflight <= 0) { _faulted = true; return; } _inflight--; } }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Release(int generation)
        {
            if (!DetachLease(generation)) return;
            try { new Harmony(Id).UnpatchAll(Id); }
            catch { lock (Sync) _faulted = true; throw; }
            finally { lock (Sync) _changing = false; }
        }
        private static bool DetachLease(int generation)
        {
            lock (Sync)
            {
                if (!_installed || generation != _generation || _leases == 0) return false;
                if (--_leases != 0) return false;
                // Do not unpatch an admitted/native body. Its finalizer only
                // decrements the count; the inert set can be reused by the next
                // outside-lock preparation/release. No waits or async cleanup.
                if (_inflight != 0) return false;
                _changing = true; _installed = false; return true;
            }
        }
        private static MethodInfo Method(Type type, string name, params Type[] args)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, args, null);
            if (method == null || method.IsStatic || method.GetMethodBody() == null) throw new MissingMethodException(type.FullName + "." + name);
            return method;
        }
        private static List<PatchPlan> Plans()
        {
            Type byBuilding = typeof(Building).MakeByRefType();
            List<PatchPlan> plans = new List<PatchPlan>();
            plans.Add(new PatchPlan { Target = Method(typeof(PlayerBuildingAI), "GetBudget", typeof(ushort), byBuilding), Prefix = "BudgetPrefix" });
            plans.Add(new PatchPlan { Target = Method(typeof(PlayerBuildingAI), "GetAverageBudget", typeof(ushort), byBuilding), Prefix = "BudgetPrefix" });
            plans.Add(new PatchPlan { Target = Method(typeof(PlayerBuildingAI), "SimulationStepActive", typeof(ushort), byBuilding, typeof(Building.Frame).MakeByRefType()), Prefix = "Begin", Transpiler = "Maintenance", Finalizer = "End" });
            plans.Add(new PatchPlan { Target = Method(typeof(PlayerBuildingAI), "GetFinalMaintenanceCost", typeof(ushort), byBuilding), Postfix = "FinalCost" });
            plans.Add(new PatchPlan { Target = Method(typeof(PlayerBuildingAI), "GetResourceRate", typeof(ushort), byBuilding, typeof(EconomyManager.Resource)), Postfix = "ResourceRate" });
            Type[] produce = { typeof(ushort), byBuilding, typeof(Building.Frame).MakeByRefType(), typeof(int), typeof(int), typeof(Citizen.BehaviourData).MakeByRefType(), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) };
            plans.Add(new PatchPlan { Target = Method(typeof(SchoolAI), "ProduceGoods", produce), Prefix = "BeginProduction", Transpiler = "SchoolProduction", Finalizer = "End" });
            plans.Add(new PatchPlan { Target = Method(typeof(HospitalAI), "ProduceGoods", produce), Prefix = "BeginProduction", Transpiler = "HospitalProduction", Finalizer = "End" });
            plans.Add(new PatchPlan { Target = Method(typeof(SchoolAI), "GetStudentCount", typeof(ushort), byBuilding, typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType()), Postfix = "Students" });
            foreach (Type type in new Type[] { typeof(SchoolAI), typeof(HospitalAI) })
                plans.Add(new PatchPlan { Target = Method(type, "GetCurrentRange", typeof(ushort), byBuilding), Prefix = "Range" });
            plans.Add(new PatchPlan { Target = Method(typeof(HospitalAI), "GetLocalizedStats", typeof(ushort), byBuilding), Transpiler = "HospitalStats" });
            foreach (string name in new string[] { "CreateBuilding", "BuildingLoaded", "EndRelocating" })
            {
                Type[] args = name == "BuildingLoaded" ? new Type[] { typeof(ushort), byBuilding, typeof(uint) } : new Type[] { typeof(ushort), byBuilding };
                plans.Add(new PatchPlan { Target = Method(typeof(HospitalAI), name, args), Transpiler = "HospitalUnits" });
            }
            return plans;
        }
        private static HarmonyMethod Hook(string name) { return name == null ? null : new HarmonyMethod(Helper(name)); }
        private static MethodInfo Helper(string name) { return typeof(CivicFundingPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic); }
        private static bool Settings(ushort id, ref Building data, out CivicFundingSettings settings)
        {
            CivicFundingBridge owner = CivicFundingBridge.Active;
            Scope scope = _scope;
            // An admitted native cycle completes its one cost/output mapping even
            // after Stop. New cycle entry never inherits this authority.
            if (Matches(scope, id, ref data))
            { settings = scope.Settings; return true; }
            // A declined/ineligible native cycle stays native even if a new
            // owner is activated before its later consumers execute.
            if (scope != null) { settings = null; return false; }
            settings = null; return owner != null && owner.TrySettings(id, ref data, out settings);
        }
        private static bool Matches(Scope scope, ushort id, ref Building data)
        {
            if (scope == null || scope.Settings == null || scope.Cycle == null || !CivicFundingBridge.OnSimulationThread() || !Singleton<BuildingManager>.exists) return false;
            CivicFundingBridge active = CivicFundingBridge.Active;
            if (active != null && !ReferenceEquals(active, scope.Owner)) return false;
            CivicFundingSettings settings = scope.Settings; BuildingInfo info = settings.Prefab;
            if (id == 0 || id != settings.Identity.BuildingId || data.m_buildIndex != settings.Identity.NativeBuildIndex ||
                (data.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created || data.m_eventIndex != 0 ||
                !ReferenceEquals(data.Info, info) || info == null || !ReferenceEquals(info.m_buildingAI, settings.Ai) || !ReferenceEquals(settings.Ai.m_info, info)) return false;
            BuildingManager manager = Singleton<BuildingManager>.instance;
            Building[] buffer = manager.m_buildings.m_buffer;
            bool eligible = ReferenceEquals(buffer, scope.Buffer) && id < buffer.Length && buffer[id].m_buildIndex == settings.Identity.NativeBuildIndex && ReferenceEquals(buffer[id].Info, info) &&
                (buffer[id].m_flags & (Building.Flags.Created | Building.Flags.Deleted)) == Building.Flags.Created && buffer[id].m_eventIndex == 0;
            return scope.Cycle.Allows(active, scope.Owner.Session, id, data.m_buildIndex, info.m_buildingAI, data.Info, settings.Ai.m_info, manager, buffer, eligible);
        }
        private static CivicFundingBridge Observer(CivicFundingSettings settings)
        { Scope scope = _scope; return scope != null && ReferenceEquals(scope.Settings, settings) ? scope.Owner : CivicFundingBridge.Active; }
        private static void Begin(PlayerBuildingAI __instance, ushort __0, ref Building __1, out Scope __state)
        {
            CivicFundingSettings settings; CivicFundingBridge owner = CivicFundingBridge.Active;
            __state = new Scope { Previous = _scope, Owner = owner, Session = owner == null ? 0 : owner.Session };
            _scope = __state;
            if (owner == null || !CivicFundingBridge.OnSimulationThread()) return;
            try
            {
                if (!owner.TrySettings(__0, ref __1, out settings) || !ReferenceEquals(settings.Ai, __instance)) return;
                int generation;
                if (!TryAdmit(owner, owner.Session, out generation)) return;
                _scope.Admitted = true; _scope.Generation = generation;
                _scope.Settings = settings; _scope.Manager = Singleton<BuildingManager>.instance; _scope.Buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                _scope.Cycle = new CivicFundingCycle(owner, owner.Session, __0, settings.Identity.NativeBuildIndex, settings.Ai, settings.Prefab, _scope.Manager, _scope.Buffer);
                owner.EnsureHospitalUnits(settings, __0, ref __1);
            }
            catch (Exception error) { _scope.Settings = null; owner.Suspend("Civic native scope unavailable: " + error.Message); }
        }
        private static void BeginProduction(PlayerBuildingAI __instance, ushort __0, ref Building __1, int __4, out Scope __state)
        {
            Scope parent = _scope;
            CivicFundingBridge active = CivicFundingBridge.Active;
            if (parent != null)
            {
                __state = new Scope { Previous = parent, Owner = parent.Owner, Session = parent.Session };
                _scope = __state;
                if ((active == null || ReferenceEquals(active, parent.Owner)) && Matches(parent, __0, ref __1) && ReferenceEquals(parent.Settings.Ai, __instance) && RetainBody(parent))
                { __state.Admitted = true; __state.Generation = parent.Generation; __state.Settings = parent.Settings; __state.Manager = parent.Manager; __state.Buffer = parent.Buffer; __state.Cycle = parent.Cycle; }
            }
            else Begin(__instance, __0, ref __1, out __state);
            Scope scope = _scope;
            if (scope != null && scope.Settings != null) scope.Owner.Record(scope.Settings, delegate(CivicFundingObservation o) { o.FinalProduction = __4; o.Capacity = CivicFundingMath.Capacity(scope.Settings, __4); o.ProductionCount = CivicFundingBridge.Increment(o.ProductionCount); });
        }
        private static Exception End(Exception __exception, Scope __state)
        {
            if (__state == null || __state.Ended) return __exception;
            __state.Ended = true;
            if (ReferenceEquals(_scope, __state)) _scope = __state.Previous;
            else if (__state.Owner != null) __state.Owner.Suspend("Civic scope nesting differs; control disabled.");
            if (__state.Admitted) LeaveBody(__state.Generation);
            if (__exception != null && __state.Owner != null && __state.Settings != null) __state.Owner.Suspend("Native civic simulation failed: " + __exception.Message);
            return __exception;
        }
        private static bool BudgetPrefix(PlayerBuildingAI __instance, ushort __0, ref Building __1, ref int __result)
        { CivicFundingSettings settings; if (!Settings(__0, ref __1, out settings) || !ReferenceEquals(settings.Ai, __instance)) return true; __result = 100; return false; }
        private static bool Range(PlayerBuildingAI __instance, ushort __0, ref Building __1, ref float __result)
        { CivicFundingSettings settings; if (!Settings(__0, ref __1, out settings) || !ReferenceEquals(settings.Ai, __instance)) return true; __result = CivicFundingMath.Range(settings, ref __1); return false; }
        private static void Students(PlayerBuildingAI __instance, ushort __0, ref Building __1, ref int __3)
        { CivicFundingSettings settings; if (Settings(__0, ref __1, out settings) && ReferenceEquals(settings.Ai, __instance)) __3 = CivicFundingMath.Capacity(settings, 100); }
        private static int Cost(int native, ushort id, ref Building data)
        {
            CivicFundingSettings settings; if (!Settings(id, ref data, out settings)) return native;
            int mapped = SC4Evolution.Simulation.CivicFundingEvaluator.Evaluate(settings.Identity.Archetype, settings.Capacity, settings.Coverage, native).MaintenanceCost;
            CivicFundingBridge owner = Observer(settings);
            if (owner != null) owner.Record(settings, delegate(CivicFundingObservation o) { o.NativeCost = native; o.RequestedCost = mapped; o.MaintenanceCount = CivicFundingBridge.Increment(o.MaintenanceCount); });
            return mapped;
        }
        private static int Fetch(EconomyManager manager, EconomyManager.Resource resource, int amount, ItemClass itemClass, ushort id, ref Building data)
        {
            // Exactly the original call, once. Its return still drives native affordability.
            int available = manager.FetchResource(resource, amount, itemClass);
            CivicFundingSettings settings;
            if (Settings(id, ref data, out settings)) { CivicFundingBridge owner = Observer(settings); if (owner != null) owner.Record(settings, delegate(CivicFundingObservation o) { o.RequestedCost = amount; o.Availability = available; o.FetchCount = CivicFundingBridge.Increment(o.FetchCount); }); }
            return available;
        }
        private static void FinalCost(PlayerBuildingAI __instance, ushort __0, ref Building __1, ref int __result)
        { CivicFundingSettings settings; if (Settings(__0, ref __1, out settings) && ReferenceEquals(settings.Ai, __instance)) __result = Estimate(__result, __0, ref __1); }
        private static int Estimate(int native, ushort id, ref Building data)
        { CivicFundingSettings settings; return Settings(id, ref data, out settings) ? SC4Evolution.Simulation.CivicFundingEvaluator.Evaluate(settings.Identity.Archetype, settings.Capacity, settings.Coverage, native).MaintenanceCost : native; }
        private static void ResourceRate(PlayerBuildingAI __instance, ushort __0, ref Building __1, EconomyManager.Resource __2, ref int __result)
        { CivicFundingSettings settings; if (__2 == EconomyManager.Resource.Maintenance && __result <= 0 && __result != int.MinValue && Settings(__0, ref __1, out settings) && ReferenceEquals(settings.Ai, __instance)) __result = -Estimate(-__result, __0, ref __1); }
        private static int Resource(ImmaterialResourceManager manager, ImmaterialResourceManager.Resource resource, int amount, Vector3 position, float radius, ushort id, ref Building data)
        {
            CivicFundingSettings settings;
            if (Settings(id, ref data, out settings))
            {
                amount = CivicFundingMath.ResourceAmount(amount, settings.AverageCapacity);
                radius = (float)(radius * settings.AverageEvaluation.CoverageFactor);
                CivicFundingBridge owner = Observer(settings);
                if (owner != null) { int a = amount; float r = radius; owner.Record(settings, delegate(CivicFundingObservation o) { o.ResourceAmount = a; o.ResourceRadius = r; }); }
            }
            return manager.AddResource(resource, amount, position, radius);
        }
        private static int SchoolCapacity(int native, ushort id, ref Building data, int finalProduction)
        { CivicFundingSettings settings; return Settings(id, ref data, out settings) ? CivicFundingMath.Capacity(settings, finalProduction) : native; }
        private static int PatientCapacity(int raw, ushort id, ref Building data)
        { CivicFundingSettings settings; return Settings(id, ref data, out settings) ? CivicFundingMath.Capacity(settings, 100) : raw; }
        private static int SafeRawCapacity(int raw, ushort id, ref Building data)
        { CivicFundingSettings settings; return raw == 0 && Settings(id, ref data, out settings) ? 1 : raw; }
        private static int Cure(int native, ushort id, ref Building data, int finalProduction)
        {
            CivicFundingSettings settings; if (!Settings(id, ref data, out settings)) return native;
            HospitalAI ai = (HospitalAI)settings.Ai;
            int result = CivicFundingMath.CureThreshold(settings.RawCapacity, ai.CuringRate, finalProduction, settings.Capacity);
            CivicFundingBridge owner = Observer(settings); if (owner != null) owner.Record(settings, delegate(CivicFundingObservation o) { o.Cure = result; }); return result;
        }
        private static int Ambulances(int native, ushort id, ref Building data, int finalProduction)
        {
            CivicFundingSettings settings; if (!Settings(id, ref data, out settings)) return native;
            int result = CivicFundingMath.AmbulanceTarget(((HospitalAI)settings.Ai).AmbulanceCount, finalProduction, settings.Capacity);
            CivicFundingBridge owner = Observer(settings); if (owner != null) owner.Record(settings, delegate(CivicFundingObservation o) { o.Ambulances = result; }); return result;
        }
        private static int NeutralStatBudget(int native, ushort id, ref Building data)
        { CivicFundingSettings settings; return Settings(id, ref data, out settings) ? 100 : native; }
        private static int StatsAmbulances(int native, ushort id, ref Building data, int finalProduction)
        {
            CivicFundingSettings settings; if (!Settings(id, ref data, out settings)) return native;
            CivicFundingBridge owner = Observer(settings); int? actual = owner == null ? null : owner.LastAmbulanceTarget(settings);
            return actual ?? CivicFundingMath.AmbulanceTarget(((HospitalAI)settings.Ai).AmbulanceCount, finalProduction, settings.Capacity);
        }

        private static bool Call(CodeInstruction instruction, Type type, string name)
        { MethodInfo method = instruction.operand as MethodInfo; return (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && method != null && method.DeclaringType == type && method.Name == name; }
        private static void Identity(List<CodeInstruction> result)
        { result.Add(new CodeInstruction(OpCodes.Ldarg_1)); result.Add(new CodeInstruction(OpCodes.Ldarg_2)); }
        private static void BeforeReplacement(List<CodeInstruction> result, CodeInstruction instruction)
        {
            CodeInstruction first = new CodeInstruction(OpCodes.Ldarg_1);
            first.labels.AddRange(instruction.labels); instruction.labels.Clear();
            first.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
            result.Add(first); result.Add(new CodeInstruction(OpCodes.Ldarg_2));
        }
        private static void After(List<CodeInstruction> result, string helper, bool finalProduction)
        { Identity(result); if (finalProduction) result.Add(new CodeInstruction(OpCodes.Ldarg_S, (byte)5)); result.Add(new CodeInstruction(OpCodes.Call, Helper(helper))); }
        private static IEnumerable<CodeInstruction> Maintenance(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> result = new List<CodeInstruction>(); int adjust = 0, fetch = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (Call(instruction, typeof(EconomyManager), "FetchResource"))
                { if (adjust != 1) throw new InvalidOperationException("Maintenance adjustment must precede the one fetch."); BeforeReplacement(result, instruction); instruction.operand = Helper("Fetch"); instruction.opcode = OpCodes.Call; fetch++; }
                result.Add(instruction);
                if (Call(instruction, typeof(PlayerBuildingAI), "AdjustMaintenanceCost")) { After(result, "Cost", false); adjust++; }
            }
            if (adjust != 1 || fetch != 1) throw new InvalidOperationException("Expected one adjusted maintenance amount and one native debit."); return result;
        }
        private static IEnumerable<CodeInstruction> SchoolProduction(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> result = new List<CodeInstruction>(); int resources = 0, capacity = 0, getters = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (Call(instruction, typeof(ImmaterialResourceManager), "AddResource")) { BeforeReplacement(result, instruction); instruction.operand = Helper("Resource"); instruction.opcode = OpCodes.Call; resources++; }
                result.Add(instruction);
                if (Call(instruction, typeof(SchoolAI), "get_StudentCount")) getters++;
                if (Call(instruction, typeof(Mathf), "Min")) { After(result, "SchoolCapacity", true); capacity++; }
            }
            if (resources != 3 || capacity != 1 || getters != 2) throw new InvalidOperationException("School resource/capacity shape differs."); return result;
        }
        private static IEnumerable<CodeInstruction> HospitalProduction(IEnumerable<CodeInstruction> instructions)
        { return HospitalBody(instructions, false); }
        private static IEnumerable<CodeInstruction> HospitalStats(IEnumerable<CodeInstruction> instructions)
        { return HospitalBody(instructions, true); }
        private static IEnumerable<CodeInstruction> HospitalBody(IEnumerable<CodeInstruction> instructions, bool stats)
        {
            List<CodeInstruction> body = new List<CodeInstruction>(instructions), result = new List<CodeInstruction>();
            int patients = 0, resources = 0, budgets = 0, cure = 0, ambulance = 0;
            int afterCure = -1, afterAmbulance = -1;
            for (int i = 0; i < body.Count; i++)
            {
                CodeInstruction instruction = body[i];
                if (Call(instruction, typeof(ImmaterialResourceManager), "AddResource")) { BeforeReplacement(result, instruction); instruction.operand = Helper("Resource"); instruction.opcode = OpCodes.Call; resources++; }
                result.Add(instruction);
                if (Call(instruction, typeof(HospitalAI), "get_PatientCapacity"))
                {
                    patients++; After(result, !stats && patients <= 2 ? "SafeRawCapacity" : "PatientCapacity", false);
                    if (!stats && patients == 2) { if (i + 1 >= body.Count || body[i + 1].opcode != OpCodes.Div) throw new InvalidOperationException("Raw cure denominator shape differs."); afterCure = i + 1; }
                }
                if (i == afterCure) { After(result, "Cure", true); cure++; }
                if (Call(instruction, typeof(HospitalAI), "get_AmbulanceCount") && ambulance == 0 && afterAmbulance < 0)
                {
                    for (int n = i + 1; n <= i + 6 && n < body.Count; n++) if (body[n].opcode == OpCodes.Div) { afterAmbulance = n; break; }
                    if (afterAmbulance < 0) throw new InvalidOperationException("Ambulance producer shape differs.");
                }
                if (i == afterAmbulance)
                {
                    Identity(result); result.Add(stats ? new CodeInstruction(OpCodes.Ldc_I4, 100) : new CodeInstruction(OpCodes.Ldarg_S, (byte)5));
                    result.Add(new CodeInstruction(OpCodes.Call, Helper(stats ? "StatsAmbulances" : "Ambulances"))); ambulance++;
                }
                if (Call(instruction, typeof(EconomyManager), "GetBudget")) { After(result, "NeutralStatBudget", false); budgets++; }
            }
            if (stats ? patients != 1 || resources != 0 || budgets != 1 || ambulance != 1 : patients != 6 || resources != 1 || budgets != 0 || cure != 1 || ambulance != 1)
                throw new InvalidOperationException("Hospital raw/effective capacity consumer shape differs.");
            return result;
        }
        private static IEnumerable<CodeInstruction> HospitalUnits(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> result = new List<CodeInstruction>(); int patients = 0;
            foreach (CodeInstruction instruction in instructions)
            { result.Add(instruction); if (Call(instruction, typeof(HospitalAI), "get_PatientCapacity")) { After(result, "PatientCapacity", false); patients++; } }
            if (patients != 1) throw new InvalidOperationException("Hospital native visit allocation shape differs."); return result;
        }
    }
}
