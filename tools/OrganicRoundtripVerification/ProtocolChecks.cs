#pragma warning disable 0649, 0414
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
// Inert value stubs. No native, Unity, Steam, provider or installed Mod code runs.
public struct CitizenUnit { [Flags] public enum Flags { Created=1,Home=2,Work=4 } public Flags m_flags; public ushort m_building; }
public struct Citizen { [Flags] public enum Flags { Created=1,Dead=2,MovingIn=4 } public Flags m_flags; public ushort m_homeBuilding; }
public class ItemClass { public int m_service,m_subService,m_level; }
public class BuildingAI { public BuildingInfo m_info; } public class DifferentBuildingAI:BuildingAI { }
public class NetAI { public NetInfo m_info; } public class DifferentNetAI:NetAI { }
public class BuildingInfo { public string name; public ItemClass m_class=new ItemClass(); public BuildingAI m_buildingAI; public int m_cellWidth=2,m_cellLength=2,m_placementMode,m_placementStyle,m_zoningMode; public bool m_circular;public float m_placementOffset; }
public class NetInfo { public string name;public ItemClass m_class=new ItemClass();public NetAI m_netAI;public float m_halfWidth=8,m_pavementWidth=3,m_segmentLength=64; }
public static class PrefabCollection<T> { public static T Current; public static T FindLoaded(string unused){return Current;} }
public static class Singleton<T> { public static bool exists=true;public static T instance; }
public sealed class LoadingManager { public bool m_currentlyLoading,m_applicationQuitting; }
public sealed class SimulationManager { public bool ForcedSimulationPaused=true;public Queue<Action> Actions=new Queue<Action>();public void AddAction(string unused,Action action){Actions.Enqueue(action);}public void AddAction(Action action){Actions.Enqueue(action);}public void Drain(){while(Actions.Count>0)Actions.Dequeue()();} }
namespace SC4Evolution.Mod.Diagnostics {
 public sealed class EconomicBridge { public object Capacity=new object();public int DemandContextSession=7; }
 public static class ModRuntime { public static EconomicBridge CurrentEconomicBridge; }
 public sealed partial class AutonomousTestDriver {
  private static bool _cancelled;private bool _owns=true,_organicMode=true; private NativeSaveRoundtrip _roundtrip;
  private NativeOrganicResidentialProbe _organicProbe;private CleanupDummy _classProbe,_civicProbe;
  private string _session="owned",_phase="Cancelled",_error;private void WriteReport(){}private bool IsOrganicRoundtripMode(){return _organicMode;}
  private sealed class CleanupDummy {internal bool CleanupReady;internal void RequestCleanup(){}}
  private sealed class NativeDiagnosticContext {internal NativeDiagnosticContext(string phase,string session,bool accepted){}}
  private bool OwnsCurrentCity(){return _owns;}
  // REQUEST_CLASS
  // DRIVER_METHODS
  private sealed class NativeSaveRoundtrip {
   internal AutonomousTestDriver _owner;internal NativeOrganicResidentialProbe _organic;
   internal bool _organicUnloadPending=true,_cleanupRequested,_interrupted,_forcedLease=true;
   internal string _path="owned.ccs",_fileHash="saved-hash";
   private readonly object _leaseLock=new object();private NativeDiagnosticContext _diagnosticContext;
   internal bool _saveStarted,_saveDone,_reloadStarted,_reloadDone,_releaseQueued,_releaseDone,_cleanupReported,_previousForced;
   internal string _cleanupError;internal SimulationManager _leaseManager;internal Signal _pending;
   private bool _insideOwnReload;
   internal sealed class Signal {internal bool Done;}
   // SAVE_METHODS
   internal NativeSaveRoundtrip(AutonomousTestDriver owner){_owner=owner;}
   internal void SetOrganic(NativeOrganicResidentialProbe organic){_organic=organic;}
  }
  private sealed class NativeOrganicResidentialProbe {
   private readonly object _gate=new object();private AutonomousTestDriver _owner;
   private readonly List<string> _errors=new List<string>();private object _optionalToolbarContext;internal SimulationManager _simulation;
   internal bool _done,_speedHeld,_pending,_cleanupQueued,_cleaned;internal string _failure,LastPhase;internal int Destructions;
   internal bool DiagnosticOnly {get{return false;}} private bool _verified=true;private void Detach(){}private void Add(string unused){}
   private void ReleaseSpeedLease(){_speedHeld=false;}
   private void CleanupOwnedResources(){RequireCleanupOwner();Destructions++;}
   internal bool _handoffFrozen=true,_handoffReloadSeen,_handoffReloadArmed,_interrupted,_roundtripUnloadAcknowledged;
   private string _fixtureFingerprint="frozen",_reloadPermitPath,_reloadPermitHash;
   private NativeSaveRoundtrip _reloadPermitOwner; private EconomicBridge _checkpointOwner;
   private EconomicBridge _bridge,_restoredOwner;private int _restoredEpoch,_initialOwnerEpoch;
   private bool _handoffAdopted;
   private object _checkpointCapacity; private int _checkpointEpoch;
   private bool OwnsCity(){return _owner.OwnsCurrentCity();} private void SetPhase(string phase){LastPhase=phase;}
   private static string Json(string text){return text==null?"null":"\""+text+"\"";}private static string Bool(bool value){return value?"true":"false";}
   private static int FloatBits(float value){return BitConverter.ToInt32(BitConverter.GetBytes(value),0);}
   // ORGANIC_METHODS
   internal NativeOrganicResidentialProbe(AutonomousTestDriver owner,EconomicBridge bridge){_owner=owner;_checkpointOwner=_bridge=bridge;_checkpointCapacity=bridge.Capacity;_checkpointEpoch=_initialOwnerEpoch=bridge.DemandContextSession;}
   internal static void Inverse(HashSet<ushort> homes,HashSet<uint> units,HashSet<uint> people,CitizenUnit[] u,Citizen[] p){RequireInverseHomeCoverage(homes,units,people,u,p);}
   internal static string BuildingContract(BuildingInfo info){var b=new StringBuilder();AppendBuildingPrefabContract(b,info);return b.ToString();}
   internal static string NetContract(NetInfo info){var b=new StringBuilder();AppendNetPrefabContract(b,info);return b.ToString();}
   internal bool Consume(){return ConsumeOwnedReloadPermit();}
   internal static bool Inputs(int[] a,int[] b){return SameInputs(a,b);} internal static bool Zero(int[] a){return AllZero(a);}
  }
  private static int _checks;
  private static void Check(bool condition,string name){if(!condition)throw new Exception(name);_checks++;}
  private static void Reject(Action action,string name){bool failed=false;try{action();}catch{failed=true;}Check(failed,name);}
  private static NativeOrganicResidentialProbe Fixture(out AutonomousTestDriver owner){_cancelled=false;owner=new AutonomousTestDriver();ModRuntime.CurrentEconomicBridge=new EconomicBridge();owner._roundtrip=new NativeSaveRoundtrip(owner);var p=new NativeOrganicResidentialProbe(owner,ModRuntime.CurrentEconomicBridge);owner._roundtrip.SetOrganic(p);owner._organicProbe=p;return p;}
  public static int Run(string requestPath){
   _checks=0;AutonomousTestDriver owner;var probe=Fixture(out owner);
   Check(NativeOrganicResidentialProbe.Inputs(new[]{1,2,3},new[]{1,2,3}),"exact applied inputs accepted");
   Check(!NativeOrganicResidentialProbe.Inputs(new[]{1,2,3},new[]{1,2,4}),"one-unit census difference rejected without tolerance");
   Check(!NativeOrganicResidentialProbe.Inputs(new[]{1,2},new[]{1,2,0}),"census input shape difference rejected");
   Check(NativeOrganicResidentialProbe.Zero(new[]{0,0,0})&&!NativeOrganicResidentialProbe.Zero(new[]{0,1,0}),"unbound inputs require exact zero");
   probe.ArmOwnedReload(owner._roundtrip,"owned.ccs","saved-hash");
   Check(probe.Consume(),"accepted ticket survives beyond synchronous LoadLevel call until actual unload");
   Check(!probe.Consume(),"owned unload permit is single-use");
   Check(probe.ConsumeRoundtripUnloadAcknowledgment()&&!probe.ConsumeRoundtripUnloadAcknowledgment(),"roundtrip callback acknowledgment is single-use");
   Check(!owner._roundtrip._organicUnloadPending,"global pending flag consumed with permit");
   foreach(string mutation in new[]{"cancel","foreign-owner","foreign-capacity","epoch","foreign-session","wrong-path","wrong-hash","no-pending","no-pause","different-roundtrip","revoked","interrupted"}){
    probe=Fixture(out owner);probe.ArmOwnedReload(owner._roundtrip,"owned.ccs","saved-hash");
    switch(mutation){case "cancel":_cancelled=true;break;case "foreign-owner":ModRuntime.CurrentEconomicBridge=new EconomicBridge();break;case "foreign-capacity":ModRuntime.CurrentEconomicBridge.Capacity=new object();break;case "epoch":ModRuntime.CurrentEconomicBridge.DemandContextSession++;break;case "foreign-session":owner._owns=false;break;case "wrong-path":owner._roundtrip._path="foreign.ccs";break;case "wrong-hash":owner._roundtrip._fileHash="changed";break;case "no-pending":owner._roundtrip._organicUnloadPending=false;break;case "no-pause":owner._roundtrip._forcedLease=false;break;case "different-roundtrip":owner._roundtrip=new NativeSaveRoundtrip(owner);break;case "revoked":probe.RevokeUnusedReloadPermit();break;case "interrupted":probe._interrupted=true;break;}
    Check(!probe.Consume(),"reject permit "+mutation);
   }
   probe=Fixture(out owner);probe.ArmOwnedReload(owner._roundtrip,"owned.ccs","saved-hash");var capturedProbe=probe;var capturedOwner=owner;
   Reject(delegate{capturedProbe.ArmOwnedReload(capturedOwner._roundtrip,"owned.ccs","saved-hash");},"cannot mint second armed permit");
   var homes=new HashSet<ushort>{5};var reachedUnits=new HashSet<uint>{1};var reachedPeople=new HashSet<uint>{1};var units=new CitizenUnit[4];var people=new Citizen[4];
   units[1]=new CitizenUnit{m_flags=CitizenUnit.Flags.Created|CitizenUnit.Flags.Home,m_building=5};people[1]=new Citizen{m_flags=Citizen.Flags.Created,m_homeBuilding=5};
   NativeOrganicResidentialProbe.Inverse(homes,reachedUnits,reachedPeople,units,people);Check(true,"coherent inverse HOME/member coverage");
   units[2]=units[1];Reject(delegate{NativeOrganicResidentialProbe.Inverse(homes,reachedUnits,reachedPeople,units,people);},"orphan HOME unit rejected");units[2]=new CitizenUnit();
   foreach(Citizen.Flags flags in new[]{Citizen.Flags.Created,Citizen.Flags.Created|Citizen.Flags.MovingIn,Citizen.Flags.Created|Citizen.Flags.Dead}){people[2]=new Citizen{m_flags=flags,m_homeBuilding=5};Reject(delegate{NativeOrganicResidentialProbe.Inverse(homes,reachedUnits,reachedPeople,units,people);},"orphan citizen rejected "+flags);}
   people[2]=new Citizen{m_flags=Citizen.Flags.Created,m_homeBuilding=9};units[2]=new CitizenUnit{m_flags=CitizenUnit.Flags.Created|CitizenUnit.Flags.Home,m_building=9};
   NativeOrganicResidentialProbe.Inverse(homes,reachedUnits,reachedPeople,units,people);Check(true,"unrelated native home is outside inverse fixture ownership");
   var b=Building();string original=NativeOrganicResidentialProbe.BuildingContract(b);
   var replacement=Building();Check(original==NativeOrganicResidentialProbe.BuildingContract(replacement),"new catalog instance with exact contract accepted");
   replacement.m_cellWidth++;Check(original!=NativeOrganicResidentialProbe.BuildingContract(replacement),"same-name different building geometry changes fingerprint");replacement.m_cellWidth--;
   replacement.m_class.m_subService++;Check(original!=NativeOrganicResidentialProbe.BuildingContract(replacement),"same-name different class changes fingerprint");replacement.m_class.m_subService--;
   replacement.m_placementMode++;Check(original!=NativeOrganicResidentialProbe.BuildingContract(replacement),"same-name different placement changes fingerprint");replacement.m_placementMode--;
   replacement.m_buildingAI=new DifferentBuildingAI{m_info=replacement};Check(original!=NativeOrganicResidentialProbe.BuildingContract(replacement),"same-name different AI changes fingerprint");
   Reject(delegate{NativeOrganicResidentialProbe.BuildingContract(b);},"noncanonical old prefab reference rejected");replacement.m_buildingAI.m_info=b;Reject(delegate{NativeOrganicResidentialProbe.BuildingContract(replacement);},"incoherent building AI binding rejected");
   var n=Network();string netOriginal=NativeOrganicResidentialProbe.NetContract(n);var next=Network();Check(netOriginal==NativeOrganicResidentialProbe.NetContract(next),"new native network instance with exact contract accepted");
   next.m_halfWidth++;Check(netOriginal!=NativeOrganicResidentialProbe.NetContract(next),"same-name different network width changes fingerprint");next.m_halfWidth--;
   next.m_netAI=new DifferentNetAI{m_info=next};Check(netOriginal!=NativeOrganicResidentialProbe.NetContract(next),"same-name different network AI changes fingerprint");Reject(delegate{NativeOrganicResidentialProbe.NetContract(n);},"noncanonical network reference rejected");
   string valid="schema=1\nmode=OrganicResidentialSaveReloadTestCity\nassetName=builtin\nassetChecksum=hash\ntimeoutSeconds=600\ncontrolledQuit=true\ncaptureAllTabs=false\norganicInventoryOnly=false\n";
   File.WriteAllText(requestPath,valid);Check(ReadRequest(requestPath).Mode=="OrganicResidentialSaveReloadTestCity","explicit new mode parsed");
   foreach(string changed in new[]{valid.Replace("organicInventoryOnly=false","organicInventoryOnly=true"),valid.Replace("captureAllTabs=false","captureAllTabs=true"),valid.Replace("600","601"),valid.Replace("SaveReloadTestCity","SaveReloadTestCityTypo")}){File.WriteAllText(requestPath,changed);Reject(delegate{ReadRequest(requestPath);},"invalid populated request rejected");}
   File.WriteAllText(requestPath,valid.Replace("OrganicResidentialSaveReloadTestCity","OrganicResidentialTestCity").Replace("organicInventoryOnly=false","organicInventoryOnly=true"));Check(ReadRequest(requestPath).OrganicInventoryOnly,"existing organic inventory remains allowed");
   foreach(string work in new[]{"save","reload","checkpoint","loading"}){
    probe=Fixture(out owner);var sim=new SimulationManager();Singleton<SimulationManager>.instance=sim;Singleton<LoadingManager>.instance=new LoadingManager();
    owner._roundtrip._leaseManager=probe._simulation=sim;probe._speedHeld=true;
    if(work=="save")owner._roundtrip._saveStarted=true;
    if(work=="reload")owner._roundtrip._reloadStarted=true;
    if(work=="checkpoint")owner._roundtrip._pending=new NativeSaveRoundtrip.Signal();
    if(work=="loading")Singleton<LoadingManager>.instance.m_currentlyLoading=true;
    _cancelled=true;owner._roundtrip.CancelBeforePluginRemoval();probe.InterruptRoundtrip();
    Check(owner._roundtrip._cleanupRequested&&owner._roundtrip._interrupted&&!owner._roundtrip._organicUnloadPending,"cancel invalidates acquisition and ticket "+work);
    Check(sim.ForcedSimulationPaused&&sim.Actions.Count==0,"cancel retains populated pause without early release "+work);
    owner._roundtrip.RequestCleanup();Check(sim.Actions.Count==0,"direct cleanup cannot release unsettled populated work "+work);
    Check(!owner.RoundtripCleanupReady()&&sim.Actions.Count==0,"parent cannot enqueue fixture cleanup before native work settles "+work);
    owner._roundtrip._saveDone=owner._roundtrip._reloadDone=true;if(owner._roundtrip._pending!=null)owner._roundtrip._pending.Done=true;Singleton<LoadingManager>.instance.m_currentlyLoading=false;
    // OnDisabled stopped the old bridge. Matching native session alone cannot
    // authorize destruction; the real cleanup wrapper must finish refusal.
    ModRuntime.CurrentEconomicBridge=null;
    Check(!owner.RoundtripCleanupReady()&&sim.Actions.Count==1&&sim.ForcedSimulationPaused,"settled parent queues fixture resolution before pause release "+work);
    sim.Drain();Check(probe.CleanupReady&&!probe._cleaned&&probe.Destructions==0&&probe._failure!=null&&probe.LastPhase=="CleanupIncomplete","stopped owner yields terminal safe refusal, no destruction "+work);
    Check(sim.ForcedSimulationPaused&&!owner._roundtrip._releaseDone,"cleanup refusal finishes while own pause remains held "+work);
    Check(!owner.RoundtripCleanupReady()&&sim.Actions.Count==1,"pause release is queued only after settled cleanup refusal "+work);
    sim.Drain();Check(!sim.ForcedSimulationPaused&&owner.RoundtripCleanupReady(),"cancel drain releases only own pause and terminates "+work);
    Check(_cancelled&&owner._phase!="Completed","cancelled probe never becomes pass "+work);
   }
   probe=Fixture(out owner);var foreignSim=new SimulationManager();Singleton<SimulationManager>.instance=foreignSim;Singleton<LoadingManager>.instance=new LoadingManager();
   owner._roundtrip._leaseManager=probe._simulation=foreignSim;_cancelled=true;owner._roundtrip.CancelBeforePluginRemoval();probe.InterruptRoundtrip();owner._owns=false;ModRuntime.CurrentEconomicBridge=null;probe._speedHeld=true;
   Check(!owner.RoundtripCleanupReady(),"foreign session starts only refusal drain");foreignSim.Drain();Check(probe.CleanupReady&&probe.Destructions==0,"foreign ownership refusal does not strand speed lease guard");
   Check(!owner.RoundtripCleanupReady(),"foreign pause ownership checked after fixture refusal");foreignSim.Drain();Check(foreignSim.ForcedSimulationPaused&&owner._roundtrip._cleanupError!=null&&owner.RoundtripCleanupReady(),"foreign pause unchanged and cleanup ownership loss explicit/terminal");
   probe=Fixture(out owner);var legacySim=new SimulationManager();Singleton<SimulationManager>.instance=legacySim;Singleton<LoadingManager>.instance=new LoadingManager();owner._roundtrip._leaseManager=legacySim;owner._roundtrip._organic=null;owner._roundtrip._saveStarted=true;
   owner._roundtrip.CancelBeforePluginRemoval();Check(owner._roundtrip._interrupted&&legacySim.Actions.Count==1,"legacy SaveReload cancellation dispatch remains immediate");
   probe=Fixture(out owner);var ownedSim=new SimulationManager();Singleton<SimulationManager>.instance=ownedSim;Singleton<LoadingManager>.instance=new LoadingManager();
   owner._roundtrip._leaseManager=probe._simulation=ownedSim;probe._speedHeld=true;_cancelled=true;owner._roundtrip.CancelBeforePluginRemoval();probe.InterruptRoundtrip();
   Check(!owner.RoundtripCleanupReady()&&ownedSim.ForcedSimulationPaused,"cancelled own fixture resolves before own pause release");ownedSim.Drain();
   Check(probe._cleaned&&probe.Destructions==1&&probe.LastPhase=="FailedOrCancelledReleased"&&owner._phase=="Cancelled","cancelled cleanup does not claim verified pass even with matching owner");
   Check(!owner.RoundtripCleanupReady(),"own cancelled cleanup queues pause release afterward");ownedSim.Drain();Check(!ownedSim.ForcedSimulationPaused&&owner.RoundtripCleanupReady(),"own cancelled cleanup drains to terminal");
   probe=Fixture(out owner);var externalSim=new SimulationManager();Singleton<SimulationManager>.instance=externalSim;Singleton<LoadingManager>.instance=new LoadingManager();
   owner._roundtrip._leaseManager=probe._simulation=externalSim;probe._speedHeld=true;owner._roundtrip._saveStarted=true;
   probe.BeforeLevelUnload();owner._roundtrip.BeforeLevelUnload();
   Check(probe._interrupted&&owner._roundtrip._interrupted&&externalSim.Actions.Count==0&&externalSim.ForcedSimulationPaused,"external unload during save cannot release before deferred fixture resolution");
   Check(!owner.RoundtripCleanupReady()&&externalSim.Actions.Count==0,"external unload still waits for accepted native save");
   owner._roundtrip._saveDone=true;owner._owns=false;ModRuntime.CurrentEconomicBridge=null;
   Check(!owner.RoundtripCleanupReady(),"external foreign load resolves by refusal after accepted work settles");externalSim.Drain();
   Check(probe.CleanupReady&&probe.Destructions==0&&probe.LastPhase=="CleanupIncomplete","external unload refusal is explicit and terminal");
   Check(!owner.RoundtripCleanupReady(),"external foreign pause checked after refusal");externalSim.Drain();Check(externalSim.ForcedSimulationPaused&&owner._roundtrip._cleanupError!=null&&owner.RoundtripCleanupReady(),"external foreign state unchanged and ownership failure reported");
   return _checks;
  }
  private static BuildingInfo Building(){var b=new BuildingInfo{name="same"};b.m_buildingAI=new BuildingAI{m_info=b};PrefabCollection<BuildingInfo>.Current=b;return b;}
  private static NetInfo Network(){var n=new NetInfo{name="same"};n.m_netAI=new NetAI{m_info=n};PrefabCollection<NetInfo>.Current=n;return n;}
 }
}
