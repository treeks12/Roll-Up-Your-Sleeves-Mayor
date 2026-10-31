using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using SC4Evolution.Mod.Civic;
using SC4Evolution.Simulation;
internal static class Program
{
    private static int _checks;
    private static readonly Dictionary<ushort, OpCode> Ops = new Dictionary<ushort, OpCode>();
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static void Throws(Action action, string message)
    { bool thrown = false; try { action(); } catch { thrown = true; } Check(thrown, message); }
    private static byte[] Save(CivicFundingLedger ledger)
    { using (MemoryStream stream = new MemoryStream()) { CivicFundingLedgerCodec.Write(stream, ledger); return stream.ToArray(); } }
    private static bool Equal(byte[] a, byte[] b)
    { if (a == null || b == null) return a == b; if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; ++i) if (a[i] != b[i]) return false; return true; }
    private static void Field(object instance, string name, object value)
    { instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value); }
    private static CivicFundingSettings Settings(bool school, int raw, int? e, int? c, int day, int night, int current)
    {
        // Uninitialized managed AI type only; no Unity constructor, manager, AI
        // method or live observation is executed. Used only by integer math.
        BuildingAI ai = (BuildingAI)FormatterServices.GetUninitializedObject(school ? typeof(SchoolAI) : typeof(HospitalAI));
        return new CivicFundingSettings(new CivicFacilityIdentity(1, 0, school ? "fixture school" : "Medical Clinic", ai.GetType().FullName,
            school ? CivicFundingArchetype.ElementarySchool : CivicFundingArchetype.Clinic), ai, e, c, day, night, current, raw, 400);
    }
    private static void PureMath()
    {
        for (int p = 0; p <= 255; ++p)
        {
            CivicFundingSettings school = Settings(true, 100, null, null, p, p, p), hospital = Settings(false, 100, null, null, p, p, p);
            int units = p <= 100 ? 2 * p : p >= 120 ? 220 : p + 100;
            Check(CivicFundingMath.EfficiencyUnits(p) == units, "exact curve units");
            Check(CivicFundingMath.Capacity(school, 100) == (units + 1) / 2, "school exact ceiling");
            Check(CivicFundingMath.Capacity(hospital, 50) == (units + 1) / 2, "hospital capacity independent of final rate");
            Check(CivicFundingMath.ResourceAmount(100, p) == (units + 1) / 2, "resource exact ceiling");
            Check(CivicFundingMath.CureThreshold(100, 10, 100, p) == 5 * units, "raw cure denominator");
            Check(CivicFundingMath.AmbulanceTarget(100, 100, p) == (units + 1) / 2, "ambulance exact ceiling");
        }
        Check(CivicFundingMath.Capacity(Settings(true, 100, 120, null, 100, 100, 100), 100) == 110, "100*1.1 must not become111");
        Check(CivicFundingMath.Capacity(Settings(true, 110, 120, null, 100, 100, 100), 100) == 121, "110*1.1 exact121");
        Check(CivicFundingMath.Capacity(Settings(true, 100, 50, null, 100, 100, 100), 50) == 25, "current production multiplied once");
        Check(CivicFundingMath.Capacity(Settings(true, 100, 120, null, 100, 100, 100), 200) == 125, "native school structural ceiling");
        Check(CivicFundingMath.CureThreshold(0, 10, 100, 120) == 0, "zero raw capacity cure");
        Check(CivicFundingMath.ResourceAmount(1, 1) == 1, "explicit resource ceil conversion");
        Throws(delegate { CivicFundingMath.EfficiencyUnits(256); }, "unknown native budget must not clamp");
        CivicFundingSettings inherit = Settings(true, 100, null, null, 50, 150, 150);
        Check(inherit.Average == 87 && inherit.Capacity == 150 && inherit.AverageCapacity == 87, "independent current/weighted average");
        CivicFundingSettings one = Settings(true, 100, 20, null, 50, 150, 150);
        Check(one.Capacity == 20 && one.AverageCapacity == 20 && one.Coverage == 150 && one.AverageCoverage == 87, "one-axis override does not overwrite inheritance");
        CivicFundingSettings other = Settings(false, 100, null, 30, 150, 50, 50);
        Check(other.Capacity == 50 && other.AverageCapacity == 112 && other.Coverage == 30 && other.AverageCoverage == 30, "opposite axis inheritance");
        CivicFundingSettings liveWeights = new CivicFundingSettings(other.Identity, other.Ai, null, null, 50, 150, 150, 100, 400, 1, 1);
        Check(liveWeights.Average == 100 && liveWeights.AverageCapacity == 100, "actual native mutable day/night weights");
        Throws(delegate { new CivicFundingSettings(other.Identity, other.Ai, null, null, 50, 150, 150, 100, 400, 0, 0); }, "zero native day/night denominator rejected");
        Throws(delegate { new CivicFundingSettings(other.Identity, other.Ai, null, null, 255, 255, 255, 100, 400, int.MaxValue, 1); }, "native int weight overflow rejected");
        Building data = default(Building); data.m_productionRate = 100; data.m_flags = Building.Flags.Created | Building.Flags.Active;
        Check(CivicFundingMath.Range(Settings(true, 100, 0, 0, 100, 100, 100), ref data) == 200, "coverage zero retains source50percent radius");
        data.m_flags |= Building.Flags.RateReduced;
        Check(CivicFundingMath.Range(Settings(true, 100, 100, 100, 100, 100, 100), ref data) == 200, "range rate-reduced gate");
        data.m_flags |= Building.Flags.Evacuating;
        Check(CivicFundingMath.Range(Settings(true, 100, 100, 100, 100, 100, 100), ref data) == 0, "range evacuation gate");
    }
    private static void Retention()
    {
        CivicFundingLedger ledger = new CivicFundingLedger(new Guid("5fab3d32-9cc1-4658-872b-48230da07870"));
        CivicFacilityIdentity identity = new CivicFacilityIdentity(2, 0, "Medical Clinic", "HospitalAI", CivicFundingArchetype.Clinic);
        ledger.Set(identity, 20, 90); byte[] initial = Save(ledger);
        using (CivicFundingPreparation prep = CivicFundingBridge.Prepare(false, "unused"))
        {
            Check(!prep.Ready && !prep.Enabled, "disabled preparation cannot initialize a provider");
            CivicFundingBridge bridge = new CivicFundingBridge(); bridge.Start(initial, false, prep);
            Check(!bridge.Enabled && !bridge.Ready && Equal(initial, bridge.CaptureSave()), "disabled exact bytes");
            byte[] exposed = bridge.CaptureSave(); exposed[0] ^= 1;
            Check(Equal(initial, bridge.CaptureSave()), "save bytes defensive clone"); bridge.Stop(); bridge.ReleaseHooks();
        }
        byte[] malformed = { 1, 2, 3, 4, 5 };
        CivicFundingBridge bad = new CivicFundingBridge(); bad.Start(malformed, false, CivicFundingBridge.Prepare(false, "unused"));
        Check(Equal(malformed, bad.CaptureSave()), "malformed known bytes retained exactly");
        CivicFundingBridge unknown = new CivicFundingBridge(); unknown.Start(null, false, CivicFundingBridge.Prepare(false, "unused"), "native LoadData failed");
        Throws(delegate { unknown.CaptureSave(); }, "unknown read failure cannot become absent bytes");
        Check(unknown.Status.IndexOf("LoadData failed", StringComparison.Ordinal) >= 0 && unknown.Status.IndexOf("preserved", StringComparison.Ordinal) < 0, "unknown-read status honest");
        CivicFundingBridge fresh = new CivicFundingBridge(); fresh.Start(null, false, CivicFundingBridge.Prepare(false, "unused"));
        Check(fresh.CaptureSave() == null, "genuinely absent disabled payload");
        // Exercise the actual retention path with a valid, edited pure ledger.
        // This fixture does NOT claim that native preparation/control was active.
        CivicFundingBridge retry = new CivicFundingBridge();
        Field(retry, "_enabled", true); Field(retry, "_everActive", true); Field(retry, "_ledger", ledger); Field(retry, "_retained", initial); Field(retry, "_ready", true);
        ledger.Set(identity, 110, null); byte[] current = Save(ledger);
        retry.ReportPersistenceFailure("storage write failed");
        Check(!retry.Ready && Equal(current, retry.CaptureSave()) && !Equal(initial, retry.CaptureSave()), "failed storage retry retains current ledger rather than original");
        Check(Equal(current, retry.CaptureSave()), "failed storage retry stable");
        CivicFundingBridge noPreparation = new CivicFundingBridge(); noPreparation.Start(initial, true, null);
        Check(!noPreparation.Ready && Equal(initial, noPreparation.CaptureSave()), "missing warm lease preserves raw/failclosed");
    }
    private static void CyclePolicy()
    {
        object owner = new object(), ai = new object(), prefab = new object(), manager = new object(), buffer = new object();
        CivicFundingCycle cycle = new CivicFundingCycle(owner, 7, 4, 0, ai, prefab, manager, buffer);
        Check(cycle.Allows(owner, 7, 4, 0, ai, prefab, prefab, manager, buffer, true), "admitted cycle");
        Check(cycle.Allows(null, 7, 4, 0, ai, prefab, prefab, manager, buffer, true), "Stop detaches future entry but admitted cycle can finish");
        Check(!cycle.Allows(new object(), 7, 4, 0, ai, prefab, prefab, manager, buffer, true), "new owner cannot inherit old cycle");
        Check(!cycle.Allows(owner, 8, 4, 0, ai, prefab, prefab, manager, buffer, true), "session mismatch");
        Check(!cycle.Allows(owner, 7, 5, 0, ai, prefab, prefab, manager, buffer, true), "ID mismatch");
        Check(!cycle.Allows(owner, 7, 4, 1, ai, prefab, prefab, manager, buffer, true), "reused generation/index mismatch");
        Check(!cycle.Allows(owner, 7, 4, 0, new object(), prefab, prefab, manager, buffer, true), "AI replacement");
        Check(!cycle.Allows(owner, 7, 4, 0, ai, new object(), prefab, manager, buffer, true), "prefab replacement");
        Check(!cycle.Allows(owner, 7, 4, 0, ai, prefab, new object(), manager, buffer, true), "AI backpointer replacement");
        Check(!cycle.Allows(owner, 7, 4, 0, ai, prefab, prefab, new object(), buffer, true), "manager replacement");
        Check(!cycle.Allows(owner, 7, 4, 0, ai, prefab, prefab, manager, new object(), true), "buffer replacement/new city");
        Check(!cycle.Allows(owner, 7, 4, 0, ai, prefab, prefab, manager, buffer, false), "event/deleted/ineligible entry");
        Type patches = typeof(CivicFundingPatches); BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo begin = patches.GetMethod("Begin", flags), end = patches.GetMethod("End", flags), settings = patches.GetMethod("Settings", flags), production = patches.GetMethod("BeginProduction", flags);
        FieldInfo active = typeof(CivicFundingBridge).GetField("_active", flags), scopeField = patches.GetField("_scope", flags);
        object[] entry = { null, (ushort)4, default(Building), null }; begin.Invoke(null, entry);
        Check(scopeField.GetValue(null) != null, "declined cycle has an explicit sentinel");
        CivicFundingBridge later = new CivicFundingBridge(); active.SetValue(null, later);
        try
        {
            object[] query = { (ushort)4, default(Building), null };
            Check(!(bool)settings.Invoke(null, query), "owner becoming active mid-native-cycle cannot map its later outputs");
            object sentinel = scopeField.GetValue(null); object[] nested = { null, (ushort)4, default(Building), 100, null }; production.Invoke(null, nested);
            Check(!(bool)settings.Invoke(null, new object[] { (ushort)4, default(Building), null }), "declined parent keeps nested production native");
            end.Invoke(null, new object[] { null, nested[4] }); Check(ReferenceEquals(scopeField.GetValue(null), sentinel), "nested finalizer restores sentinel");
        }
        finally { active.SetValue(null, null); end.Invoke(null, new object[] { null, entry[3] }); }
        Check(scopeField.GetValue(null) == null, "native-cycle finalizer restores outside context");
        // The native eligibility branch supplies these bits to the pure predicate;
        // inspection additionally proves they precede the off-thread cache lookup.
        List<CodeInstruction> lookup = Read(typeof(CivicFundingBridge).GetMethod("TrySettings", BindingFlags.Instance | BindingFlags.NonPublic));
        int eventRead = -1, cacheCall = -1;
        for (int i = 0; i < lookup.Count; ++i)
        { FieldInfo f = lookup[i].operand as FieldInfo; MethodInfo m = lookup[i].operand as MethodInfo; if (f != null && f.Name == "m_eventIndex") eventRead = i; if (m != null && m.Name == "TryGetValue") { cacheCall = i; break; } }
        Check(eventRead >= 0 && eventRead < cacheCall, "event exclusion precedes cached settings");
    }
    private static void AdmissionCounters()
    {
        // Bookkeeping-only fixture: no lease here represents a native install.
        // No path which queries/patches a provider is invoked.
        Type patches = typeof(CivicFundingPatches); BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        Dictionary<string, object> before = new Dictionary<string, object>();
        foreach (string name in new string[] { "_installed", "_changing", "_faulted", "_leases", "_generation", "_inflight", "_scope" }) before[name] = patches.GetField(name, flags).GetValue(null);
        FieldInfo active = typeof(CivicFundingBridge).GetField("_active", flags); object oldActive = active.GetValue(null);
        try
        {
            patches.GetField("_installed", flags).SetValue(null, true); patches.GetField("_changing", flags).SetValue(null, false); patches.GetField("_faulted", flags).SetValue(null, false);
            patches.GetField("_leases", flags).SetValue(null, 1); patches.GetField("_generation", flags).SetValue(null, 77); patches.GetField("_inflight", flags).SetValue(null, 0);
            CivicFundingBridge owner = new CivicFundingBridge(); Field(owner, "_ready", true); Field(owner, "_session", 4); active.SetValue(null, owner);
            object[] admit = { owner, 4, 0 }; MethodInfo admission = patches.GetMethod("TryAdmit", flags);
            Check((bool)admission.Invoke(null, admit) && (int)admit[2] == 77, "real atomic admission bookkeeping");
            Check((int)patches.GetField("_inflight", flags).GetValue(null) == 1, "one admitted cycle");
            owner.Stop();
            Check(!(bool)admission.Invoke(null, new object[] { owner, 4, 0 }), "Stop forbids new old-owner admission");
            Check(!CivicFundingPatches.CanBind(77), "new owner cannot bind across old admitted cycle");
            CivicFundingPatches.Release(77);
            Check((bool)patches.GetField("_installed", flags).GetValue(null) && (int)patches.GetField("_leases", flags).GetValue(null) == 0, "last release leaves in-flight hooks resident/inert");
            Type scopeType = patches.GetNestedType("Scope", BindingFlags.NonPublic);
            object parent = Activator.CreateInstance(scopeType, true), child = Activator.CreateInstance(scopeType, true);
            foreach (object scope in new object[] { parent, child }) { Field(scope, "Owner", owner); Field(scope, "Session", 4); Field(scope, "Admitted", true); Field(scope, "Generation", 77); }
            Field(child, "Previous", parent); patches.GetField("_scope", flags).SetValue(null, child);
            Check((bool)patches.GetMethod("RetainBody", flags).Invoke(null, new object[] { parent }), "already admitted nested production retains cycle after Stop");
            patches.GetMethod("End", flags).Invoke(null, new object[] { null, child });
            Check((int)patches.GetField("_inflight", flags).GetValue(null) == 1, "nested completion decrements only its admission");
            patches.GetMethod("End", flags).Invoke(null, new object[] { null, child });
            Check((int)patches.GetField("_inflight", flags).GetValue(null) == 1, "finalizer token is single use");
            patches.GetMethod("End", flags).Invoke(null, new object[] { null, parent });
            Check(CivicFundingPatches.CanBind(77), "drain completes without provider operation");
            CivicFundingHookLease next = CivicFundingPatches.Acquire();
            Check(next.Current && next.CanBind, "next outside preparation can reuse inert set");
            CivicFundingBridge successor = new CivicFundingBridge();
            Check(next.TryBind(successor) && ReferenceEquals(CivicFundingBridge.Active, successor), "bind is atomic with no admitted cycles");
            Check(!(bool)patches.GetMethod("TryBind", flags).Invoke(null, new object[] { next, new CivicFundingBridge() }), "cannot replace an active owner");
            successor.Stop();
            Check((bool)patches.GetMethod("DetachLease", flags).Invoke(null, new object[] { 77 }), "next outside last release can detach after drain");
            Check(!(bool)patches.GetField("_installed", flags).GetValue(null), "detach precedes external unpatch operation");
            // Prevent Dispose from calling the external provider in this fixture.
            typeof(CivicFundingHookLease).GetField("_released", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(next, 1);
        }
        finally { foreach (KeyValuePair<string, object> item in before) patches.GetField(item.Key, flags).SetValue(null, item.Value); active.SetValue(null, oldActive); }
    }
    private sealed class Decoded { internal int Offset; internal CodeInstruction Instruction; }
    private static List<CodeInstruction> Read(MethodInfo method)
    {
        byte[] il = method.GetMethodBody().GetILAsByteArray(); int p = 0; List<Decoded> decoded = new List<Decoded>();
        DynamicMethod labelsOnly = new DynamicMethod("labels", typeof(void), Type.EmptyTypes); ILGenerator generator = labelsOnly.GetILGenerator();
        Dictionary<int, Label> labels = new Dictionary<int, Label>();
        while (p < il.Length)
        {
            int offset = p; ushort code = il[p++]; if (code == 0xfe) code = (ushort)(0xfe00 | il[p++]); OpCode op = Ops[code]; object operand = null;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineI: operand = (sbyte)il[p++]; break;
                case OperandType.ShortInlineVar: operand = il[p++]; break;
                case OperandType.InlineVar: operand = BitConverter.ToUInt16(il, p); p += 2; break;
                case OperandType.InlineI: operand = BitConverter.ToInt32(il, p); p += 4; break;
                case OperandType.InlineI8: operand = BitConverter.ToInt64(il, p); p += 8; break;
                case OperandType.ShortInlineR: operand = BitConverter.ToSingle(il, p); p += 4; break;
                case OperandType.InlineR: operand = BitConverter.ToDouble(il, p); p += 8; break;
                case OperandType.ShortInlineBrTarget: { int target = p + 1 + (sbyte)il[p]; p++; operand = LabelFor(labels, generator, target); break; }
                case OperandType.InlineBrTarget: { int target = p + 4 + BitConverter.ToInt32(il, p); p += 4; operand = LabelFor(labels, generator, target); break; }
                case OperandType.InlineSwitch:
                    int n = BitConverter.ToInt32(il, p); p += 4; int end = p + n * 4; Label[] switches = new Label[n];
                    for (int i = 0; i < n; ++i) { switches[i] = LabelFor(labels, generator, end + BitConverter.ToInt32(il, p)); p += 4; } operand = switches; break;
                case OperandType.InlineMethod: operand = method.Module.ResolveMethod(BitConverter.ToInt32(il, p)); p += 4; break;
                case OperandType.InlineField: operand = method.Module.ResolveField(BitConverter.ToInt32(il, p)); p += 4; break;
                case OperandType.InlineType: operand = method.Module.ResolveType(BitConverter.ToInt32(il, p)); p += 4; break;
                case OperandType.InlineString: operand = method.Module.ResolveString(BitConverter.ToInt32(il, p)); p += 4; break;
                case OperandType.InlineTok: operand = method.Module.ResolveMember(BitConverter.ToInt32(il, p)); p += 4; break;
                default: throw new Exception("Unsupported metadata operand " + op.OperandType);
            }
            decoded.Add(new Decoded { Offset = offset, Instruction = new CodeInstruction(op, operand) });
        }
        List<CodeInstruction> result = new List<CodeInstruction>();
        foreach (Decoded d in decoded) { Label label; if (labels.TryGetValue(d.Offset, out label)) d.Instruction.labels.Add(label); result.Add(d.Instruction); }
        if (method.DeclaringType.Assembly == typeof(BuildingManager).Assembly) Check(method.GetMethodBody().ExceptionHandlingClauses.Count == 0, "native proof target has no EH"); return result;
    }
    private static Label LabelFor(Dictionary<int, Label> labels, ILGenerator generator, int offset)
    { Label label; if (!labels.TryGetValue(offset, out label)) { label = generator.DefineLabel(); labels.Add(offset, label); } return label; }
    private static MethodInfo Native(Type type, string name)
    { foreach (MethodInfo method in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) if (method.Name == name) return method; throw new Exception("Native method absent"); }
    private static List<CodeInstruction> Transform(string name, List<CodeInstruction> body)
    { return new List<CodeInstruction>((IEnumerable<CodeInstruction>)typeof(CivicFundingPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { body })); }
    private static int CountCall(List<CodeInstruction> body, Type type, string name)
    { int n = 0; foreach (CodeInstruction instruction in body) { MethodBase m = instruction.operand as MethodBase; if (m != null && m.DeclaringType == type && m.Name == name) n++; } return n; }
    private static int Pop(StackBehaviour behaviour)
    {
        switch (behaviour)
        {
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
            case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8: case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
            case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_pop1: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8: case StackBehaviour.Popref_popi_popr4: case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref: return 3;
            default: throw new Exception("Unsupported pop " + behaviour);
        }
    }
    private static int Push(StackBehaviour behaviour)
    { if (behaviour == StackBehaviour.Push0) return 0; if (behaviour == StackBehaviour.Push1_push1) return 2; if (behaviour == StackBehaviour.Varpush) throw new Exception("Unresolved varpush"); return 1; }
    private static void StackProof(MethodInfo target, List<CodeInstruction> body)
    {
        Dictionary<Label, int> labels = new Dictionary<Label, int>(); for (int i = 0; i < body.Count; ++i) foreach (Label label in body[i].labels) labels[label] = i;
        Dictionary<int, int> heights = new Dictionary<int, int>(); Queue<int> work = new Queue<int>(); heights[0] = 0; work.Enqueue(0);
        while (work.Count != 0)
        {
            int i = work.Dequeue(), height = heights[i]; CodeInstruction instruction = body[i]; int pop, push;
            if (instruction.opcode == OpCodes.Ret) { pop = target.ReturnType == typeof(void) ? 0 : 1; push = 0; }
            else if (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Newobj)
            {
                MethodBase method = (MethodBase)instruction.operand; pop = method.GetParameters().Length;
                if (!method.IsStatic && instruction.opcode != OpCodes.Newobj) pop++;
                push = instruction.opcode == OpCodes.Newobj || (method is MethodInfo && ((MethodInfo)method).ReturnType != typeof(void)) ? 1 : 0;
            }
            else { pop = Pop(instruction.opcode.StackBehaviourPop); push = Push(instruction.opcode.StackBehaviourPush); }
            Check(height >= pop, "stack underflow at " + target.Name + ":" + i + " " + instruction.opcode);
            int next = height - pop + push;
            FlowControl flow = instruction.opcode.FlowControl;
            if (flow == FlowControl.Return || flow == FlowControl.Throw) { Check(next == 0, "terminal stack"); continue; }
            Label[] many = instruction.operand as Label[];
            if (many != null) foreach (Label label in many) Visit(labels[label], next, heights, work);
            else if (instruction.operand is Label) Visit(labels[(Label)instruction.operand], next, heights, work);
            if (flow != FlowControl.Branch && i + 1 < body.Count) Visit(i + 1, next, heights, work);
        }
    }
    private static void Visit(int position, int height, Dictionary<int, int> heights, Queue<int> work)
    { int seen; if (heights.TryGetValue(position, out seen)) { Check(seen == height, "branch stack merge"); return; } heights.Add(position, height); work.Enqueue(position); }
    private static void Metadata()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) if (field.FieldType == typeof(OpCode)) { OpCode op = (OpCode)field.GetValue(null); Ops[(ushort)op.Value] = op; }
        Type patches = typeof(CivicFundingPatches);
        string[] transformations = { "Maintenance", "SchoolProduction", "HospitalProduction", "HospitalStats", "HospitalUnits", "HospitalUnits", "HospitalUnits" };
        Type[] types = { typeof(PlayerBuildingAI), typeof(SchoolAI), typeof(HospitalAI), typeof(HospitalAI), typeof(HospitalAI), typeof(HospitalAI), typeof(HospitalAI) };
        string[] methods = { "SimulationStepActive", "ProduceGoods", "ProduceGoods", "GetLocalizedStats", "CreateBuilding", "BuildingLoaded", "EndRelocating" };
        for (int n = 0; n < transformations.Length; ++n)
        {
            MethodInfo native = Native(types[n], methods[n]); List<CodeInstruction> original = Read(native), transformed = Transform(transformations[n], original);
            StackProof(native, transformed);
            Check(CountCall(transformed, patches, n == 0 ? "Cost" : n == 1 ? "SchoolCapacity" : n == 2 ? "Cure" : n == 3 ? "NeutralStatBudget" : "PatientCapacity") == 1, "one canonical native consumer insertion");
            Check(CountCall(transformed, typeof(ColossalFramework.Math.Randomizer), "Int32") == CountCall(Read(native), typeof(ColossalFramework.Math.Randomizer), "Int32"), "native RNG call count unchanged");
            if (n == 0)
            {
                Check(CountCall(transformed, patches, "Fetch") == 1 && CountCall(transformed, typeof(EconomyManager), "FetchResource") == 0, "single real debit boundary wrapper");
                Check(CountCall(Read(patches.GetMethod("Fetch", BindingFlags.NonPublic | BindingFlags.Static)), typeof(EconomyManager), "FetchResource") == 1, "wrapper invokes original exactly once");
                Check(CountCall(transformed, typeof(PlayerBuildingAI), "GetProductionRate") == 2, "native production transforms remain, neutral budget is scoped");
            }
            if (n == 2) Check(CountCall(transformed, patches, "SafeRawCapacity") == 2 && CountCall(transformed, patches, "PatientCapacity") == 4, "two raw cure reads and four effective capacity consumers");
            if (n == 1 || n == 2)
            {
                int resources = n == 1 ? 3 : 1; Check(CountCall(transformed, patches, "Resource") == resources, "all real AddResource radii wrapped");
                List<CodeInstruction> malformed = Read(native); for (int i = 0; i < malformed.Count; ++i) if (malformed[i].operand is MethodInfo && ((MethodInfo)malformed[i].operand).Name == "AddResource") { malformed.RemoveAt(i); break; }
                int captured = n; Throws(delegate { Transform(transformations[captured], malformed); }, "shape mismatch rejects partial patch");
            }
        }
        List<CodeInstruction> labelsBody = Read(Native(typeof(SchoolAI), "ProduceGoods"));
        Label marker = new DynamicMethod("marker", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel();
        foreach (CodeInstruction i in labelsBody) if (i.operand is MethodInfo && ((MethodInfo)i.operand).Name == "AddResource") { i.labels.Add(marker); i.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock)); break; }
        List<CodeInstruction> changed = Transform("SchoolProduction", labelsBody); bool moved = false;
        for (int i = 0; i < changed.Count; ++i) if (changed[i].labels.Contains(marker)) moved = changed[i].opcode == OpCodes.Ldarg_1 && changed[i].blocks.Count == 1 && changed[i + 2].operand is MethodInfo && ((MethodInfo)changed[i + 2].operand).Name == "Resource";
        Check(moved, "call boundary labels/EH moved to first injected argument");
    }
    private static void Main()
    {
        PureMath(); Retention(); Metadata(); CyclePolicy(); AdmissionCounters();
        Console.WriteLine("PASS " + _checks + " civic checks: exact rational math, retention and actual native IL control-flow/stack. No provider activation, patch installation, Unity manager/AI execution or live gameplay proof.");
    }
}
