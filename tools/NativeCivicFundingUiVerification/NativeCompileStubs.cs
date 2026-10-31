using ColossalFramework.UI;
using SC4Evolution.Core;
namespace SC4Evolution.Mod.Civic
{
 public sealed class CivicFundingSnapshot
 {
  public ushort BuildingId{get;set;} public bool Supported{get;set;}public bool CanApply{get;set;}public string Status{get;set;}
  public int? CapacityOverridePercent{get;set;}public int? CoverageOverridePercent{get;set;}
  public int GlobalDayPercent{get;set;}public int GlobalNightPercent{get;set;}public int GlobalCurrentPercent{get;set;}public int GlobalAveragePercent{get;set;}
  public int ResolvedCapacityPercent{get;set;}public int ResolvedCoveragePercent{get;set;}public int BudgetTargetCapacity{get;set;}public float CurrentRange{get;set;}public int? LastEffectiveCapacity{get;set;}
 }
 public sealed class CivicFundingBridge
 {
  public bool Enabled {get{return true;}}
  public CivicFundingSnapshot GetSnapshot(ushort id){return null;}
  public bool Apply(CivicFundingSnapshot expected,int? capacity,int? coverage,out string reason){reason=null;return false;}
 }
}
namespace SC4Evolution.Mod.Runtime
{
 internal static class ModRuntime
 {
  internal static SC4Evolution.Mod.Civic.CivicFundingBridge CurrentCivicFundingBridge{get{return null;}}
  public static bool IsLoaded{get{return false;}}public static void Log(string message){}
 }
}
namespace SC4Evolution.Mod.UI
{
 internal sealed class NativeMunicipalUI
 {
  internal NativeMunicipalUI(UIView view){}
  internal UIPanel CouncilPanel{get{return null;}}internal UIPanel CouncilDock{get{return null;}}internal UIButton CouncilToggle{get{return null;}}internal UIScrollablePanel CouncilContent{get{return null;}}
  internal UIComponent CityHost{get{return null;}}internal UITextureSprite AdvisorPortrait{get{return null;}}internal int SelectedAdvisor{get{return -1;}}internal bool IsExpanded{get{return false;}}internal int NewsCount{get{return 0;}}
  internal UIButton ReadNoticeButton{get{return null;}}internal UIPanel MessagePanel{get{return null;}}internal UITextureSprite MessagePortrait{get{return null;}}internal UIButton MessageCloseButton{get{return null;}}internal UIButton MessageActionButton{get{return null;}}
  internal AdvisorBriefing MessageBriefing{get{return null;}}internal bool MessageVisible{get{return false;}}internal int PendingAlerts{get{return 0;}}
  internal UIButton GetAdvisorButton(int index){return null;}internal UITextureSprite GetAdvisorAvatar(int index){return null;}internal bool ReadSelectedNotice(){return false;}
  internal bool TryInstall(){return false;}internal void Refresh(bool force){}internal void Animate(){}internal void Toggle(){}internal void Release(){}
 }
}

// Facade only: this compilation checks native CS1 UI symbols, not Core ABI.
namespace SC4Evolution.Core { public sealed class AdvisorBriefing {} }
