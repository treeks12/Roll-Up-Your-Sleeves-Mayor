using System;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace SC4Evolution.Mod.Economy
{
    public static class GrowthGateBehaviorChecks
    {
        private static int _checks;
        private static object Call(Type type,string name,object[] args)
        { try { return type.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args); } catch(TargetInvocationException e){throw e.InnerException;} }
        private static void Check(bool condition,string name) { if(!condition)throw new Exception(name);_checks++; }
        private static EconomicBridge Owner()
        {
            var owner=new EconomicBridge();owner.Capacity=new ResidentialCapacityBridge();EconomicDemandExtension.Bind(owner);
            Singleton<SimulationManager>.instance=new SimulationManager();return owner;
        }
        private static object Begin(Type type,ushort id) { var args=new object[]{id,null};Call(type,"BeginAttempt",args);return args[1]; }
        private static void End(Type type,object previous,Exception error=null) { Check(ReferenceEquals(Call(type,"EndAttempt",new object[]{error,previous}),error),"native exception identity"); }
        private static bool Gate(int roll,int threshold,ItemClass.Zone zone=ItemClass.Zone.ResidentialLow)
        { return (bool)Call(typeof(NewLotGrowthGate),"AdmitCandidate",new object[]{roll,threshold,zone,(ushort)3}); }
        private static bool Create(BuildingManager manager,out ushort id)
        { Randomizer r=new Randomizer();return NewLotGrowthGate.CreateEligibleBuilding(manager,out id,ref r,new BuildingInfo(),new Vector3(),0,4,0); }
        // The fake native call explicitly invokes the REAL production callback
        // pair. Harmony itself is never loaded/executed by this behavior harness.
        public static object BeginGlobalCreate(BuildingManager manager)
        { var args=new object[]{manager,null};Call(typeof(ResidentialCapacityCreationHooks),"Begin",args);return args[1]; }
        public static void EndGlobalCreate(object state,ushort id,bool result,Exception error)
        { Check(ReferenceEquals(Call(typeof(ResidentialCapacityCreationHooks),"End",new object[]{id,result,error,state}),error),"global finalizer native exception identity"); }
        private static object BeginMutation(string name,ushort id)
        { var args=new object[]{id,null};Call(typeof(ResidentialCapacityMutationHooks),name,args);return args[1]; }
        private static void EndMutation(object state,Exception error=null)
        { Check(ReferenceEquals(Call(typeof(ResidentialCapacityMutationHooks),"End",new object[]{error,state}),error),"mutation finalizer native exception identity"); }
        public static int Run()
        {
            _checks=0;
            foreach(int threshold in new[]{int.MinValue,-1,0,1,50,99,100,101,int.MaxValue})
                for(int roll=0;roll<100;roll++)
                {
                    var owner=Owner();owner.Capacity.ModeRequested=false;object previous=Begin(typeof(NewLotGrowthGate),3);
                    Check(Gate(roll,threshold)==(roll<threshold),"legacy native comparison");End(typeof(NewLotGrowthGate),previous);
                    owner=Owner();owner.Capacity.Ready=false;previous=Begin(typeof(NewLotGrowthGate),3);
                    Check(Gate(roll,threshold)==(roll<threshold),"not-ready exact native comparison");End(typeof(NewLotGrowthGate),previous);
                    owner=Owner();previous=Begin(typeof(NewLotGrowthGate),3);
                    Check(Gate(roll,threshold,ItemClass.Zone.CommercialLow)==(roll<threshold),"nonres native comparison");End(typeof(NewLotGrowthGate),previous);
                }
            foreach(bool nativePass in new[]{false,true})
            {
                var owner=Owner();owner.Capacity.AttemptDecision=ResidentialCapacityAttemptDecision.NativeFallback;
                object previous=Begin(typeof(NewLotGrowthGate),3);Check(Gate(nativePass?0:99,1),"RAW continuation");var manager=new BuildingManager();ushort id;
                Check(Create(manager,out id)==nativePass,"prospective fallback result");Check(manager.Calls==(nativePass?1:0)&&owner.Capacity.Commits==0&&owner.Classes.Commits==0,"fallback no controlled commit");
                End(typeof(NewLotGrowthGate),previous);
            }
            foreach(int behavior in new[]{0,1,2,3})
            {
                var owner=Owner();object previous=Begin(typeof(NewLotGrowthGate),3);Check(Gate(99,0),"controlled continuation");
                var manager=new BuildingManager{Behavior=behavior};ushort id=0;Exception error=null;
                try { bool result=Create(manager,out id);Check(result==(behavior==0),"native result preserved"); }catch(Exception e){error=e;Check(behavior==2||behavior==3,"native exception preserved");}
                Check(manager.Calls==1,"one native create");
                Check(owner.Capacity.Marks==((behavior==0||behavior==3)?1:0),"physical mark");Check(owner.Capacity.Cancels==((behavior==1||behavior==2)?1:0),"reservation cancel only absent native stock");
                Check(owner.Capacity.Commits==(behavior==0?1:0)&&owner.Classes.Commits==0,"only capacity owns class commit");End(typeof(NewLotGrowthGate),previous,error);
            }
            {
                var owner=Owner();owner.Capacity.AttemptDecision=ResidentialCapacityAttemptDecision.NativeFallback;var previous=Begin(typeof(NewLotGrowthGate),3);Check(Gate(0,1),"fallback throw native pass");
                var manager=new BuildingManager{Behavior=3};ushort id;Exception error=null;try{Create(manager,out id);}catch(Exception e){error=e;}
                Check(error!=null&&!owner.Capacity.Ready&&owner.Capacity.UnresolvedMutations==1&&owner.Capacity.Commits==0,"fallback allocated exception retains unresolved mutation");End(typeof(NewLotGrowthGate),previous,error);
            }
            {
                var owner=Owner();owner.Capacity.ThrowCommit=true;var previous=Begin(typeof(NewLotGrowthGate),3);Gate(99,0);var manager=new BuildingManager();ushort id;
                Check(Create(manager,out id)&&!owner.Capacity.Ready&&owner.Capacity.Marks==1&&owner.Capacity.Cancels==0,"commit failure retains physical success");End(typeof(NewLotGrowthGate),previous);
            }
            {
                var owner=Owner();var outer=Begin(typeof(NewLotGrowthGate),3);Gate(99,0);var manager=new BuildingManager();manager.DuringCreate=delegate {
                    var inner=Begin(typeof(NewLotGrowthGate),3);Check(!Gate(99,0),"critical native create blocks reentrant admission");var child=new BuildingManager();ushort id;Check(!Create(child,out id)&&child.Calls==0,"nested rejected native result");End(typeof(NewLotGrowthGate),inner);
                };ushort created;Check(Create(manager,out created),"outer result");End(typeof(NewLotGrowthGate),outer);
                Check(owner.Capacity.Marks==1&&owner.Capacity.Commits==1&&owner.Capacity.Ends==2,"nested intents and finalizers");
            }
            {
                var owner=Owner();var previous=Begin(typeof(NewLotGrowthGate),3);Gate(99,0);owner.DemandContextSession++;var manager=new BuildingManager();ushort id;
                Check(!Create(manager,out id)&&manager.Calls==0&&owner.Capacity.Reserves==0,"late epoch fallback same original roll");End(typeof(NewLotGrowthGate),previous);
            }
            {
                var owner=Owner();owner.Capacity.ModeRequested=false;var manager=new BuildingManager();ushort id;Check(Create(manager,out id)&&owner.Classes.Commits==1,"legacy class commit retained");
                owner=Owner();owner.Capacity.Ready=false;Check(new EconomicDemandExtension().OnCalculateResidentialDemand(73)==73,"requested-not-ready native demand");
                owner.Capacity.ModeRequested=false;Check(new EconomicDemandExtension().OnCalculateResidentialDemand(73)==2,"legacy incoming output retained");
            }
            {
                var owner=Owner();var previous=Begin(typeof(ResidentialClassPatches),5);var data=new Building{Info=new BuildingInfo(),m_flags=Building.Flags.Created|Building.Flags.Collapsed};
                Check(!(bool)Call(typeof(ResidentialClassPatches),"AdmitRedevelopment",new object[]{0,0,(ushort)5,data,DistrictPolicies.CityPlanning.NoRebuild})&&owner.Capacity.Begins==0,"explicit NoRebuild retained");
                Check((bool)Call(typeof(ResidentialClassPatches),"AdmitRedevelopment",new object[]{99,0,(ushort)5,data,DistrictPolicies.CityPlanning.None}),"zero native demand is not NoRebuild");
                var manager=new BuildingManager();var selected=Call(typeof(ResidentialClassPatches),"SelectRedevelopment",new object[]{manager,new Randomizer(),ItemClass.Service.Residential,ItemClass.SubService.None,ItemClass.Level.Level1,4,4,BuildingInfo.ZoningMode.Straight,0,(ushort)5,data});
                Check(selected!=null&&owner.Capacity.Reserves==1&&(data.m_flags&Building.Flags.Demolishing)==0,"reserve before demolition");
                owner.Capacity.Ready=false;Call(typeof(ResidentialClassPatches),"Released",new object[]{(ushort)5});
                var args=new object[]{manager,(ushort)0,new Randomizer(),selected,new Vector3(),0f,4,0u};Check((bool)Call(typeof(ResidentialClassPatches),"CommitRedevelopment",args),"late commit-only native result");
                Check(manager.Calls==1&&owner.Capacity.Marks==1&&owner.Capacity.Commits==1&&owner.Capacity.Releases==1,"replacement reservation survives predecessor release");End(typeof(ResidentialClassPatches),previous);
            }
            Check(ResidentialCapacityMutationHooks.Prefix().method.DeclaringType!=typeof(ResidentialClassPatches),"Harmony state keyed by distinct patch type");
            {
                var owner=Owner();object phase=BeginMutation("BeginPhase",5);
                Check(owner.Capacity.PendingMutations==1&&owner.Capacity.CriticalMutations==0,"residential phase opens save barrier only");
                object previous=Begin(typeof(ResidentialClassPatches),5);
                Check(owner.Capacity.PendingMutations==2&&owner.Capacity.CriticalMutations==0,"private simulation opens nested phase barrier");
                Check(GateWithFreshScope(),"own phase admission remains available");
                End(typeof(ResidentialClassPatches),previous);EndMutation(phase);
                Check(owner.Capacity.PendingMutations==0&&owner.Capacity.MutationBegins==2&&owner.Capacity.MutationEnds==2,"nested phase barriers balance");
            }
            {
                var owner=Owner();bool barrierBeforeClass=false;
                owner.Classes.OnReleased=delegate {barrierBeforeClass=owner.Capacity.PendingMutations==1&&owner.Capacity.CriticalMutations==1;};
                object state=BeginMutation("BeginRelease",5);
                Check(barrierBeforeClass&&owner.Capacity.Releases==1&&owner.Classes.Releases==1,"release barrier precedes capacity/class removal");
                object previous=Begin(typeof(NewLotGrowthGate),3);Check(!Gate(99,0),"release critical blocks reentrant admission");End(typeof(NewLotGrowthGate),previous);
                EndMutation(state);EndMutation(state);Check(owner.Capacity.MutationEnds==1&&owner.Capacity.PendingMutations==0,"scope finalizer idempotence");
            }
            {
                var owner=Owner();object state=BeginMutation("Begin",5);var foreign=Owner();EndMutation(state);
                Check(owner.Capacity.PendingMutations==0&&foreign.Capacity.MutationEnds==0,"finalizer returns token to captured owner across active bridge change");
                state=BeginMutation("Begin",5);foreign.Capacity.SessionEpoch++;EndMutation(state);
                Check(foreign.Capacity.PendingMutations==1&&foreign.Capacity.MutationEnds==0,"old epoch cannot decrement a new epoch scope");
            }
            {
                var owner=Owner();object state=BeginMutation("Begin",5);Exception error=new Exception("native mutation partial");EndMutation(state,error);
                Check(!owner.Capacity.Ready&&owner.Capacity.PendingMutations==1&&owner.Capacity.UnresolvedMutations==1,"partial mutation remains unresolved without refund");
                state=BeginMutation("Begin",6);var thread=new System.Threading.Thread(delegate(){EndMutation(state);});thread.Start();thread.Join();
                Check(owner.Capacity.PendingMutations==2&&owner.Capacity.MutationEnds==1,"foreign thread cannot end token");EndMutation(state);
                Check(owner.Capacity.PendingMutations==1&&owner.Capacity.MutationEnds==2,"original thread ends its own token");
            }
            {
                var owner=Owner();var previous=Begin(typeof(NewLotGrowthGate),3);Gate(99,0);var manager=new BuildingManager();
                manager.DuringCreate=delegate {
                    Check(owner.Capacity.LastCreateAttempt!=null&&owner.Capacity.ClaimedCreates==1&&owner.Capacity.CriticalMutations==1,"reserved marker reaches global create barrier");
                    Check(owner.Capacity.DeferredCreates==0&&owner.Capacity.Observations==0,"controlled stock not observed before create finalizer");
                    var child=new BuildingManager();Randomizer random=new Randomizer();ushort nested;Check(child.CreateBuilding(out nested,ref random,new BuildingInfo(),new Vector3(),0,4,0),"nested native creation result");
                    Check(owner.Capacity.ClaimedCreates==1&&owner.Capacity.Observations==1,"same TLS attempt claims only first global create");
                };ushort id;Check(Create(manager,out id),"controlled marker create");End(typeof(NewLotGrowthGate),previous);
                Check(owner.Capacity.PendingMutations==0&&owner.Capacity.DeferredCreates==0&&owner.Capacity.Commits==1,"controlled create then wrapper commit clears deferred marker");
                Randomizer r=new Randomizer();Check(new BuildingManager().CreateBuilding(out id,ref r,new BuildingInfo(),new Vector3(),0,4,0),"direct create after controlled scope");
                Check(owner.Capacity.LastCreateAttempt==null&&owner.Capacity.Observations==2,"controlled TLS marker restored before next direct native call");
            }
            foreach(int behavior in new[]{0,1,2,3,4})
            {
                var owner=Owner();var manager=new BuildingManager{Behavior=behavior};Randomizer random=new Randomizer();ushort id=0;Exception error=null;
                try{manager.CreateBuilding(out id,ref random,new BuildingInfo(),new Vector3(),0,4,0);}catch(Exception e){error=e;}
                Check(owner.Capacity.LastCreateAttempt==null&&owner.Capacity.ClaimedCreates==0,"direct create has no controlled attempt");
                Check(owner.Capacity.Commits==0&&owner.Capacity.Marks==0,"direct create never mints controlled commit");
                Check(owner.Capacity.UnresolvedMutations==((behavior>=2)?1:0),"direct native error/false-live outID remains unresolved");
                Check((error!=null)==(behavior==2||behavior==3),"false-live diagnostic does not change native result/exception");
            }
            foreach(int behavior in new[]{1,2,3,4})
            {
                var owner=Owner();object previous=Begin(typeof(NewLotGrowthGate),3);Gate(99,0);var manager=new BuildingManager{Behavior=behavior};ushort id=0;Exception error=null;
                try{Check(!Create(manager,out id),"reserved failure native result preserved");}catch(Exception caught){error=caught;Check(behavior==2||behavior==3,"reserved failure exception preserved");}
                Check(ResidentialCapacityCreationContext.Current(owner,owner.Capacity)==null,"reserved TLS marker restored after false/exception");
                Check(owner.Capacity.Cancels==((behavior==1||behavior==2)?1:0)&&owner.Capacity.Marks==((behavior==3||behavior==4)?1:0),"partial false/exception never refunds physical stock");
                End(typeof(NewLotGrowthGate),previous,error);
            }
            {
                var owner=Owner();var attempt=new ResidentialCapacityAttempt{Decision=ResidentialCapacityAttemptDecision.Reserved};
                var outer=ResidentialCapacityCreationContext.Enter(owner,owner.Capacity,attempt);
                Check(ReferenceEquals(ResidentialCapacityCreationContext.Current(owner,owner.Capacity),attempt),"TLS owner/session marker available");
                var nested=new ResidentialCapacityAttempt{Decision=ResidentialCapacityAttemptDecision.Reserved};var inner=ResidentialCapacityCreationContext.Enter(owner,owner.Capacity,nested);
                Check(ReferenceEquals(ResidentialCapacityCreationContext.Current(owner,owner.Capacity),nested),"nested TLS marker owns its attempt");ResidentialCapacityCreationContext.Exit(inner);
                Check(ReferenceEquals(ResidentialCapacityCreationContext.Current(owner,owner.Capacity),attempt),"nested TLS marker restores parent");
                object foreignThreadMarker=attempt;var thread=new System.Threading.Thread(delegate(){foreignThreadMarker=ResidentialCapacityCreationContext.Current(owner,owner.Capacity);});thread.Start();thread.Join();
                Check(foreignThreadMarker==null,"controlled creation marker never crosses thread");owner.DemandContextSession++;
                Check(ResidentialCapacityCreationContext.Current(owner,owner.Capacity)==null,"controlled creation marker invalidated by owner epoch");ResidentialCapacityCreationContext.Exit(outer);
                Check(ResidentialCapacityCreationContext.Current(owner,owner.Capacity)==null,"controlled creation marker popped despite changed epoch");
            }
            {
                var owner=Owner();owner.Capacity.ModeRequested=false;object state=BeginMutation("Begin",5);EndMutation(state);
                Check(owner.Capacity.MutationBegins==0&&owner.Capacity.PendingMutations==0,"ordinary shadow mutation hooks remain neutral");
            }
            return _checks;
        }
        private static bool GateWithFreshScope()
        { object previous=Begin(typeof(NewLotGrowthGate),3);bool result=Gate(99,0);End(typeof(NewLotGrowthGate),previous);return result; }
    }
}
