#pragma warning disable 0649, 0436
using System;
using SC4Evolution.Core;
using SC4Evolution.Simulation;
using ColossalFramework.UI;
namespace SC4Evolution.Mod { internal static class SavedData { internal const string EconomicPairKey="SC4Evolution.EconomicPair.v1"; } }
namespace SC4Evolution.Mod.Economy {
 public sealed class CityCensus {
  public uint Frame; public int HomeHouseholds,OccupiedHomeHouseholds,UnmappedJobCapacity,UnmappedFilledJobs; public ResidentialStockSnapshot ResidentialStock;
  public int[] ResidentsByBootstrapWealth,WorkforceByBootstrapWealthEducation,ResidentsByLotClass,WorkforceByLotClassEducation,
   JobCapacityBySegment,FilledJobsBySegment,UnboundResidentsByNativeWealth,UnboundWorkforceByNativeWealthEducation;
  internal static CityCensus Capture(ResidentialClassBridge bridge){throw new NotSupportedException();}
 }
 public sealed class SegmentedDemandSnapshot { public CityCensus Census; public bool Converged,GameplayEnabled; public DemandNodeState[] Nodes; }
 internal sealed class ResidentialCapacityBridge { internal bool ModeRequested; internal bool Ready; internal int SessionEpoch; internal ResidentialCapacityStatus Published; }
 public sealed class EconomicBridge {
  internal ResidentialCapacityBridge Capacity; internal ResidentialClassBridge Classes; internal int DemandContextSession; public bool InitialSampleComplete;
  public byte[] GetClassSaveState(){throw new NotSupportedException();}
  public bool TryGetSavePair(out EconomicSavePair pair,out string reason){pair=null;reason=null;throw new NotSupportedException();}
 }
 internal sealed class ResidentialClassBridge { internal static bool TryIdentity(ushort id,out ResidentialLotIdentity identity){identity=null;throw new NotSupportedException();} }
}
namespace SC4Evolution.Mod.Runtime {
 internal static class ModRuntime {
  internal static bool IsLoaded; internal static CabinetReport CurrentReport; internal static SC4Evolution.Mod.Economy.SegmentedDemandSnapshot CurrentDemand;
  internal static SC4Evolution.Mod.Economy.EconomicBridge CurrentEconomicBridge; internal static SC4Evolution.Mod.Economy.ResidentialClassStatus CurrentResidentialClasses;
  internal static byte[] GetSaveState(){throw new NotSupportedException();} internal static byte[] GetEconomicSaveEnvelope(){throw new NotSupportedException();}
 }
}
namespace SC4Evolution.Mod.UI {
 public sealed class CabinetRoot : UIPanel { internal UIPanel CouncilPanel; internal bool CouncilExpanded; }
 public static class CabinetUI { public static void Toggle(){throw new NotSupportedException();} }
}
namespace SC4Evolution.Mod.Diagnostics {
 public sealed partial class AutonomousTestDriver {
  private void PrepareCapture(){throw new NotSupportedException();} private void WaitForCapture(){throw new NotSupportedException();}
  private void EnsureVisualTargetIdentity(UIComponent component){throw new NotSupportedException();} private string NativeVisualReportJson(){return "null";}
  private SC4Evolution.Mod.UI.CabinetRoot RequireCouncil(){throw new NotSupportedException();}
  // Civic is outside this historical organic/save slice. These inert methods
  // let the shared parent compile without executing or validating that mode.
  private sealed class NativeCivicFundingProbe {
   internal NativeCivicFundingProbe(AutonomousTestDriver owner){}
   internal bool CleanupReady {get{return false;}} internal bool CanQuitCurrentScene {get{return false;}}
   internal bool MayQuitUnderOwnedPause {get{return false;}}
   internal void Start(){throw new NotSupportedException();} internal bool Step(){throw new NotSupportedException();}
   internal void RequestCleanup(){throw new NotSupportedException();} internal void BeforeLevelUnload(){throw new NotSupportedException();}
   internal void CancelBeforePluginRemoval(){throw new NotSupportedException();} internal void NoteControlledQuit(){throw new NotSupportedException();}
   internal string ReportJson(){return "null";}
  }
  private sealed class NativeResidentialClassProbe {
   internal NativeResidentialClassProbe(AutonomousTestDriver owner){} internal bool CleanupReady {get{return false;}}
   internal void Start(){throw new NotSupportedException();} internal bool Step(){throw new NotSupportedException();}
   internal void RequestCleanup(){throw new NotSupportedException();} internal void BeforeLevelUnload(){throw new NotSupportedException();}
   internal string ReportJson(){return "null";}
  }
 }
}
