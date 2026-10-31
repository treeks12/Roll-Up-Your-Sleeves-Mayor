using System;
using System.IO;
using SC4Evolution.Mod.Diagnostics;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Simulation;
class Program {
 static int checks;
 static void Check(bool b,string name){checks++;if(!b)throw new Exception(name);}
 static void Throws(Action action,string name){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Check(rejected,name);}
 static bool Same(byte[] a,byte[] b){if(a==null||b==null)return a==b;if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
 static byte[] Classes(Guid id){using(var stream=new MemoryStream()){ResidentialClassLedgerCodec.Write(stream,new ResidentialClassLedger(id,1,new byte[32]));return stream.ToArray();}}
 static byte[] Capacity(Guid id,byte[] c,long day){var state=new ResidentialCapacityLedgerState(id,ResidentialCapacityLedger.PolicyName,ResidentialCapacityLedger.PolicyVersion,ResidentialCapacityLedger.PolicyHash,ResidentialNominalCapacity.PolicyName,ResidentialNominalCapacity.PolicyVersion,ResidentialNominalCapacity.PolicyHash,day,0,0,-1,-1,new ResidentialCapacityRecord[0],new ResidentialCapacityWindow[3]);using(var stream=new MemoryStream()){ResidentialCapacityLedgerCodec.Write(stream,state,c);return stream.ToArray();}}
 static void Main(){
  Guid id=Guid.NewGuid();byte[] c=Classes(id),p=Capacity(id,c,0),pair=EconomicSaveEnvelope.Encode(c,p),noCapacity=EconomicSaveEnvelope.Encode(c,null),x,y;
  EconomicSaveRoundtripOracle.DecodeRequired(pair,out x,out y);Check(Same(c,x)&&Same(p,y),"real codec exact inner payloads");
  EconomicSaveRoundtripOracle.RequireSaved(pair,(byte[])pair.Clone(),null,null);checks++;
  EconomicSaveRoundtripOracle.RequireRestored(pair,(byte[])pair.Clone(),(byte[])pair.Clone(),null,null);checks++;
  byte[] opaqueLegacy={7,8,9};EconomicSaveRoundtripOracle.RequireSaved(pair,pair,opaqueLegacy,(byte[])opaqueLegacy.Clone());checks++;
  EconomicSaveRoundtripOracle.RequireRestored(pair,pair,pair,opaqueLegacy,opaqueLegacy);checks++;
  EconomicSaveRoundtripOracle.RequireSaved(noCapacity,noCapacity,null,null);checks++;
  EconomicSaveRoundtripOracle.RequireRestored(noCapacity,noCapacity,noCapacity,null,null);checks++;
  EconomicSaveRoundtripOracle.DecodeRequired(noCapacity,out x,out y);Check(Same(c,x)&&y==null,"legitimate capacity absence retained");
  Throws(delegate{EconomicSaveRoundtripOracle.RequireSaved(pair,null,opaqueLegacy,opaqueLegacy);},"legacy presence cannot substitute for absent authority");
  Throws(delegate{EconomicSaveRoundtripOracle.RequireSaved(null,pair,null,null);},"missing live authority fails");
  Throws(delegate{EconomicSaveRoundtripOracle.RequireSaved(pair,new byte[0],opaqueLegacy,opaqueLegacy);},"empty authority corrupt despite legacy");
  Throws(delegate{EconomicSaveRoundtripOracle.RequireSaved(pair,pair,null,opaqueLegacy);},"legacy cannot be invented");
  Throws(delegate{EconomicSaveRoundtripOracle.RequireSaved(pair,pair,opaqueLegacy,null);},"legacy cannot be removed");
  Throws(delegate{EconomicSaveRoundtripOracle.RequireSaved(pair,pair,opaqueLegacy,new byte[]{7,8,10});},"legacy cannot change");
  Throws(delegate{EconomicSaveRoundtripOracle.RequireRestored(pair,pair,pair,opaqueLegacy,null);},"reload legacy preservation");
  Throws(delegate{EconomicSaveRoundtripOracle.RequireSaved(pair,noCapacity,null,null);},"capacity presence lost on save rejected");
  Throws(delegate{EconomicSaveRoundtripOracle.RequireRestored(pair,pair,noCapacity,null,null);},"live reload capacity absence mismatch");
  byte[] changedCapacity=EconomicSaveEnvelope.Encode(c,Capacity(id,c,1));
  Throws(delegate{EconomicSaveRoundtripOracle.RequireSaved(pair,changedCapacity,null,null);},"valid changed capacity bytes rejected");
  Throws(delegate{EconomicSaveRoundtripOracle.RequireRestored(pair,changedCapacity,pair,null,null);},"restored storage envelope identity");
  Throws(delegate{EconomicSaveRoundtripOracle.RequireRestored(pair,pair,changedCapacity,null,null);},"restored live capacity identity");
  byte[] changedClass=EconomicSaveEnvelope.Encode(Classes(Guid.NewGuid()),null);
  Throws(delegate{EconomicSaveRoundtripOracle.RequireSaved(pair,changedClass,null,null);},"valid different class identity rejected");
  for(int i=0;i<pair.Length;i++){byte[] corrupt=(byte[])pair.Clone();corrupt[i]^=1;Throws(delegate{EconomicSaveRoundtripOracle.RequireSaved(pair,corrupt,opaqueLegacy,opaqueLegacy);},"corrupt authoritative byte "+i);}
  for(int n=0;n<pair.Length;n+=Math.Max(1,pair.Length/9)){byte[] partial=new byte[n];Array.Copy(pair,partial,n);Throws(delegate{EconomicSaveRoundtripOracle.RequireRestored(pair,partial,pair,null,null);},"partial authoritative reload "+n);}
  Check(Same(c,x),"source inner class bytes retained");
  checks+=StableCapacityRestoreChecks.Run();
  Console.WriteLine("Save roundtrip oracle PASS: "+checks+" pure checks using production oracle/envelope + real inner codecs; no native save/reload, city fixture or Mod build.");
 }
}
