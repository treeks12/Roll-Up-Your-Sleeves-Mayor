using System;
using SC4Evolution.Core;
using SC4Evolution.Simulation;
namespace ICities {
 public enum LoadMode { NewGame,LoadGame,NewGameFromScenario,Other }
 public class LoadingExtensionBase { public virtual void OnLevelLoaded(LoadMode mode){} public virtual void OnLevelUnloading(){} }
 public class ThreadingExtensionBase { public virtual void OnAfterSimulationFrame(){} }
 public interface ISerializableData { byte[] LoadData(string key); void SaveData(string key,byte[] value); }
 public class SerializableDataExtensionBase { public ISerializableData serializableDataManager; public virtual void OnLoadData(){} public virtual void OnSaveData(){} }
}
namespace UnityEngine { public static class Debug { public static void Log(string x){} public static void LogWarning(string x){} } }
namespace ColossalFramework { public static class Singleton<T> where T:new() { public static T instance=new T(); public static bool exists=true; } }
namespace ColossalFramework.Plugins { public class PluginManager { public class PluginInfo { public string modPath {get;set;} } public PluginInfo FindPluginInfo(System.Reflection.Assembly a){return null;} } }
public static class BuildConfig { public static string applicationVersion="stub"; }
public class SimulationManager { public uint m_currentFrameIndex; public void AddAction(Action a){} }
namespace SC4Evolution.Mod.UI { public static class CabinetUI { public static int Installs,Uninstalls;public static Action InstallCallback;public static void Install(){Installs++;if(InstallCallback!=null)InstallCallback();} public static void Uninstall(){Uninstalls++;} } }
namespace SC4Evolution.Mod.Diagnostics { public static class AutonomousTestDriver { public static void OnLevelUnloading(){} } }
namespace SC4Evolution.Mod.Runtime { public static class SnapshotReader { public static CitySnapshot Capture(){return new CitySnapshot();} } }
namespace SC4Evolution.Mod.Economy {
 internal sealed class EconomicStartPreparation {
  internal readonly string RootSnapshot; internal readonly bool RawOptIn,ConfigurationValid,ProviderReady,CivicFundingEnabled;internal readonly string FailureReason;
  internal EconomicStartPreparation(string root,bool raw,bool valid,string failure,bool providerReady=false,bool civicFundingEnabled=false){RootSnapshot=root;RawOptIn=raw;ConfigurationValid=valid;FailureReason=failure;ProviderReady=providerReady;CivicFundingEnabled=civicFundingEnabled;}
 }
 public sealed class EconomicSavePair { readonly byte[] c,p; public EconomicSavePair(byte[] classes,byte[] capacity){c=classes;p=capacity;} public byte[] ClassPayload {get{return c==null?null:(byte[])c.Clone();}} public byte[] CapacityPayload {get{return p==null?null:(byte[])p.Clone();}} }
 public class CityCensus { public int HomeHouseholds,OccupiedHomeHouseholds,UnmappedJobCapacity,UnmappedFilledJobs; }
 public class SegmentedDemandSnapshot { public DemandNodeState[] Nodes {get;set;} public CityCensus Census {get;set;} public bool Converged,GameplayEnabled; public int PropagationPasses; }
 public class ResidentialClassStatus { public bool Enabled;public object Policy;public int ImportedCount,SelectedCount,UnboundResidents;public string Status; }
 public class EconomicBridge {
  public static EconomicBridge Latest; public static EconomicSavePair Capture;public static bool Available=true;public static string UnavailableReason="InFlight";
  public static bool CivicConfigured;
  public static bool RawConfigured,ConfigurationValid=true,ProviderAvailable=true;public static string PreparationFailure;public static Action PreparationCallback,ReadinessCallback;public static int Preparations,Activations,ReadinessQueries,DependencyCalls;internal static EconomicStartPreparation LastPrepared;
  internal EconomicStartPreparation StartedPreparation;private string _status="stub";
  public byte[] LoadedClass,LoadedCapacity;public string ClassFailure,CapacityFailure,PersistenceFailure; public bool NewCity;public int Captures,StopCalls;
  public EconomicBridge(){Latest=this;} public SegmentedDemandSnapshot Published {get{return null;}} public string Status {get{return _status;}} public ResidentialClassStatus PublishedClassStatus {get{return null;}} public bool InitialSampleComplete {get{return true;}}
  public void SetLoadedClassState(byte[] b){LoadedClass=b;}public void SetClassLoadFailure(string r){ClassFailure=r;}
  public void SetLoadedCapacityState(byte[] b){LoadedCapacity=b;}public void SetCapacityLoadFailure(string r){CapacityFailure=r;}
  public void SetNewCapacityCity(bool value){NewCity=value;} public void Start(string root){}public void Stop(){StopCalls++;}public void SampleIfDue(){}
  internal static EconomicStartPreparation PrepareStart(string root){Preparations++;bool raw=RawConfigured,valid=ConfigurationValid,civic=CivicConfigured;string failure=PreparationFailure;bool ready=false;if(raw&&valid){ReadinessQueries++;if(ReadinessCallback!=null)ReadinessCallback();ready=ProviderAvailable;}var prepared=new EconomicStartPreparation(root,raw,valid,failure,ready,civic);LastPrepared=prepared;if(prepared.RawOptIn&&prepared.ConfigurationValid&&prepared.ProviderReady){Activations++;if(PreparationCallback!=null)PreparationCallback();}return prepared;}
  internal void Start(string root,EconomicStartPreparation preparation){if(preparation.RootSnapshot!=root)throw new InvalidOperationException("Preparation root mismatch");StartedPreparation=preparation;if(!preparation.ConfigurationValid||preparation.FailureReason!=null||(preparation.RawOptIn&&!preparation.ProviderReady)){_status="Preparation rejected: "+(preparation.FailureReason??"Official provider unavailable");Available=false;}else if(preparation.RawOptIn){DependencyCalls++;}}
  public bool TryGetSavePair(out EconomicSavePair pair,out string reason){Captures++;pair=Available?Capture:null;reason=UnavailableReason;return Available;}
  public void ReportPersistenceFailure(string reason){PersistenceFailure=reason;Available=false;}
 }
}

// Lifecycle contract stub only. It lets the harness link the production Runtime
// partial without loading Harmony, Unity, or the game's civic APIs. Raw retained
// bytes are forwarded and preserved exactly; this is not proof of backend behavior.
namespace SC4Evolution.Mod.Civic {
 using System;
 using System.IO;
 using System.Threading;
 using SC4Evolution.Simulation;
 internal sealed class CivicFundingPreparation : IDisposable {
  public static int DisposeCalls;
  public static Action DisposeCallback;
  public static bool ThrowOnDispose;
  public readonly bool Enabled;
  public readonly string Root;
  internal CivicFundingPreparation(bool enabled,string root){Enabled=enabled;Root=root;}
  public void Dispose(){DisposeCalls++;if(DisposeCallback!=null)DisposeCallback();if(ThrowOnDispose)throw new IOException("stub preparation disposal failure");}
 }
 public sealed class CivicFundingBridge {
  public static CivicFundingBridge Latest;
  public static int PrepareCalls;
  public static int StartCalls;
  public static Action PrepareCallback,StartCallback,StopCallback,ReleaseHooksCallback,CaptureCallback;
  public static byte[] CaptureOverride;
  public static bool ThrowOnReleaseHooks;
  public static string LastPreparedRoot;
  public static bool LastPrepareEnabled,LastStartEnabled;
  public static byte[] LastStartState;
  public static string LastLoadFailure;
  internal static CivicFundingPreparation LastPreparation;
  public static int StopCalls,ReleaseHooksCalls,CaptureCalls,ReconcileCalls;
  public static bool LastBridgeStopUnderLock,LastReleaseHooksUnderLock,LastStartUnderLock;
  public int InstanceStopCalls,InstanceReleaseHooksCalls;
  private byte[] _retained;
  private string _failure;
  private bool _enabled;
  public string Status {get;private set;}
  public string PersistenceFailure {get;private set;}
  internal static CivicFundingPreparation Prepare(bool enabled,string root){
   PrepareCalls++;LastPrepareEnabled=enabled;LastPreparedRoot=root;LastPreparation=new CivicFundingPreparation(enabled,root);
   Action callback=PrepareCallback;if(callback!=null)callback();return LastPreparation;
  }
  internal void Start(byte[] state,bool enabled,CivicFundingPreparation preparation,string loadFailure=null){
   StartCalls++;LastStartUnderLock=ProbeRuntimeLock();LastStartEnabled=enabled;LastStartState=Clone(state);LastPreparation=preparation;LastLoadFailure=loadFailure;
   _retained=Clone(state);_enabled=enabled;_failure=loadFailure;Latest=this;Status=enabled?"stub civic ready":"stub civic disabled";
   Action callback=StartCallback;if(callback!=null)callback();
  }
  public void Stop(){StopCalls++;InstanceStopCalls++;LastBridgeStopUnderLock=ProbeRuntimeLock();Action callback=StopCallback;if(callback!=null)callback();}
  public void ReleaseHooks(){ReleaseHooksCalls++;InstanceReleaseHooksCalls++;LastReleaseHooksUnderLock=ProbeRuntimeLock();Action callback=ReleaseHooksCallback;if(callback!=null)callback();if(ThrowOnReleaseHooks)throw new IOException("stub hook release failure");}
  public void Reconcile(){ReconcileCalls++;}
  public byte[] CaptureSave(){CaptureCalls++;if(CaptureCallback!=null)CaptureCallback();if(CaptureOverride!=null)return Clone(CaptureOverride);if(_failure!=null&&_retained==null)throw new InvalidDataException(_failure);return Clone(_retained);}
  public void ReportPersistenceFailure(string reason){PersistenceFailure=reason;Status="Persistence failed: "+reason;}
  private static byte[] Clone(byte[] value){return value==null?null:(byte[])value.Clone();}
  private static bool ProbeRuntimeLock(){
   object stateLock=typeof(SC4Evolution.Mod.Runtime.ModRuntime).GetField("StateLock",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).GetValue(null);
   bool acquired=false;Thread t=new Thread(delegate(){bool entered=false;try{entered=Monitor.TryEnter(stateLock,250);acquired=entered;}finally{if(entered)Monitor.Exit(stateLock);}});t.IsBackground=true;t.Start();
   if(!t.Join(1500))throw new InvalidOperationException("StateLock probe did not complete.");return !acquired;
  }
 }
}
