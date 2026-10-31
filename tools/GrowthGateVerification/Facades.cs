using System;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Economy
{
    public sealed class SegmentedDemandSnapshot
    {
        public bool Converged = true, GameplayEnabled = true;
        public int Demand = 20;
        public bool TryGetDemand(uint key, out int demand) { demand = Demand; return true; }
    }
    public sealed class EconomicBridge
    {
        internal ResidentialCapacityBridge Capacity = null;
        internal ResidentialClassBridge Classes = new ResidentialClassBridge();
        internal bool IsLotClassStage = true;
        internal int DemandContextSession = 7;
        internal IncomingDemandBinding DemandContextBinding = new IncomingDemandBinding();
        public SegmentedDemandSnapshot Published = new SegmentedDemandSnapshot();
        internal bool CanUseDemand(SegmentedDemandSnapshot snapshot) { return snapshot != null; }
    }
    internal sealed class ResidentialClassBridge
    {
        internal int Commits, Releases;
        internal Action OnReleased = null;
        internal ResidentialChoiceKind Choice = ResidentialChoiceKind.Selected;
        internal ResidentialClassChoice ChooseAt(SegmentedDemandSnapshot snapshot, Vector3 position) { return (ResidentialClassChoice)Activator.CreateInstance(typeof(ResidentialClassChoice), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new object[] { Choice, ResidentialClass.Low, ResidentialClassMask.All, ResidentialWaterEvidence.Unknown }, System.Globalization.CultureInfo.InvariantCulture); }
        internal void Commit(ushort id, uint index, BuildingInfo info, ResidentialClass selected) { Commits++; }
        internal void Disable(string reason) { }
        internal void Released(ushort id) { if (OnReleased != null) OnReleased(); Releases++; }
        internal void Mirror(ref Citizen citizen) { }
        internal void MirrorUnit(ref CitizenUnit unit) { }
        internal ResidentialLotIdentity BeforeUpgrade(ushort id) { return null; }
        internal void AfterUpgrade(ResidentialLotIdentity identity) { }
        internal ulong Generation(ResidentialLotIdentity identity) { return 1; }
        internal bool MatchesAttempt(ResidentialLotIdentity identity, ulong generation) { return true; }
        internal static bool TryIdentity(ushort id, out ResidentialLotIdentity identity) { identity = new ResidentialLotIdentity(id,0,0,0,"fixture",1,1,1,1); return true; }
        internal static int FloatBits(float value) { return BitConverter.ToInt32(BitConverter.GetBytes(value),0); }
    }
    internal enum ResidentialCapacityIntentDecision { NativeFallback, ControlledReject, Continue }
    internal enum ResidentialCapacityAttemptDecision { NativeFallback, ControlledReject, Reserved }
    internal sealed class ResidentialCapacityIntent { internal ResidentialCapacityIntentDecision Decision; }
    internal sealed class ResidentialCapacityAttempt { internal ResidentialCapacityAttemptDecision Decision; internal ResidentialClass Class; internal bool CreateScopeClaimed; }
    internal enum ResidentialCapacityMutationKind { Critical=0, Phase=1 }
    internal sealed class ResidentialCapacityMutation
    {
        internal ResidentialCapacityBridge Owner;
        internal int Epoch, Thread;
        internal bool Ended, Deferred;
        internal ResidentialCapacityMutationKind Kind;
    }
    internal sealed class ResidentialCapacityBridge
    {
        internal bool ModeRequested = true, Ready = true;
        internal int SessionEpoch = 7;
        internal ResidentialCapacityIntentDecision IntentDecision = ResidentialCapacityIntentDecision.Continue;
        internal ResidentialCapacityAttemptDecision AttemptDecision = ResidentialCapacityAttemptDecision.Reserved;
        internal int Begins, Ends, Reserves, Marks, Commits, Cancels, Releases, Observations;
        internal int MutationBegins, MutationEnds, PendingMutations, CriticalMutations, UnresolvedMutations, DeferredCreates, ClaimedCreates;
        internal ResidentialCapacityAttempt LastCreateAttempt;
        internal bool ThrowCommit = false;
        internal ResidentialCapacityIntent BeginIntent(SegmentedDemandSnapshot snapshot,int roll,int threshold,ItemClass.Zone zone,ushort block) { Begins++; return new ResidentialCapacityIntent { Decision = Ready && CriticalMutations == 0 && (zone==ItemClass.Zone.ResidentialLow || zone==ItemClass.Zone.ResidentialHigh) ? IntentDecision : ResidentialCapacityIntentDecision.NativeFallback }; }
        internal ResidentialCapacityAttempt TryReserveNew(ResidentialCapacityIntent intent,BuildingInfo info,Vector3 position,int length,uint index) { Reserves++; return new ResidentialCapacityAttempt { Decision=intent.Decision==ResidentialCapacityIntentDecision.Continue?AttemptDecision:ResidentialCapacityAttemptDecision.NativeFallback, Class=ResidentialClass.Low }; }
        internal ResidentialCapacityAttempt TryReserveRedevelopment(ResidentialCapacityIntent intent,ushort predecessor,BuildingInfo info,Vector3 position,int length,uint index) { return TryReserveNew(intent,info,position,length,index); }
        internal void MarkNativeCreated(ResidentialCapacityAttempt attempt) { Marks++; }
        internal bool CommitNative(ResidentialCapacityAttempt attempt,ushort id,uint index,BuildingInfo info) { Commits++; if(ThrowCommit) throw new Exception("commit"); if (DeferredCreates > 0) DeferredCreates--; return true; }
        internal void Cancel(ResidentialCapacityAttempt attempt) { Cancels++; }
        internal void EndIntent(ResidentialCapacityIntent intent) { Ends++; }
        internal void ObserveChanged(ushort id) { Observations++; }
        internal void BeforeRelease(ushort id) { Releases++; }
        internal void ConfirmHookSet(int epoch,bool complete,string reason) { Ready=complete; }
        // These are test facades for hook ordering, not the real ledger/audit/
        // publication implementation, which has its own integration verification.
        internal ResidentialCapacityMutation BeginMutation(ushort building,ResidentialCapacityMutationKind kind=ResidentialCapacityMutationKind.Critical)
        {
            if (!ModeRequested) return null;
            MutationBegins++; PendingMutations++; if (kind==ResidentialCapacityMutationKind.Critical) CriticalMutations++;
            return new ResidentialCapacityMutation { Owner=this,Epoch=SessionEpoch,Thread=System.Threading.Thread.CurrentThread.ManagedThreadId,Kind=kind };
        }
        internal ResidentialCapacityMutation BeginCreateMutation(ResidentialCapacityAttempt attempt)
        {
            LastCreateAttempt=attempt;
            bool deferred=attempt!=null&&!attempt.CreateScopeClaimed;
            if(deferred){attempt.CreateScopeClaimed=true;ClaimedCreates++;}
            var token=BeginMutation(0);if(token!=null)token.Deferred=deferred;return token;
        }
        internal void EndMutation(ResidentialCapacityMutation token,ushort id,Exception error)
        {
            if(token==null||token.Ended||!ReferenceEquals(token.Owner,this)||token.Epoch!=SessionEpoch||token.Thread!=System.Threading.Thread.CurrentThread.ManagedThreadId)return;
            token.Ended=true;MutationEnds++;
            if(error!=null){UnresolvedMutations++;Ready=false;return;}
            PendingMutations--;if(token.Kind==ResidentialCapacityMutationKind.Critical)CriticalMutations--;
            if(token.Deferred){if(id!=0)DeferredCreates++;}else if(id!=0)Observations++;
        }
    }
    internal sealed class IncomingDemandBinding { }
    internal static class IncomingResidentDemandContext
    {
        internal static bool CanAggregate(IncomingDemandBinding binding,EconomicBridge owner,int epoch) { return true; }
        internal static void RecordCallback(IncomingDemandBinding binding,int original,int result,bool aggregated) { }
    }
    internal static class CityCensus { internal static int SegmentFor(ItemClass info,ItemClass.Level level) { return -1; } }
    internal static class ResidentialCapacityCompatibility
    {
        internal sealed class MethodPin { internal MethodPin(string a,string t,string n,string s,string il,string body,int length) { } }
        internal sealed class Observation { internal bool Known=true; }
        internal static Observation ObserveMethod(System.Reflection.MethodBase method,MethodPin pin) { return new Observation(); }
    }
}
