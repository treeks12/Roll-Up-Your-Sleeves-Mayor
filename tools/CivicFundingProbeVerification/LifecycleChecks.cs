#pragma warning disable 0067, 0169, 0414, 0649
using System; using System.Collections.Generic; using System.IO;
public sealed class CivicLifecycleChecks
{
    // ACTUAL_METHODS
    private static bool _quitCommitted;
    private readonly object _gate = new object();
    private readonly List<OwnedFacility> _owned = new List<OwnedFacility>();
    private readonly List<ushort> _roadSegments = new List<ushort>();
    private readonly List<ushort> _roadNodes = new List<ushort>();
    private readonly List<Budget> _budgets = new List<Budget>();
    private readonly List<string> _receipts = new List<string>();
    private Owner _owner; private SimulationManager _simulation; private SimulationMetaData _metadata;
    private BuildingManager _buildingManager; private CitizenManager _citizenManager; private NetManager _netManager; private EconomyManager _economy;
    private Building[] _buildingBuffer; private CitizenUnit[] _unitBuffer; private Citizen[] _citizenBuffer; private NetNode[] _nodeBuffer; private NetSegment[] _segmentBuffer;
    private CivicFundingBridge _bridge;
    private bool _pending,_interrupted,_leaseHeld,_oldForcedPause,_cleanupQueued,_cleanupFinished,_cleaned,_verified,_subscribed,_pauseRestorationUnproved,_releasedCommandRejected;
    private string _failure,_cleanupError,_phase; private int _revision;
    private void OnLog(string message,string stack,LogType type){}
    private bool Matches(OwnedFacility facility){return _buildingBuffer[facility.Id].Alive;}
    private CivicFundingSnapshot Snapshot(OwnedFacility facility){return new CivicFundingSnapshot{BuildingId=facility.Id};}
    private List<uint> OwnedUnitIds(OwnedFacility facility){return new List<uint>();}
    private void RequireReleasedFacility(OwnedFacility facility,List<uint> units){Require(!_buildingBuffer[facility.Id].Alive,"facade release incomplete");}
    private static string Json(string value){return value??"null";}
    private static int count;
    private static void Check(bool value,string reason){++count;if(!value)throw new Exception(reason);}
    private static CivicLifecycleChecks Fixture()
    {
        var p=new CivicLifecycleChecks();p._owner=new Owner{_createdCity=true,_session="owned-guid"};
        p._metadata=new SimulationMetaData{m_gameInstanceIdentifier="owned-guid"};
        p._simulation=Singleton<SimulationManager>.instance=new SimulationManager{m_metaData=p._metadata,ForcedSimulationPaused=true};
        p._buildingManager=Singleton<BuildingManager>.instance=new BuildingManager();
        p._citizenManager=Singleton<CitizenManager>.instance=new CitizenManager();
        p._netManager=Singleton<NetManager>.instance=new NetManager();
        p._economy=Singleton<EconomyManager>.instance=new EconomyManager();
        p._buildingBuffer=p._buildingManager.m_buildings.m_buffer;p._unitBuffer=p._citizenManager.m_units.m_buffer;
        p._citizenBuffer=p._citizenManager.m_citizens.m_buffer;p._nodeBuffer=p._netManager.m_nodes.m_buffer;p._segmentBuffer=p._netManager.m_segments.m_buffer;
        p._bridge=ModRuntime.CurrentCivicFundingBridge=new CivicFundingBridge{Ready=true};
        var c=new ItemClass{m_service=ItemClass.Service.Education,m_subService=ItemClass.SubService.None};var info=new BuildingInfo{m_class=c};
        p._budgets.Add(new Budget{Info=info,Class=c,Service=c.m_service,SubService=c.m_subService,Day=50,Night=75});
        for(ushort id=1;id<=4;id++){p._owned.Add(new OwnedFacility{Id=id});p._buildingBuffer[id].Alive=true;}
        p._leaseHeld=true;p._verified=true;return p;
    }
    public static int Run()
    {
        var p=Fixture();p.RequestCleanup();Check(!p._cleanupFinished&&p._cleanupQueued,"cleanup enqueued");
        Check(p._simulation.ForcedSimulationPaused&&p._buildingManager.Releases==0,"pause held before FIFO callback");
        p._simulation.Drain();Check(p._cleaned&&p.CleanupReady&&p._buildingManager.Releases==4,"normal native facade cleanup");
        Check(!p._simulation.ForcedSimulationPaused&&p._economy.Writes==2,"restore follows release");Check(p._releasedCommandRejected,"released snapshot rejected");
        p=Fixture();p.CancelBeforePluginRemoval();ModRuntime.CurrentCivicFundingBridge=null;p._simulation.Drain();
        Check(p._cleaned&&p._buildingManager.Releases==4,"stopped bridge permits exact-scene cleanup");
        Check(!p._verified&&p._failure!=null&&p._phase=="ReleasedAfterFailure","cancel never verified");Check(!p._simulation.ForcedSimulationPaused,"cancel cleanup before unpause");
        p=Fixture();p.BeforeLevelUnload();ModRuntime.CurrentCivicFundingBridge=null;p._simulation.Drain();
        Check(p._cleaned&&p._buildingManager.Releases==4&&!p._simulation.ForcedSimulationPaused,"unload FIFO survives bridge Stop");
        p=Fixture();p._simulation.RejectQueue=true;p.RequestCleanup();
        Check(p._cleanupFinished&&!p._cleanupQueued&&p._cleanupError!=null,"queue rejection terminal observable");
        Check(!p.CleanupReady&&p.MayQuitUnderOwnedPause&&p._simulation.ForcedSimulationPaused,"queue rejection retains pause and explicit quit gate");
        Check(p._buildingManager.Releases==0&&p._economy.Writes==0,"queue rejection no native mutations");
        p=Fixture();p._buildingManager.FailRelease=true;p.RequestCleanup();p._simulation.Drain();
        Check(!p._cleaned&&!p.CleanupReady&&p._cleanupError!=null,"release failure cannot claim cleanup");
        Check(p._simulation.ForcedSimulationPaused&&p.MayQuitUnderOwnedPause,"release failure never resumes city");
        p=Fixture();p.RequestCleanup();p._simulation.m_metaData=new SimulationMetaData{m_gameInstanceIdentifier="owned-guid"};p._simulation.Drain();
        Check(p._buildingManager.Releases==0&&p._economy.Writes==0,"same-string metadata replacement refuses writes");
        Check(!p.CanQuitCurrentScene&&!p.MayQuitUnderOwnedPause&&p._pauseRestorationUnproved,"foreign metadata cannot quit and restoration unproved");
        Check(p._simulation.ForcedSimulationPaused,"foreign pause untouched; leak limitation explicit");
        p=Fixture();p.RequestCleanup();p._buildingManager.m_buildings.m_buffer=new Building[16];p._simulation.Drain();
        Check(p._buildingManager.Releases==0&&p._economy.Writes==0&&!p.CanQuitCurrentScene,"buffer replacement refuses all writes/quit");
        p=Fixture();p.RequestCleanup();ModRuntime.CurrentCivicFundingBridge=new CivicFundingBridge{Ready=true};p._simulation.Drain();
        Check(p._buildingManager.Releases==0&&p._economy.Writes==0&&p._simulation.ForcedSimulationPaused,"different live bridge refused");
        Check(!p.CanQuitCurrentScene&&!p.MayQuitUnderOwnedPause,"different bridge cannot authorize process exit");
        p=Fixture();p._budgets[0].Class.m_service=ItemClass.Service.Other;p.RequestCleanup();p._simulation.Drain();
        Check(p._economy.Writes==0&&!p._cleaned&&p._cleanupError!=null,"mutable class key blocks budget restore");
        Check(p._simulation.ForcedSimulationPaused&&p.MayQuitUnderOwnedPause,"budget restore failure retains own pause");
        p=Fixture();p._pending=true;p._simulation.AddAction(delegate{p._pending=false;});p.RequestCleanup();
        Check(!p.CleanupReady,"accepted preceding work not prematurely settled");p._simulation.Drain();Check(p.CleanupReady&&p._buildingManager.Releases==4,"FIFO drains prior work before cleanup");
        p=Fixture();p._roadSegments.Add(5);p._roadSegments.Add(6);p._roadNodes.Add(7);p._roadNodes.Add(8);p._roadNodes.Add(9);
        p._segmentBuffer[5].m_flags=NetSegment.Flags.Created;p._segmentBuffer[6].m_flags=NetSegment.Flags.Created;
        p._nodeBuffer[7].m_flags=NetNode.Flags.Created;p._nodeBuffer[8].m_flags=NetNode.Flags.Created;p._nodeBuffer[9].m_flags=NetNode.Flags.Created;
        p.RequestCleanup();p._simulation.Drain();
        Check(p._cleaned&&p._buildingManager.Releases==4&&p._netManager.SegmentReleases==2&&p._netManager.NodeReleases==3,"access road released after facilities");
        Check((p._segmentBuffer[5].m_flags&NetSegment.Flags.Created)==0&&(p._nodeBuffer[9].m_flags&NetNode.Flags.Created)==0,"road release clears every created flag");
        p=Fixture();p._roadSegments.Add(5);p._segmentBuffer[5].m_flags=NetSegment.Flags.Created;p._netManager.FailSegmentRelease=true;p.RequestCleanup();p._simulation.Drain();
        Check(!p._cleaned&&p._cleanupError!=null&&p._buildingManager.Releases==4,"segment release failure cannot claim cleanup");
        Check(p._simulation.ForcedSimulationPaused&&p.MayQuitUnderOwnedPause,"road release failure retains own pause");
        return count;
    }
    private sealed class Owner{public bool _createdCity;public string _session;}
    private sealed class SimulationMetaData{public string m_gameInstanceIdentifier;}
    private sealed class SimulationManager
    {
        public SimulationMetaData m_metaData;public bool ForcedSimulationPaused,RejectQueue;readonly Queue<Action> actions=new Queue<Action>();
        public void AddAction(Action a){if(RejectQueue)throw new Exception("rejected");actions.Enqueue(a);}public void Drain(){while(actions.Count>0)actions.Dequeue()();}
    }
    private struct Building{public bool Alive;}private struct CitizenUnit{}private struct Citizen{}
    private struct NetNode{public Flags m_flags;public int Segments;public int CountSegments(){return Segments;}public enum Flags{Created=1}}
    private struct NetSegment{public Flags m_flags;public enum Flags{Created=1}}
    private sealed class Buffer<T>{public T[] m_buffer=new T[16];}
    private sealed class BuildingManager{public Buffer<Building> m_buildings=new Buffer<Building>();public int Releases;public bool FailRelease;public void ReleaseBuilding(ushort id){++Releases;if(FailRelease)throw new Exception("release failed");m_buildings.m_buffer[id].Alive=false;}}
    private sealed class CitizenManager{public Buffer<CitizenUnit>m_units=new Buffer<CitizenUnit>();public Buffer<Citizen>m_citizens=new Buffer<Citizen>();}
    private sealed class NetManager{public Buffer<NetNode>m_nodes=new Buffer<NetNode>();public Buffer<NetSegment>m_segments=new Buffer<NetSegment>();
        public int SegmentReleases,NodeReleases;public bool FailSegmentRelease;
        public void ReleaseSegment(ushort id,bool collapse){++SegmentReleases;if(FailSegmentRelease)throw new Exception("segment release failed");m_segments.m_buffer[id].m_flags&=~NetSegment.Flags.Created;}
        public void ReleaseNode(ushort id){++NodeReleases;m_nodes.m_buffer[id].m_flags&=~NetNode.Flags.Created;}}
    private sealed class ItemClass{public enum Service{Education,Other}public enum SubService{None}public Service m_service;public SubService m_subService;}
    private sealed class BuildingInfo{public ItemClass m_class;}
    private sealed class EconomyManager{int day=100,night=100;public int Writes;public void SetBudget(ItemClass.Service s,ItemClass.SubService u,int v,bool n){++Writes;if(n)night=v;else day=v;}public int GetBudget(ItemClass c,bool n){return n?night:day;}}
    private static class Singleton<T>where T:class{public static T instance;public static bool exists{get{return instance!=null;}}}
    private sealed class CivicFundingSnapshot{public ushort BuildingId;}
    private sealed class CivicFundingBridge{public bool Ready;public bool Apply(CivicFundingSnapshot snapshot,int?e,int?c,out string reason){reason="rejected";return false;}}
    private static class ModRuntime{public static CivicFundingBridge CurrentCivicFundingBridge;}
    private enum LogType{Error}
    private static class Application{public static event Action<string,string,LogType> logMessageReceivedThreaded;}
    private struct Vector3{}
}
