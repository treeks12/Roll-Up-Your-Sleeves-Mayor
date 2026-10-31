using System;
using System.Collections.Generic;
namespace UnityEngine {
 public class Object {public static void Destroy(object o) {}}
 public struct Vector2 {public float x,y; public Vector2(float a,float b){x=a;y=b;}public static Vector2 zero=>new Vector2(0,0);}
 public struct Vector3 {public float x,y,z;public Vector3(float a,float b,float c=0){x=a;y=b;z=c;}public static Vector3 zero=>new Vector3(0,0);}
 public class RectOffset {public RectOffset(int a,int b,int c,int d){}}
 public static class Time {public static float realtimeSinceStartup;}
 public static class Mathf {public static float Max(float a,float b)=>Math.Max(a,b);public static float Min(float a,float b)=>Math.Min(a,b);public static float Clamp(float v,float a,float b)=>Math.Max(a,Math.Min(b,v));}
}
namespace ColossalFramework {public static class Singleton<T> where T:new(){public static T instance=new T();}}
namespace ColossalFramework.UI {
 using UnityEngine;
 public enum UIHorizontalAlignment{Center} public enum UIVerticalAlignment{Middle}
 public class UIMouseEventParameter {public void Use(){}}
 public class UIComponent {public string name,tooltip;public bool isVisible=true,isEnabled=true,isInteractive,canFocus;public UIComponent parent;public object gameObject=new object(); public Vector2 size; public Vector3 relativePosition;private Vector3? _absolute;
 public float width=>size.x;public float height=>size.y;public Vector3 absolutePosition{get=>_absolute??(parent==null?relativePosition:new Vector3(parent.absolutePosition.x+relativePosition.x,parent.absolutePosition.y+relativePosition.y));set=>_absolute=value;}
 public List<UIComponent> Children=new List<UIComponent>();
 public T AddUIComponent<T>() where T:UIComponent,new(){var v=new T();v.parent=this;Children.Add(v);return v;}
 public void RemoveUIComponent(UIComponent v){Children.Remove(v);v.parent=null;}
 public T Find<T>(string n) where T:class {foreach(var c in Children){if(c.name==n&&c is T t)return t;var r=c.Find<T>(n);if(r!=null)return r;}return null;}}
 public class UIPanel:UIComponent {public bool autoLayout,autoFitChildrenHorizontally,autoFitChildrenVertically,clipChildren;public object atlas,color;public string backgroundSprite;}
 public class UILabel:UIComponent {public object font,textColor,disabledTextColor,dropShadowColor;public Vector2 dropShadowOffset;public float textScale=1;public bool useDropShadow,autoSize,autoHeight,wordWrap,processMarkup;public RectOffset padding;public string text;public UIVerticalAlignment verticalAlignment;public UIHorizontalAlignment textAlignment;}
 public class UIButton:UIComponent {public object atlas,font,color,hoveredColor,pressedColor,focusedColor,disabledColor,textColor,hoveredTextColor,pressedTextColor,focusedTextColor,disabledTextColor;public float textScale=1;public string normalBgSprite,hoveredBgSprite,pressedBgSprite,focusedBgSprite,disabledBgSprite,text;public bool autoSize,wordWrap;public UIHorizontalAlignment textHorizontalAlignment;public UIVerticalAlignment textVerticalAlignment;public event Action<UIComponent,UIMouseEventParameter> eventClicked;public void Click(){if(isVisible&&isEnabled)eventClicked?.Invoke(this,new UIMouseEventParameter());}}
 public class UILibrary {public Dictionary<string,object> Items=new Dictionary<string,object>();public T Get<T>(string n) where T:class=>Items.TryGetValue(n,out var x)?x as T:null;}
 public class UIView:UIPanel {public static UILibrary library=new UILibrary();public static bool Modal;public Vector2 Screen=new Vector2(1920,1080);public static bool HasModalInput()=>Modal;public Vector2 GetScreenResolution()=>Screen;}
}
public struct InstanceID {public ushort Building;}
public static class WorldInfoPanel {public static InstanceID Current;public static InstanceID GetCurrentInstanceID()=>Current;}
public class CityServiceWorldInfoPanel {public ColossalFramework.UI.UIComponent component;public T Find<T>(string name) where T:class=>component.Find<T>(name);}
public class CityInfoPanel {public static CityInfoPanel instance;public ColossalFramework.UI.UIComponent component;public T Find<T>(string name) where T:class=>component.Find<T>(name);}
public class SimulationManager {public Queue<Action> Actions=new Queue<Action>();public static bool InSIM;public void AddAction(Action a)=>Actions.Enqueue(a);public void Run(){InSIM=true;try{while(Actions.Count>0)Actions.Dequeue()();}finally{InSIM=false;}}}
namespace SC4Evolution.Mod.Civic {
 public sealed class CivicFundingSnapshot {public ushort BuildingId{get;set;}public bool Supported{get;set;}=true;public bool CanApply{get;set;}=true;public string Status{get;set;}="TechnicalPatchFailure";public int? CapacityOverridePercent{get;set;}public int? CoverageOverridePercent{get;set;}public int GlobalDayPercent{get;set;}=100;public int GlobalNightPercent{get;set;}=90;public int GlobalCurrentPercent{get;set;}=100;public int GlobalAveragePercent{get;set;}=95;public int ResolvedCapacityPercent=>CapacityOverridePercent??100;public int ResolvedCoveragePercent=>CoverageOverridePercent??100;public int BudgetTargetCapacity=>200;public float CurrentRange=>500;public int? LastEffectiveCapacity=>null;}
 public sealed class CivicFundingBridge {public bool Enabled=true,Supported=true,CanApply=true;public int Gets,Applies;public int? E,C;public CivicFundingSnapshot Current;public bool Reject;
 private static void SIM(){if(!SimulationManager.InSIM)throw new Exception("Backend called off SIM");}
 public CivicFundingSnapshot GetSnapshot(ushort id){SIM();Gets++;return Current=new CivicFundingSnapshot{BuildingId=id,Supported=Supported,CanApply=CanApply,CapacityOverridePercent=E,CoverageOverridePercent=C};}
 public bool Apply(CivicFundingSnapshot expected,int? e,int? c,out string reason){SIM();Applies++;reason="TechnicalFailure";if(Reject||!ReferenceEquals(Current,expected)||expected.CapacityOverridePercent!=E||expected.CoverageOverridePercent!=C)return false;E=e;C=c;return true;}}
}
namespace SC4Evolution.Mod.Runtime {public static class ModRuntime {public static SC4Evolution.Mod.Civic.CivicFundingBridge CurrentCivicFundingBridge;public static bool IsLoaded=true;}}
