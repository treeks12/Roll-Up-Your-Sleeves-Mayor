using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
namespace UnityEngine { public struct Vector3 { public float x,y,z; } public struct Vector2 { public float x,y; } }
namespace ColossalFramework { public class Singleton<T> { public static T instance { get; set; } public static bool exists { get { return instance != null; } } } }
namespace ColossalFramework.Math { public struct Randomizer { public int Int32(uint value) { throw new Exception("Gate must not draw RNG"); } } }
namespace CitiesHarmony.API { public static class HarmonyHelper { public static bool IsHarmonyInstalled { get { return false; } } } }
namespace HarmonyLib {
 public class HarmonyMethod { public MethodInfo method; public HarmonyMethod(MethodInfo info) { method=info; } }
 public class Harmony { public Harmony(string id) { } public void Patch(MethodBase target,HarmonyMethod prefix=null,HarmonyMethod postfix=null,HarmonyMethod transpiler=null,HarmonyMethod finalizer=null) { throw new Exception("No native patch installation in pure harness"); } public void UnpatchAll(string id) { } }
 public class CodeInstruction { public OpCode opcode; public object operand; public List<Label> labels=new List<Label>(); public List<object> blocks=new List<object>(); public CodeInstruction(OpCode op,object value=null){opcode=op;operand=value;} public CodeInstruction(CodeInstruction copy){opcode=copy.opcode;operand=copy.operand;labels.AddRange(copy.labels);blocks.AddRange(copy.blocks);} }
}
namespace ICities { public class BuildingExtensionBase { public virtual void OnBuildingCreated(ushort id){} public virtual void OnBuildingReleased(ushort id){} public virtual void OnBuildingRelocated(ushort id){} } public class DemandExtensionBase { public virtual int OnCalculateResidentialDemand(int value){return value;} public virtual int OnCalculateCommercialDemand(int value){return value;} public virtual int OnCalculateWorkplaceDemand(int value){return value;} public virtual int OnUpdateDemand(int last,int next,int target){return next;} } }
public static class BuildConfig { public static string applicationVersion="stub"; }
public class ItemClass { public enum Service { Residential,Commercial,Industrial,Office } public enum SubService { None } public enum Level { Level1,Level2,Level3,Level4,Level5 } public enum Zone { Unzoned,ResidentialLow,ResidentialHigh,CommercialLow } public Service m_service=Service.Residential; public Level m_level=Level.Level1; public Zone GetZone(){return Zone.ResidentialLow;} }
public class BuildingInfo { public enum ZoningMode { Straight,CornerLeft,CornerRight } public ItemClass m_class=new ItemClass(); public ZoningMode m_zoningMode; }
public struct Building { [Flags] public enum Flags { None=0,Created=1,Demolishing=524288,Collapsed=4194304 } public struct Frame {} public Flags m_flags; public uint m_buildIndex; public UnityEngine.Vector3 m_position; public BuildingInfo Info; }
public struct Citizen { public enum Wealth { Low,Medium,High } public void SetHome(uint a,ushort b,uint c){} public void SetHotel(uint a,ushort b,uint c){} }
public struct CitizenUnit { public void SetWealthLevel(Citizen.Wealth level){} }
public class Buffer<T> { public T[] m_buffer=new T[64]; }
public class BuildingManager {
 public Buffer<Building> m_buildings=new Buffer<Building>(); public int Calls,Selections; public int Behavior; public BuildingInfo SelectedPrefab=new BuildingInfo(); public Action DuringCreate;
 public bool CreateBuilding(out ushort building,ref ColossalFramework.Math.Randomizer randomizer,BuildingInfo info,UnityEngine.Vector3 position,float angle,int length,uint index) {
  object state=SC4Evolution.Mod.Economy.GrowthGateBehaviorChecks.BeginGlobalCreate(this);
  bool result=false;Exception error=null;building=0;
  try {
   Calls++; if(Behavior==2) throw new Exception("native-before"); if(Behavior==1) return false;
   building=10; m_buildings.m_buffer[building]=new Building {m_flags=Building.Flags.Created,m_buildIndex=index,Info=info,m_position=position};
   if(DuringCreate!=null) DuringCreate(); if(Behavior==3) throw new Exception("native-after"); if(Behavior==4)return false; result=true;return result;
  }catch(Exception caught){error=caught;throw;}finally{SC4Evolution.Mod.Economy.GrowthGateBehaviorChecks.EndGlobalCreate(state,building,result,error);}
 }
 public BuildingInfo GetRandomBuildingInfo(ref ColossalFramework.Math.Randomizer randomizer,ItemClass.Service service,ItemClass.SubService sub,ItemClass.Level level,int width,int length,BuildingInfo.ZoningMode zone,int style){Selections++;return SelectedPrefab;}
 public void UpdateBuildingInfo(ushort id,ref Building data,BuildingInfo info){} public void ReleaseBuilding(ushort id){}
}
public class SimulationManager { public ColossalFramework.Math.Randomizer m_randomizer; public uint m_currentBuildIndex; }
public class ZoneManager { public short[] m_goodAreaFound; public uint m_lastBuildIndex; }
public struct ZoneBlock { public void SimulationStep(ushort id){} public bool IsGoodPlace(UnityEngine.Vector2 value){return true;} }
public class PrivateBuildingAI { public virtual void SimulationStep(ushort id,ref Building data,ref Building.Frame frame){} public virtual void BuildingUpgraded(ushort id,ref Building data){} public virtual void StartUpgrading(ushort id,ref Building data){} }
public class ResidentialBuildingAI : PrivateBuildingAI { public override void SimulationStep(ushort id,ref Building data,ref Building.Frame frame){} }
public static class DistrictPolicies { [Flags] public enum CityPlanning { None=0,NoRebuild=16384 } }
