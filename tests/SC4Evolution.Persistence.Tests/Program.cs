using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using ICities;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Mod.Economy;
using SC4Evolution.Mod.Civic;
using SC4Evolution.Simulation;
class Program {
 const string Cabinet="SC4Evolution.Cabinet.v1",Legacy="SC4Evolution.ResidentialClasses.v1";
 static int checks;
 static void Check(bool b,string n){checks++;if(!b)throw new Exception(n);}
 static void Throws(Action a,string n){bool failed=false;try{a();}catch{failed=true;}Check(failed,n);}
 static bool Same(byte[] a,byte[] b){if(a==null||b==null)return a==b;if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
 static bool CanAcquireStateLock(){object stateLock=typeof(ModRuntime).GetField("StateLock",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);bool acquired=false;Thread worker=new Thread(delegate(){bool held=false;try{held=Monitor.TryEnter(stateLock,500);acquired=held;}finally{if(held)Monitor.Exit(stateLock);}});worker.IsBackground=true;worker.Start();if(!worker.Join(2000))throw new Exception("StateLock probe exceeded its bound");return acquired;}
 static void ResetCivicCallbacks(){CivicFundingBridge.PrepareCallback=null;CivicFundingBridge.StartCallback=null;CivicFundingBridge.StopCallback=null;CivicFundingBridge.ReleaseHooksCallback=null;CivicFundingBridge.CaptureCallback=null;CivicFundingBridge.CaptureOverride=null;CivicFundingBridge.ThrowOnReleaseHooks=false;CivicFundingPreparation.DisposeCallback=null;CivicFundingPreparation.ThrowOnDispose=false;}
 static void ResetCivicCounters(){CivicFundingBridge.Latest=null;CivicFundingBridge.PrepareCalls=0;CivicFundingBridge.StartCalls=0;CivicFundingBridge.StopCalls=0;CivicFundingBridge.ReleaseHooksCalls=0;CivicFundingBridge.CaptureCalls=0;CivicFundingBridge.ReconcileCalls=0;CivicFundingBridge.LastBridgeStopUnderLock=false;CivicFundingBridge.LastReleaseHooksUnderLock=false;CivicFundingBridge.LastStartUnderLock=false;CivicFundingBridge.LastPreparedRoot=null;CivicFundingBridge.LastPrepareEnabled=false;CivicFundingBridge.LastStartEnabled=false;CivicFundingBridge.LastStartState=null;CivicFundingBridge.LastLoadFailure=null;CivicFundingBridge.LastPreparation=null;CivicFundingPreparation.DisposeCalls=0;}
 sealed class Storage:ISerializableData {
  public Dictionary<string,byte[]> Values=new Dictionary<string,byte[]>();public List<string> Writes=new List<string>(),Reads=new List<string>();public string FailLoad,FailWrite;public bool FailAfterWrite;
  public byte[] LoadData(string k){Reads.Add(k);if(k==FailLoad)throw new IOException("load fail");byte[] v;return Values.TryGetValue(k,out v)?v:null;}
  public void SaveData(string k,byte[] b){Writes.Add(k);if(k==FailWrite&&!FailAfterWrite)throw new IOException("write fail");Values[k]=(byte[])b.Clone();if(k==FailWrite)throw new IOException("after write fail");}
 }
 static byte[] Classes(Guid id){using(var s=new MemoryStream()){ResidentialClassLedgerCodec.Write(s,new ResidentialClassLedger(id,1,new byte[32]));return s.ToArray();}}
 static byte[] Capacity(Guid id,byte[] c){var state=new ResidentialCapacityLedgerState(id,ResidentialCapacityLedger.PolicyName,ResidentialCapacityLedger.PolicyVersion,ResidentialCapacityLedger.PolicyHash,ResidentialNominalCapacity.PolicyName,ResidentialNominalCapacity.PolicyVersion,ResidentialNominalCapacity.PolicyHash,0,0,0,-1,-1,new ResidentialCapacityRecord[0],new ResidentialCapacityWindow[3]);using(var s=new MemoryStream()){ResidentialCapacityLedgerCodec.Write(s,state,c);return s.ToArray();}}
 static SavedData Reset(Storage s){ResetCivicCallbacks();ModRuntime.Stop();ModRuntime.BeginEconomicLoad();EconomicBridge.Available=true;EconomicBridge.Capture=null;EconomicBridge.RawConfigured=false;EconomicBridge.CivicConfigured=false;EconomicBridge.ConfigurationValid=true;EconomicBridge.ProviderAvailable=true;EconomicBridge.PreparationFailure=null;EconomicBridge.PreparationCallback=null;EconomicBridge.ReadinessCallback=null;EconomicBridge.Preparations=0;EconomicBridge.Activations=0;EconomicBridge.ReadinessQueries=0;EconomicBridge.DependencyCalls=0;EconomicBridge.LastPrepared=null;EconomicBridge.Latest=null;ResetCivicCounters();SC4Evolution.Mod.UI.CabinetUI.Installs=0;SC4Evolution.Mod.UI.CabinetUI.Uninstalls=0;SC4Evolution.Mod.UI.CabinetUI.InstallCallback=null;return new SavedData{serializableDataManager=s};}
 static void Main(){
  string output=Path.Combine(Directory.GetCurrentDirectory(),Path.Combine(".cache",Path.Combine("persistence-harness",Path.Combine("civic-lifecycle-harness","runtime"))));Directory.CreateDirectory(output);typeof(ModRuntime).GetField("_root",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,output);
  Guid id=Guid.NewGuid();byte[] c=Classes(id),p=Capacity(id,c),encoded=EconomicSaveEnvelope.Encode(c,p),dc,dp;
  EconomicSaveEnvelope.Decode(encoded,out dc,out dp);Check(Same(c,dc)&&Same(p,dp),"exact pair roundtrip");
  EconomicSaveEnvelope.Decode(EconomicSaveEnvelope.Encode(c,null),out dc,out dp);Check(Same(c,dc)&&dp==null,"legitimate absent capacity distinct");
  Throws(delegate{EconomicSaveEnvelope.Encode(c,new byte[0]);},"empty capacity is corrupt not absent");
  Throws(delegate{EconomicSaveEnvelope.Encode(Classes(Guid.NewGuid()),p);},"crossclass capacity rejected");
  for(int i=0;i<encoded.Length;i++){byte[] bad=(byte[])encoded.Clone();bad[i]^=1;Throws(delegate{byte[] x,y;EconomicSaveEnvelope.Decode(bad,out x,out y);},"single bit corrupt "+i);}
  Throws(delegate{byte[] x,y;EconomicSaveEnvelope.Decode(new byte[EconomicSaveEnvelope.MaximumBytes+1],out x,out y);},"oversize envelope");
  var s=new Storage();s.Values[Legacy]=c;s.Values[Cabinet]=new byte[]{4};var save=Reset(s);save.OnLoadData();ModRuntime.Start(false);Check(Same(EconomicBridge.Latest.LoadedClass,c)&&EconomicBridge.Latest.LoadedCapacity==null&&!EconomicBridge.Latest.NewCity,"legacy fallback existing city");
  s=new Storage();s.Values[Legacy]=Classes(Guid.NewGuid());s.Values[SavedData.EconomicPairKey]=encoded;save=Reset(s);save.OnLoadData();ModRuntime.Start(true);Check(Same(EconomicBridge.Latest.LoadedClass,c)&&Same(EconomicBridge.Latest.LoadedCapacity,p)&&EconomicBridge.Latest.NewCity,"authoritative pair exact bytes/newcity");Check(!s.Reads.Contains(Legacy),"present pair never reads stale legacy");
  EconomicBridge.Capture=new EconomicSavePair(c,p);save.OnSaveData();Check(s.Writes.Count==2&&s.Writes[0]==SavedData.EconomicPairKey&&s.Writes[1]==Cabinet,"pair first cabinet independent");Check(EconomicBridge.Latest.Captures==1,"one owner capture per save");Check(!s.Writes.Contains(Legacy),"legacy storage preserved");
  s=new Storage();s.Values[Legacy]=c;s.Values[SavedData.EconomicPairKey]=new byte[0];save=Reset(s);save.OnLoadData();ModRuntime.Start(false);Check(EconomicBridge.Latest.ClassFailure!=null&&EconomicBridge.Latest.CapacityFailure!=null&&!s.Reads.Contains(Legacy),"corrupt present no fallback both failures");EconomicBridge.Available=false;Throws(delegate{save.OnSaveData();},"loadfailed save visible");Check(s.Writes.Count==0,"loadfailed save no writes");
  s=new Storage();s.FailLoad=SavedData.EconomicPairKey;save=Reset(s);save.OnLoadData();ModRuntime.Start(false);Check(EconomicBridge.Latest.ClassFailure!=null&&EconomicBridge.Latest.CapacityFailure!=null&&!s.Reads.Contains(Legacy),"read exception distinct absent");
  s=new Storage();save=Reset(s);ModRuntime.SetEconomicLoadFailure("corrupt");ModRuntime.SetLoadedResidentialClassState(c);ModRuntime.SetLoadedEconomicPair(c,p);ModRuntime.Start(false);Check(EconomicBridge.Latest.ClassFailure=="corrupt"&&EconomicBridge.Latest.CapacityFailure=="corrupt","setter order cannot clear failures");
  s=new Storage();s.Values[SavedData.EconomicPairKey]=encoded;save=Reset(s);save.OnLoadData();ModRuntime.Start(false);EconomicBridge.Available=false;Throws(delegate{save.OnSaveData();},"inflight unavailable visible");Check(s.Writes.Count==0&&Same(s.Values[SavedData.EconomicPairKey],encoded),"inflight preserves whole pair before anywrite");Check(ModRuntime.PersistenceStatus.Contains("suspended")&&EconomicBridge.Latest.PersistenceFailure!=null,"failure status and suspension");
  foreach(bool after in new[]{false,true}){s=new Storage();s.Values[Legacy]=c;s.Values[SavedData.EconomicPairKey]=encoded;s.FailWrite=SavedData.EconomicPairKey;s.FailAfterWrite=after;save=Reset(s);save.OnLoadData();ModRuntime.Start(false);EconomicBridge.Capture=new EconomicSavePair(c,null);Throws(delegate{save.OnSaveData();},"pair storage failure visible");EconomicSaveEnvelope.Decode(s.Values[SavedData.EconomicPairKey],out dc,out dp);Check(Same(c,dc)&&(after?dp==null:Same(p,dp)),"before/after failure always completepair");Check(s.Writes.Count==1&&Same(s.Values[Legacy],c),"no later key after pairfailure");}
  s=new Storage();s.Values[SavedData.EconomicPairKey]=encoded;s.FailWrite=Cabinet;save=Reset(s);save.OnLoadData();ModRuntime.Start(false);EconomicBridge.Capture=new EconomicSavePair(c,null);Throws(delegate{save.OnSaveData();},"cabinet laterfailure visible");EconomicSaveEnvelope.Decode(s.Values[SavedData.EconomicPairKey],out dc,out dp);Check(Same(c,dc)&&dp==null,"laterkeyfailure leaves complete newpair");
  // Truncated/partial replacement is present-but-corrupt, never absence/migration.
  for(int length=0;length<encoded.Length;length+=Math.Max(1,encoded.Length/9)){
   byte[] partial=new byte[length];Array.Copy(encoded,partial,length);
   s=new Storage();s.Values[Legacy]=c;s.Values[SavedData.EconomicPairKey]=partial;save=Reset(s);save.OnLoadData();ModRuntime.Start(false);
   Check(EconomicBridge.Latest.ClassFailure!=null&&EconomicBridge.Latest.CapacityFailure!=null&&!s.Reads.Contains(Legacy),"partial envelope reload fails closed "+length);
  }
  s=new Storage();s.Values[Legacy]=Classes(Guid.NewGuid());s.Values[SavedData.EconomicPairKey]=EconomicSaveEnvelope.Encode(c,null);save=Reset(s);save.OnLoadData();
  Check(Same(ModRuntime.GetEconomicSaveEnvelope(),s.Values[SavedData.EconomicPairKey]),"pending valid Shadow pair retained exactly before Start");
  ModRuntime.Start(false);Check(Same(EconomicBridge.Latest.LoadedClass,c)&&EconomicBridge.Latest.LoadedCapacity==null,"reload saved legacy-capacity absence explicitly");
  s=new Storage();save=Reset(s);ModRuntime.SetEconomicLoadFailure("sticky");ModRuntime.SetLoadedEconomicPair(c,p);
  Check(ModRuntime.PersistenceStatus.Contains("rejected"),"later valid setter cannot conceal load failure status");
  s=new Storage();s.Values[SavedData.EconomicPairKey]=encoded;save=Reset(s);save.OnLoadData();ModRuntime.SetLoadedState(new byte[262145]);
  Throws(delegate{save.OnSaveData();},"cabinet preflight failure visible");Check(s.Writes.Count==0,"cabinet preflight failure before pair write");
  var loading=new Loading();s=new Storage();save=Reset(s);save.OnLoadData();loading.OnLevelLoaded(LoadMode.LoadGame);Check(!EconomicBridge.Latest.NewCity,"LoadGame does not infer fresh capacity window");
  ModRuntime.Stop();save=Reset(new Storage());save.OnLoadData();loading.OnLevelLoaded(LoadMode.NewGameFromScenario);Check(EconomicBridge.Latest.NewCity,"scenario explicitly fresh city");
  save=Reset(new Storage());save.OnLoadData();ModRuntime.Start(false);
  Check(EconomicBridge.Preparations==1&&EconomicBridge.Activations==0&&!EconomicBridge.Latest.StartedPreparation.RawOptIn,"Shadow snapshots config without activation");
  save=Reset(new Storage());ModRuntime.SetLoadedEconomicPair(c,p);EconomicBridge.RawConfigured=true;
  object stateLock=typeof(ModRuntime).GetField("StateLock",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
  EconomicBridge.PreparationCallback=delegate{
   Exception workerError=null;bool acquired=false;byte[] pending=null;
   Thread worker=new Thread(delegate(){bool held=false;try{held=Monitor.TryEnter(stateLock,500);acquired=held;if(held)pending=ModRuntime.GetEconomicSaveEnvelope();}catch(Exception e){workerError=e;}finally{if(held)Monitor.Exit(stateLock);}});
   worker.IsBackground=true;worker.Start();Check(worker.Join(2000),"preparation callback bounded worker completes");
   Check(workerError==null&&acquired,"RAW preparation executes outside StateLock");Check(Same(pending,encoded),"pending exact pair accessible and preserved during preparation");
   // Runtime must forward the immutable preparation, never reconstruct it from
   // configuration that changed during an external static-constructor callback.
   EconomicBridge.RawConfigured=false;EconomicBridge.ConfigurationValid=false;EconomicBridge.PreparationFailure="later config change";
  };
  ModRuntime.Start(true);Check(EconomicBridge.Activations==1&&ReferenceEquals(EconomicBridge.LastPrepared,EconomicBridge.Latest.StartedPreparation),"exact preparation object forwarded once");
  Check(EconomicBridge.Latest.StartedPreparation.RawOptIn&&EconomicBridge.Latest.StartedPreparation.ConfigurationValid&&EconomicBridge.Latest.StartedPreparation.FailureReason==null,"prepared configuration remains coherent");
  Check(Same(EconomicBridge.Latest.LoadedClass,c)&&Same(EconomicBridge.Latest.LoadedCapacity,p)&&EconomicBridge.Latest.NewCity,"preparation preserves payloads and newcity evidence");
  save=Reset(new Storage());ModRuntime.SetEconomicLoadFailure("existing unreadable save");EconomicBridge.RawConfigured=true;EconomicBridge.PreparationFailure="provider activation failed";ModRuntime.Start(false);
  Check(EconomicBridge.Latest.ClassFailure=="existing unreadable save"&&EconomicBridge.Latest.CapacityFailure=="existing unreadable save","activation failure does not clear load errors");
  Check(!EconomicBridge.Available&&ModRuntime.EconomicStatus.Contains("provider activation failed"),"activation failure visible and failclosed");
  save=Reset(new Storage());EconomicBridge.ConfigurationValid=false;EconomicBridge.PreparationFailure="malformed opt-in";ModRuntime.Start(false);
  Check(EconomicBridge.Activations==0&&!EconomicBridge.Available&&ModRuntime.EconomicStatus.Contains("malformed opt-in"),"invalid configuration never activates provider");
  save=Reset(new Storage());ModRuntime.SetLoadedEconomicPair(c,p);EconomicBridge.RawConfigured=true;EconomicBridge.PreparationCallback=delegate{ModRuntime.Stop();};
  Throws(delegate{ModRuntime.Start(false);},"Stop during preparation supersedes outer start");Check(!ModRuntime.IsLoaded&&EconomicBridge.Latest==null,"stale preparation cannot reactivate stopped session");
  save=Reset(new Storage());ModRuntime.SetLoadedEconomicPair(c,p);EconomicBridge.RawConfigured=true;EconomicBridge.PreparationCallback=delegate{EconomicBridge.PreparationCallback=null;ModRuntime.Start(true);};
  Throws(delegate{ModRuntime.Start(false);},"reentrant completed start supersedes outer start");Check(ModRuntime.IsLoaded&&EconomicBridge.Latest.NewCity&&Same(EconomicBridge.Latest.LoadedClass,c)&&Same(EconomicBridge.Latest.LoadedCapacity,p),"stale outer start cannot replace reentrant session or payloads");
  save=Reset(new Storage());ModRuntime.SetLoadedEconomicPair(c,p);EconomicBridge.RawConfigured=true;EconomicBridge.PreparationCallback=delegate{throw new IOException("unexpected preparation failure");};
  Throws(delegate{ModRuntime.Start(false);},"unexpected preparation exception remains visible");Check(EconomicBridge.Latest==null&&Same(ModRuntime.GetEconomicSaveEnvelope(),encoded),"pre-lock exception does not consume pending payloads");
  // Exercise the actual Loading callback: obsolete outer cleanup must not stop
  // the new owner or uninstall the UI installed by an inner Loading callback.
  save=Reset(new Storage());ModRuntime.SetLoadedEconomicPair(c,p);EconomicBridge.RawConfigured=true;
  EconomicBridge.PreparationCallback=delegate{EconomicBridge.PreparationCallback=null;new Loading().OnLevelLoaded(LoadMode.NewGame);};
  loading.OnLevelLoaded(LoadMode.LoadGame);
  Check(ModRuntime.IsLoaded&&EconomicBridge.Latest.NewCity&&EconomicBridge.Latest.StopCalls==0,"Loading stale outer preserves completed inner owner");
  Check(SC4Evolution.Mod.UI.CabinetUI.Installs==1&&SC4Evolution.Mod.UI.CabinetUI.Uninstalls==0,"Loading stale outer preserves inner installed UI");
  Check(Same(EconomicBridge.Latest.LoadedClass,c)&&Same(EconomicBridge.Latest.LoadedCapacity,p),"Loading reentry preserves exact inner payloads");
  save=Reset(new Storage());ModRuntime.SetLoadedEconomicPair(c,p);EconomicBridge.RawConfigured=true;
  EconomicBridge.PreparationCallback=delegate{EconomicBridge.PreparationCallback=null;new Loading().OnLevelLoaded(LoadMode.NewGame);throw new IOException("outer preparation throws after inner loading");};
  loading.OnLevelLoaded(LoadMode.LoadGame);
  Check(ModRuntime.IsLoaded&&EconomicBridge.Latest.NewCity&&EconomicBridge.Latest.StopCalls==0,"prep throw after inner Loading cannot stop new owner");
  Check(SC4Evolution.Mod.UI.CabinetUI.Installs==1&&SC4Evolution.Mod.UI.CabinetUI.Uninstalls==0,"prep throw after inner Loading cannot uninstall new UI");
  save=Reset(new Storage());EconomicBridge.RawConfigured=true;int sessionBefore=(int)typeof(ModRuntime).GetField("_session",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
  EconomicBridge.PreparationCallback=delegate{ModRuntime.Stop();throw new IOException("throw after stop");};loading.OnLevelLoaded(LoadMode.LoadGame);
  int sessionAfter=(int)typeof(ModRuntime).GetField("_session",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
  Check(!ModRuntime.IsLoaded&&EconomicBridge.Latest==null&&sessionAfter==sessionBefore+1,"prep throw after Stop does not repeat stale cleanup");
  Check(SC4Evolution.Mod.UI.CabinetUI.Installs==0&&SC4Evolution.Mod.UI.CabinetUI.Uninstalls==0,"stopped superseded callback does not touch UI");
  save=Reset(new Storage());EconomicBridge.RawConfigured=true;EconomicBridge.PreparationCallback=delegate{throw new IOException("legitimate owner failure");};
  sessionBefore=(int)typeof(ModRuntime).GetField("_session",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);loading.OnLevelLoaded(LoadMode.LoadGame);
  sessionAfter=(int)typeof(ModRuntime).GetField("_session",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
  Check(!ModRuntime.IsLoaded&&sessionAfter==sessionBefore+1&&SC4Evolution.Mod.UI.CabinetUI.Uninstalls==1,"legitimate owner failure retains normal Stop and UI cleanup");
  save=Reset(new Storage());SC4Evolution.Mod.UI.CabinetUI.InstallCallback=delegate{throw new IOException("legitimate current UI initialization failure");};loading.OnLevelLoaded(LoadMode.LoadGame);
  Check(!ModRuntime.IsLoaded&&EconomicBridge.Latest.StopCalls==1,"legitimate post-Start owner failure still stops its own bridge");
  Check(SC4Evolution.Mod.UI.CabinetUI.Installs==1&&SC4Evolution.Mod.UI.CabinetUI.Uninstalls==1,"legitimate current UI failure retains UI cleanup");
  save=Reset(new Storage());EconomicBridge.RawConfigured=true;EconomicBridge.ProviderAvailable=false;
  EconomicBridge.ReadinessCallback=delegate{
   bool free=false;Thread worker=new Thread(delegate(){bool held=Monitor.TryEnter(stateLock,500);free=held;if(held)Monitor.Exit(stateLock);});worker.IsBackground=true;worker.Start();
   Check(worker.Join(2000),"readiness callback bounded worker completes");Check(free,"provider readiness query also executes outside StateLock");
  };
  loading.OnLevelLoaded(LoadMode.LoadGame);
  Check(EconomicBridge.ReadinessQueries==1&&EconomicBridge.Activations==0&&EconomicBridge.DependencyCalls==0&&!EconomicBridge.Latest.StartedPreparation.ProviderReady,"cold RAW readiness failure cannot activate/install under lock");
  Check(ModRuntime.EconomicStatus.Contains("provider unavailable"),"cold provider failure visible");
  save=Reset(new Storage());ModRuntime.SetLoadedEconomicPair(c,p);loading.OnLevelLoaded(LoadMode.LoadGame);
  Check(EconomicBridge.ReadinessQueries==0&&EconomicBridge.Activations==0&&EconomicBridge.DependencyCalls==0,"retained Shadow does not query/activate provider");
  // Civic funding is read before any authoritative-envelope early return. The
  // stub intentionally treats the retained bytes as opaque; backend validation
  // belongs to CivicFundingBridge and is outside this lifecycle harness.
  byte[] civicBytes={0,255,17,128,3};
  s=new Storage();s.Values[SavedData.CivicFundingKey]=civicBytes;s.Values[SavedData.EconomicPairKey]=encoded;save=Reset(s);save.OnLoadData();
  Check(s.Reads.Count>=3&&s.Reads[0]==SavedData.CivicFundingKey&&s.Reads[2]==SavedData.EconomicPairKey,"valid economic envelope still follows Civic read");
  ModRuntime.Start(false);Check(!CivicFundingBridge.LastStartEnabled&&Same(CivicFundingBridge.LastStartState,civicBytes),"disabled Civic start receives exact retained bytes");
  EconomicBridge.Capture=new EconomicSavePair(c,p);CivicFundingBridge.CaptureCallback=delegate{Check(s.Writes.Count==0,"Civic capture happens before every storage write");};save.OnSaveData();
  Check(s.Writes.Count==3&&s.Writes[0]==SavedData.EconomicPairKey&&s.Writes[1]==Cabinet&&s.Writes[2]==SavedData.CivicFundingKey&&Same(s.Values[SavedData.CivicFundingKey],civicBytes),"disabled opaque Civic bytes roundtrip exactly in stub contract");
  s=new Storage();s.Values[SavedData.CivicFundingKey]=civicBytes;s.Values[SavedData.EconomicPairKey]=new byte[0];save=Reset(s);save.OnLoadData();
  Check(s.Reads.Count==3&&s.Reads[0]==SavedData.CivicFundingKey&&s.Reads[2]==SavedData.EconomicPairKey,"failed economic envelope still follows Civic read before returning");
  ModRuntime.Start(false);Check(Same(CivicFundingBridge.LastStartState,civicBytes),"present raw Civic bytes are forwarded even with failed economic envelope");
  s=new Storage();save=Reset(s);save.OnLoadData();Check(s.Reads.Contains(SavedData.CivicFundingKey),"absent Civic key is still queried");save.OnSaveData();
  Check(s.Writes.Count==0&&!s.Values.ContainsKey(SavedData.CivicFundingKey),"absent Civic state does not erase or create a key");
  s=new Storage();s.FailLoad=SavedData.CivicFundingKey;save=Reset(s);save.OnLoadData();Throws(delegate{save.OnSaveData();},"failed Civic read is not treated as absence");
  Check(s.Writes.Count==0,"failed Civic read blocks every storage write");ModRuntime.BeginEconomicLoad();ModRuntime.SetLoadedCivicState(civicBytes);save.OnSaveData();
  Check(s.Writes.Count==1&&s.Writes[0]==SavedData.CivicFundingKey&&Same(s.Values[SavedData.CivicFundingKey],civicBytes),"BeginEconomicLoad resets Civic read failure before a known replacement");
  s=new Storage();s.FailLoad=SavedData.CivicFundingKey;s.Values[SavedData.EconomicPairKey]=encoded;save=Reset(s);save.OnLoadData();ModRuntime.Start(false);
  Check(!string.IsNullOrEmpty(CivicFundingBridge.LastLoadFailure),"Civic read failure is forwarded into the owner");EconomicBridge.Capture=new EconomicSavePair(c,p);
  Throws(delegate{save.OnSaveData();},"loaded Civic failure stays unavailable without a retained payload");Check(s.Writes.Count==0,"loaded Civic failure cannot be mistaken for an absent save key");
  s=new Storage();s.Values[SavedData.CivicFundingKey]=new byte[]{9};s.Values[SavedData.EconomicPairKey]=encoded;s.Values[Cabinet]=new byte[]{8};save=Reset(s);save.OnLoadData();ModRuntime.Start(false);
  EconomicBridge.Capture=new EconomicSavePair(c,p);CivicFundingBridge.CaptureOverride=new byte[]{7,6};s.FailWrite=SavedData.CivicFundingKey;
  Throws(delegate{save.OnSaveData();},"Civic storage failure after earlier key calls is visible");
  Check(s.Writes.Count==3&&s.Writes[0]==SavedData.EconomicPairKey&&s.Writes[1]==Cabinet&&s.Writes[2]==SavedData.CivicFundingKey,"Civic storage failure occurs after earlier per-key writes");
  Check(Same(s.Values[SavedData.EconomicPairKey],encoded)&&s.Values.ContainsKey(Cabinet)&&!Same(s.Values[Cabinet],new byte[]{8})&&Same(s.Values[SavedData.CivicFundingKey],new byte[]{9}),"earlier accepted writes remain visible; no cross-key rollback claim");
  Check(CivicFundingBridge.Latest.PersistenceFailure!=null,"Civic owner receives visible save failure");
  s=new Storage();s.Values[SavedData.EconomicPairKey]=encoded;save=Reset(s);save.OnLoadData();ModRuntime.Start(false);EconomicBridge.Capture=new EconomicSavePair(c,p);
  CivicFundingBridge.CaptureOverride=new byte[SC4Evolution.Simulation.CivicFundingLedgerCodec.MaximumPayloadBytes+1];
  Throws(delegate{save.OnSaveData();},"oversized Civic capture fails preflight");Check(s.Writes.Count==0,"oversized Civic state is rejected before any storage write");

  // The EconomicStartPreparation snapshot controls Civic preparation even if a
  // callback changes configuration. Work that can touch external hook managers
  // stays outside StateLock; publishing/stopping a bridge remains inside it.
  s=new Storage();save=Reset(s);EconomicBridge.CivicConfigured=true;bool prepareProbe=false,disposeProbe=false;
  CivicFundingBridge.PrepareCallback=delegate{prepareProbe=CanAcquireStateLock();EconomicBridge.CivicConfigured=false;};
  CivicFundingPreparation.DisposeCallback=delegate{disposeProbe=CanAcquireStateLock();};
  CivicFundingBridge.StartCallback=delegate{Check(CivicFundingBridge.LastStartUnderLock,"Civic bridge Start runs under StateLock");};
  ModRuntime.Start(false);
  Check(prepareProbe&&disposeProbe,"Civic prepare and preparation disposal run outside StateLock");
  Check(CivicFundingBridge.LastPrepareEnabled&&CivicFundingBridge.LastStartEnabled&&CivicFundingBridge.LastPreparation.Enabled,"immutable Civic-enabled snapshot survives config callback mutation");
  Check(CivicFundingBridge.PrepareCalls==1&&CivicFundingBridge.StartCalls==1&&CivicFundingPreparation.DisposeCalls==1,"one Civic preparation is consumed and disposed");
  ModRuntime.Stop();Check(CivicFundingBridge.LastBridgeStopUnderLock&&CivicFundingBridge.LastReleaseHooksUnderLock==false,"Stop disarms bridge under lock and releases hooks after leaving it");
  Check(CivicFundingBridge.StopCalls==1&&CivicFundingBridge.ReleaseHooksCalls==1,"Stop and hook release each happen once");
  // A prior hook-release exception must not discard the replacement owner.
  s=new Storage();save=Reset(s);ModRuntime.Start(false);CivicFundingBridge oldCivic=CivicFundingBridge.Latest;CivicFundingBridge.ThrowOnReleaseHooks=true;
  ModRuntime.Start(true);Check(ModRuntime.IsLoaded&&CivicFundingBridge.Latest!=oldCivic,"previous hook-release failure does not mask successful replacement");

  // A stale outer Loading callback may be superseded during Civic preparation.
  // Even if both preparation disposals throw, the typed supersession survives,
  // the inner owner survives, and obsolete cleanup never uninstalls its UI.
  loading=new Loading();s=new Storage();save=Reset(s);ModRuntime.SetLoadedEconomicPair(c,p);EconomicBridge.CivicConfigured=true;
  CivicFundingPreparation.ThrowOnDispose=true;int disposeOutsideCount=0;
  CivicFundingPreparation.DisposeCallback=delegate{if(CanAcquireStateLock())disposeOutsideCount++;};
  CivicFundingBridge.PrepareCallback=delegate{CivicFundingBridge.PrepareCallback=null;new Loading().OnLevelLoaded(LoadMode.NewGame);};
  loading.OnLevelLoaded(LoadMode.LoadGame);
  Check(ModRuntime.IsLoaded&&EconomicBridge.Latest.NewCity&&CivicFundingBridge.StartCalls==1,"stale outer prepare cannot replace the inner Civic owner");
  Check(disposeOutsideCount==2&&CivicFundingPreparation.DisposeCalls==2,"failed and superseded preparations both dispose outside StateLock");
  Check(CivicFundingBridge.StopCalls==0&&SC4Evolution.Mod.UI.CabinetUI.Installs==1&&SC4Evolution.Mod.UI.CabinetUI.Uninstalls==0,"throwing preparation cleanup preserves the reentrant owner and UI");

  // Reentrant work from finally cleanup happens after the outer session was
  // published. Loading must recheck ownership after that external callback.
  s=new Storage();save=Reset(s);loading.OnLevelLoaded(LoadMode.NewGame);CivicFundingBridge.PrepareCallback=null;
  CivicFundingPreparation.DisposeCallback=delegate{CivicFundingPreparation.DisposeCallback=null;new Loading().OnLevelLoaded(LoadMode.NewGame);};
  loading.OnLevelLoaded(LoadMode.LoadGame);
  Check(ModRuntime.IsLoaded&&EconomicBridge.Latest.NewCity&&SC4Evolution.Mod.UI.CabinetUI.Installs==2,"dispose-callback inner Loading supersedes stale outer UI install");
  Check(SC4Evolution.Mod.UI.CabinetUI.Uninstalls==0,"obsolete post-dispose Loading callback leaves inner UI installed");

  s=new Storage();save=Reset(s);loading.OnLevelLoaded(LoadMode.NewGame);CivicFundingBridge.ReleaseHooksCallback=delegate{CivicFundingBridge.ReleaseHooksCallback=null;new Loading().OnLevelLoaded(LoadMode.NewGame);};
  loading.OnLevelLoaded(LoadMode.LoadGame);
  Check(ModRuntime.IsLoaded&&EconomicBridge.Latest.NewCity&&SC4Evolution.Mod.UI.CabinetUI.Installs==2,"old-hook release inner Loading supersedes stale outer UI install");
  Check(CivicFundingBridge.Latest.InstanceStopCalls==0&&SC4Evolution.Mod.UI.CabinetUI.Uninstalls==0,"stale outer cleanup does not stop inner owner or uninstall UI");

  s=new Storage();save=Reset(s);loading.OnLevelLoaded(LoadMode.NewGame);CivicFundingBridge.ReleaseHooksCallback=delegate{CivicFundingBridge.ReleaseHooksCallback=null;ModRuntime.Stop();};
  loading.OnLevelLoaded(LoadMode.LoadGame);
  Check(!ModRuntime.IsLoaded&&SC4Evolution.Mod.UI.CabinetUI.Installs==1,"Stop from old-hook release leaves no stale outer UI install");
  Check(SC4Evolution.Mod.UI.CabinetUI.Uninstalls==0,"typed post-cleanup supersession avoids duplicate UI cleanup");

  // The exceptional path needs the same post-finally ownership check. The
  // replacement Civic Start fails under StateLock; disposing the old hook lease
  // then reenters a complete inner Loading before the original error can escape.
  s=new Storage();save=Reset(s);loading.OnLevelLoaded(LoadMode.NewGame);
  int exceptionDisposeOutsideCount=0;CivicFundingPreparation.DisposeCallback=delegate{if(CanAcquireStateLock())exceptionDisposeOutsideCount++;};
  CivicFundingBridge.StartCallback=delegate{CivicFundingBridge.StartCallback=null;throw new IOException("outer civic Start failed");};
  CivicFundingBridge.ReleaseHooksCallback=delegate{CivicFundingBridge.ReleaseHooksCallback=null;new Loading().OnLevelLoaded(LoadMode.NewGame);};
  loading.OnLevelLoaded(LoadMode.LoadGame);
  Check(exceptionDisposeOutsideCount==2,"failed outer and reentrant inner preparations both dispose outside the lock");
  Check(ModRuntime.IsLoaded&&EconomicBridge.Latest.NewCity&&CivicFundingBridge.Latest.InstanceStopCalls==0,"cleanup reentry supersedes Start failure without stopping the inner owner");
  Check(SC4Evolution.Mod.UI.CabinetUI.Installs==2&&SC4Evolution.Mod.UI.CabinetUI.Uninstalls==0,"typed exceptional supersession preserves inner UI without stale cleanup");

  s=new Storage();save=Reset(s);loading.OnLevelLoaded(LoadMode.NewGame);
  CivicFundingBridge.StartCallback=delegate{CivicFundingBridge.StartCallback=null;throw new IOException("outer civic Start failed before cleanup Stop");};
  CivicFundingBridge.ReleaseHooksCallback=delegate{CivicFundingBridge.ReleaseHooksCallback=null;ModRuntime.Stop();};
  int cleanupSessionBefore=(int)typeof(ModRuntime).GetField("_session",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
  loading.OnLevelLoaded(LoadMode.LoadGame);
  int cleanupSessionAfter=(int)typeof(ModRuntime).GetField("_session",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
  Check(!ModRuntime.IsLoaded&&cleanupSessionAfter==cleanupSessionBefore+1,"cleanup Stop supersedes Civic Start failure exactly once");
  Check(SC4Evolution.Mod.UI.CabinetUI.Installs==1&&SC4Evolution.Mod.UI.CabinetUI.Uninstalls==0,"outer generic failure cleanup does not uninstall after cleanup Stop");
  Console.WriteLine("Persistence harness PASS: "+checks+" checks; production Runtime/Lifecycle/envelope; real codecs, stubbed game+owner contract; no mod build/game access.");
 }
}
