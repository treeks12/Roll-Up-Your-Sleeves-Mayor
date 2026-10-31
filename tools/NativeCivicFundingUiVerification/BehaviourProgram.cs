using System;
using ColossalFramework;using ColossalFramework.UI;using UnityEngine;using SC4Evolution.Mod.UI;using SC4Evolution.Mod.Runtime;using SC4Evolution.Mod.Civic;
class Program {
 static int checks;static void Check(bool x,string message){checks++;if(!x)throw new Exception(message);}
 sealed class Fixture {public UIView View=new UIView();public UIPanel Host=new UIPanel();public CivicFundingBridge Bridge=new CivicFundingBridge();public NativeCivicFundingUI UI;
 public Fixture(){Singleton<SimulationManager>.instance=new SimulationManager();UIView.library=new UILibrary();UIView.Modal=false;Time.realtimeSinceStartup=0;ModRuntime.IsLoaded=true;ModRuntime.CurrentCivicFundingBridge=Bridge;WorldInfoPanel.Current=new InstanceID{Building=10};Host.size=new Vector2(450,550);Host.absolutePosition=new Vector3(100,100);Host.clipChildren=true;var style=Host.AddUIComponent<UIPanel>();style.name="Wrapper";var label=Host.AddUIComponent<UILabel>();label.name="Upkeep";UIView.library.Items["CityServiceWorldInfoPanel"]=new CityServiceWorldInfoPanel{component=Host};CityInfoPanel.instance=new CityInfoPanel{component=new UIPanel()};CityInfoPanel.instance.component.AddUIComponent<UIButton>().name="PoliciesButton";UI=new NativeCivicFundingUI(View);}
 public void Load(){UI.Refresh();Check(UI.Pending,"query pending");Singleton<SimulationManager>.instance.Run();UI.Refresh();Check(UI.Panel!=null&&UI.Panel.isVisible,"loaded panel");}
 public void Complete(){Singleton<SimulationManager>.instance.Run();UI.Refresh();}
 }
 static void Main(){
 var f=new Fixture();f.Bridge.Enabled=false;for(int i=0;i<4;i++){Time.realtimeSinceStartup+=2;f.UI.Refresh();}Check(f.Bridge.Gets==0&&Singleton<SimulationManager>.instance.Actions.Count==0&&f.UI.Panel==null,"off no polling");
 f=new Fixture();f.Load();Check(f.Bridge.Gets==1,"one SIM sample");Check(!f.Host.clipChildren,"only own clip changed");Check(f.Host.size.x==450&&f.Host.size.y==550&&f.Host.absolutePosition.x==100,"host preserved");Check(f.UI.Panel.absolutePosition.x>=554,"no host overlap");
 f.Bridge.C=85;Time.realtimeSinceStartup=2;f.UI.Refresh();f.Complete();f.UI.CapacityMinus.Click();Check(f.UI.Pending&&!f.UI.CoveragePlus.isEnabled,"single pending");f.Complete();Check(f.Bridge.E==95&&f.Bridge.C==85,"capacity preserves coverage");f.UI.CapacityInherit.Click();f.Complete();Check(f.Bridge.E==null&&f.Bridge.C==85,"capacity inherit only");f.UI.CoverageInherit.Click();f.Complete();Check(f.Bridge.E==null&&f.Bridge.C==null,"coverage inherit only");f.Bridge.Reject=true;f.UI.CapacityPlus.Click();f.Complete();Check(f.UI.Panel.Find<UILabel>("SC4EvolutionCivicFundingStatus").text.StartsWith("Alteração não aplicada"),"generic rejected command");Check(f.UI.Status=="TechnicalFailure","technical diagnostic retained");Check(!f.UI.Panel.Find<UILabel>("SC4EvolutionCivicFundingStatus").tooltip.Contains("Technical"),"no raw diagnostic tooltip");f.UI.Release();Check(f.Host.clipChildren&&f.UI.Panel==null&&!f.UI.Pending,"release owned controls and clip");f.UI.Release();
 f=new Fixture();f.UI.Refresh();WorldInfoPanel.Current=new InstanceID{Building=11};f.UI.Refresh();f.Complete();Check(f.Bridge.Gets==1&&f.UI.Snapshot.BuildingId==11,"stale queued selection skipped");
 f=new Fixture();f.Load();f.UI.CapacityPlus.Click();WorldInfoPanel.Current=new InstanceID{Building=11};f.UI.Refresh();f.Complete();Check(f.Bridge.Applies==0,"selection cancels command");
 f=new Fixture();f.Load();f.UI.CapacityPlus.Click();UIView.Modal=true;f.UI.Refresh();f.Complete();Check(f.Bridge.Applies==0&&f.UI.Panel==null&&f.Host.clipChildren,"modal invalidates and releases");
 f=new Fixture();f.Load();f.UI.CapacityPlus.Click();f.Bridge.Enabled=false;f.UI.Refresh();f.Complete();Check(f.Bridge.Applies==0&&f.UI.Panel==null,"off invalidates pending command");
 f=new Fixture();f.Load();f.UI.CapacityPlus.Click();ModRuntime.CurrentCivicFundingBridge=new CivicFundingBridge();f.UI.Refresh();f.Complete();Check(f.Bridge.Applies==0,"bridge replaced command skipped");
 f=new Fixture();f.UI.Refresh();f.UI.Release();f.Complete();Check(f.Bridge.Gets==0&&!f.UI.Pending,"release pending skips backend");
 f=new Fixture();f.Bridge.Supported=false;f.UI.Refresh();f.Complete();Check(f.UI.Panel==null,"unsupported hidden");
 f=new Fixture();f.Bridge.CanApply=false;f.Load();Check(f.UI.Panel.height==108&&!f.UI.CapacityPlus.isVisible,"unavailable compact no controls");Check(!f.UI.Panel.Find<UILabel>("SC4EvolutionCivicFundingEffects").isVisible,"unavailable no effects");Check(!f.UI.Panel.Find<UILabel>("SC4EvolutionCivicFundingStatus").text.Contains("Technical"),"unavailable Portuguese only");
 f=new Fixture();f.View.Screen=new Vector2(600,600);f.UI.Refresh();f.Complete();Check(f.UI.Panel!=null&&!f.UI.Panel.isVisible,"no screen room hides");Check(f.Host.size.x==450&&f.Host.size.y==550,"no room preserves host");f.UI.Release();Check(f.Host.clipChildren,"no room cleanup");
 f=new Fixture();f.Host.autoLayout=true;f.UI.Refresh();f.Complete();Check(f.UI.Panel==null&&f.Host.clipChildren,"managed native layout untouched");
 f=new Fixture();f.Load();var dock=f.UI.Panel.parent;f.Host.RemoveUIComponent(dock);var alien=new UIPanel();alien.Children.Add(dock);dock.parent=alien;f.UI.Release();Check(!f.Host.clipChildren,"no ownership no clip restore");
 Console.WriteLine(checks+" UI ordering/cleanup assertions PASS (stub-only; no game runtime)");
 }
}
