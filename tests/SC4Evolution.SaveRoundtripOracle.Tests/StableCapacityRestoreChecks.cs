using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SC4Evolution.Mod.Diagnostics;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Simulation;

internal static class StableCapacityRestoreChecks
{
    private static int _checks;
    private sealed class Fixture
    {
        internal byte[] Classes, Capacity, Envelope;
        internal ResidentialCapacityLedgerState State;
    }
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { bool failed = false; try { action(); } catch (T) { failed = true; } Check(failed, message); }
    private static bool Same(byte[] a, byte[] b)
    { if (a == null || b == null) return a == b; if (a.Length != b.Length) return false; for (int i=0;i<a.Length;i++) if(a[i]!=b[i]) return false; return true; }
    private static byte[] EncodeCapacity(ResidentialCapacityLedgerState state, byte[] classes)
    { using (MemoryStream stream=new MemoryStream()) { ResidentialCapacityLedgerCodec.Write(stream,state,classes); return stream.ToArray(); } }
    private static ResidentialCapacityLedgerState Decode(byte[] envelope, out byte[] classes)
    { byte[] capacity; EconomicSaveRoundtripOracle.DecodeRequired(envelope,out classes,out capacity); using(MemoryStream stream=new MemoryStream(capacity,false)) return ResidentialCapacityLedgerCodec.ReadBound(stream,classes); }
    private static ResidentialCapacityLedgerState State(Fixture source, ResidentialCapacityRecord[] records, ResidentialCapacityWindow[] windows,
        long external, long publishedStock, long publishedExternal, long stock=9, long firstDay=10)
    { return new ResidentialCapacityLedgerState(source.State.ClassLedgerId,ResidentialCapacityLedger.PolicyName,ResidentialCapacityLedger.PolicyVersion,ResidentialCapacityLedger.PolicyHash,
        ResidentialNominalCapacity.PolicyName,ResidentialNominalCapacity.PolicyVersion,ResidentialNominalCapacity.PolicyHash,
        firstDay,stock,external,publishedStock,publishedExternal,records,windows); }
    private static byte[] Envelope(Fixture source, ResidentialCapacityLedgerState state)
    { return EconomicSaveEnvelope.Encode(source.Classes,EncodeCapacity(state,source.Classes)); }
    private static Fixture Populated(bool otherWindows, Guid? suppliedId = null)
    {
        Guid id=suppliedId ?? new Guid("c33ceab2-2af1-4571-b18a-a3e1065743cc");
        ResidentialClassLedger classes=new ResidentialClassLedger(id,1,new byte[32]);
        ResidentialCapacityRecord[] records=new ResidentialCapacityRecord[3];
        for(int i=0;i<3;i++)
        {
            ResidentialLotIdentity identity=new ResidentialLotIdentity((ushort)(i+1),(uint)(101+i),0,0,"Stable House "+i,1,2,2,2);
            ResidentialClass value=(ResidentialClass)(i+1);ResidentialLotRecord lot=i==1 ? classes.ImportExisting(identity,value) : classes.CommitCreated(identity,value);
            records[i]=new ResidentialCapacityRecord(id,identity,lot.Generation,value,ResidentialNominalCategory.LowGeneric,0,1,ResidentialCapacityPhase.Completed,lot.Origin);
        }
        ResidentialCapacityWindow[] windows=otherWindows ? new ResidentialCapacityWindow[] {
            new ResidentialCapacityWindow(ResidentialClass.Low,11,ResidentialCapacityWindowPhase.NotQualified,BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)),0),1.5f,0,0,0,1,2), null,
            new ResidentialCapacityWindow(ResidentialClass.High,14,ResidentialCapacityWindowPhase.Open,10f,1.5f,40,60,3,4,4)
        } : new ResidentialCapacityWindow[] {
            new ResidentialCapacityWindow(ResidentialClass.Low,11,ResidentialCapacityWindowPhase.Open,20.125f,1.5f,80,120,13,4,4),
            new ResidentialCapacityWindow(ResidentialClass.Medium,12,ResidentialCapacityWindowPhase.Exhausted,6f,2f,24,48,26,3,2),
            new ResidentialCapacityWindow(ResidentialClass.High,14,ResidentialCapacityWindowPhase.Invalidated,10f,1.5f,40,60,100,1,1)
        };
        Fixture f=new Fixture(); using(MemoryStream stream=new MemoryStream()) { ResidentialClassLedgerCodec.Write(stream,classes); f.Classes=stream.ToArray(); }
        f.State=new ResidentialCapacityLedgerState(id,ResidentialCapacityLedger.PolicyName,ResidentialCapacityLedger.PolicyVersion,ResidentialCapacityLedger.PolicyHash,
            ResidentialNominalCapacity.PolicyName,ResidentialNominalCapacity.PolicyVersion,ResidentialNominalCapacity.PolicyHash,10,9,4,8,3,records,windows);
        f.Capacity=EncodeCapacity(f.State,f.Classes);f.Envelope=EconomicSaveEnvelope.Encode(f.Classes,f.Capacity);return f;
    }
    private static ResidentialCapacityWindow CopyWindow(ResidentialCapacityWindow w, ResidentialCapacityWindowPhase phase)
    { return w==null ? null : new ResidentialCapacityWindow(w.Class,w.Day,phase,w.OpeningRaw,w.Overbuild,w.DesiredQ,w.DesiredMaximumQ,w.CommittedQ,w.OpeningStockRevision,w.OpeningExternalRevision); }
    private static void VerifyTransitions(Fixture f)
    {
        byte[] untouched=(byte[])f.Envelope.Clone();
        foreach(bool raw in new[]{false,true})
        {
            byte[] expected=EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(f.Envelope,raw),c;
            ResidentialCapacityLedgerState state=Decode(expected,out c);
            Check(Same(c,f.Classes),"exact class bytes retained");Check(Same(f.Envelope,untouched),"input envelope not mutated");
            Check(state.ClassLedgerId==f.State.ClassLedgerId&&state.FirstPlanningDay==10&&state.StockRevision==9,"class GUID/first planning day/stock history retained");
            Check(state.ExternalRevision==(raw?5:4),"only RAW advances external once");
            Check(state.PublishedStockRevision==(raw?9:-1)&&state.PublishedExternalRevision==(raw?5:-1),"fresh RAW ack versus unacknowledged Shadow");
            Check(state.Records.Length==3,"populated records retained");
            Check(Same(expected,EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(f.Envelope,raw)),"deterministic repeated preparation");
            ResidentialCapacityWindow[] original=f.State.Windows,goldenWindows=new ResidentialCapacityWindow[3];
            for(int i=0;i<3;i++)
            {
                ResidentialCapacityWindow w=original[i],got=state.Windows[i];
                if(w==null){Check(got==null,"never opened window stays absent");continue;}
                ResidentialCapacityWindowPhase phase=raw&&w.Phase==ResidentialCapacityWindowPhase.Open ? ResidentialCapacityWindowPhase.Invalidated : w.Phase;
                goldenWindows[i]=CopyWindow(w,phase);
                Check(got.Phase==phase,"only open RAW window invalidated");
                Check(got.Day==w.Day&&got.CommittedQ==w.CommittedQ&&got.DesiredQ==w.DesiredQ&&got.DesiredMaximumQ==w.DesiredMaximumQ,"window day/quota/debit retained without renewal/refund");
                Check(got.OpeningStockRevision==w.OpeningStockRevision&&got.OpeningExternalRevision==w.OpeningExternalRevision,"opening history revisions retained");
                Check(Same(BitConverter.GetBytes(got.OpeningRaw),BitConverter.GetBytes(w.OpeningRaw))&&Same(BitConverter.GetBytes(got.Overbuild),BitConverter.GetBytes(w.Overbuild)),"opening float bits including negative zero retained");
            }
            // An independently assembled DTO is the golden wire expectation, not
            // a second Restore/Advance/Ack call copied from the production method.
            var golden=State(f,f.State.Records,goldenWindows,raw?5:4,raw?9:-1,raw?5:-1);
            Check(Same(expected,Envelope(f,golden)),"golden exact payload/envelope matches allowed changes only");
            EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,(byte[])f.Envelope.Clone(),expected,(byte[])expected.Clone(),new byte[]{8},new byte[]{8});_checks++;
            Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestored(f.Envelope,f.Envelope,expected,null,null);},"old strict oracle remains strict");
            Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,expected,expected,expected,null,null);},"stored bytes never transformed");
            Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,f.Envelope,expected,f.Envelope,null,null);},"live must match prepared branch exactly");
            Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,f.Envelope,expected,expected,new byte[]{8},new byte[]{9});},"optional legacy stays exact");
            Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,f.Envelope,expected,expected,null,new byte[]{8});},"optional legacy cannot appear");
            byte[] illegal=Envelope(f,State(f,state.Records,state.Windows,state.ExternalRevision,state.PublishedStockRevision,state.PublishedExternalRevision,stock:10));
            Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,f.Envelope,expected,illegal,null,null);},"valid unexpected stock revision forbidden");
            illegal=Envelope(f,State(f,state.Records,state.Windows,state.ExternalRevision,state.PublishedStockRevision,state.PublishedExternalRevision,firstDay:9));
            Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,f.Envelope,expected,illegal,null,null);},"valid unexpected first planning day forbidden");
        }
    }
    private static byte[] UncheckedEnvelope(byte[] classes,byte[] capacity)
    { using(MemoryStream stream=new MemoryStream()) using(BinaryWriter writer=new BinaryWriter(stream)){writer.Write(Encoding.ASCII.GetBytes("SC4EPAR1"));writer.Write((ushort)1);writer.Write(classes.Length);writer.Write(capacity.Length);writer.Write(classes);writer.Write(capacity);using(SHA256 sha=SHA256.Create())writer.Write(sha.ComputeHash(stream.ToArray()));return stream.ToArray();} }
    private static void RepairCrc(byte[] bytes)
    { uint crc=uint.MaxValue;for(int i=0;i<bytes.Length-4;i++){crc^=bytes[i];for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)==0?0u:0xEDB88320u);}Array.Copy(BitConverter.GetBytes(~crc),0,bytes,bytes.Length-4,4); }
    internal static int Run()
    {
        _checks=0;Fixture f=Populated(false);VerifyTransitions(f);VerifyTransitions(Populated(true));
        foreach(ResidentialCapacityPhase phase in new[]{ResidentialCapacityPhase.Constructing,ResidentialCapacityPhase.Upgrading,ResidentialCapacityPhase.Unavailable})
        {
            ResidentialCapacityRecord[] records=f.State.Records;ResidentialCapacityRecord r=records[0];
            records[0]=new ResidentialCapacityRecord(r.ClassLedgerId,r.Identity,r.Generation,r.Class,r.Category,r.NativeLevel,r.HomeCount,phase,r.Origin);
            byte[] unstable=Envelope(f,State(f,records,f.State.Windows,4,8,3));
            foreach(bool raw in new[]{false,true})Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(unstable,raw);},"non-Completed record rejects branch "+phase);
        }
        byte[] empty=Envelope(f,State(f,new ResidentialCapacityRecord[0],new ResidentialCapacityWindow[3],4,8,3));
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(empty,true);},"empty record set is not populated");
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(EconomicSaveEnvelope.Encode(f.Classes,null),false);},"absent capacity is not stable populated restore");
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(null,true);},"missing saved authority");
        byte[] overflow=Envelope(f,State(f,f.State.Records,new ResidentialCapacityWindow[3],long.MaxValue,8,long.MaxValue-1));
        Reject<OverflowException>(delegate{EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(overflow,true);},"RAW external revision overflow rejects without wrap");
        byte[] overflowShadow=EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(overflow,false),c;
        var shadowState=Decode(overflowShadow,out c);Check(shadowState.ExternalRevision==long.MaxValue&&shadowState.PublishedExternalRevision==-1,"Shadow can retain maximum revision without advancing");
        byte[] crossed;
        using(MemoryStream stream=new MemoryStream()){ResidentialClassLedgerCodec.Write(stream,new ResidentialClassLedger(Guid.NewGuid(),1,new byte[32]));crossed=UncheckedEnvelope(stream.ToArray(),f.Capacity);}
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(crossed,true);},"crossed valid class/capacity payloads reject");
        // Same ledger GUID/records still cannot replace the exact class bytes
        // against which the capacity payload was originally hashed.
        byte[] changedPolicyHash=new byte[32];changedPolicyHash[0]=1;
        var sameGuidClasses=new ResidentialClassLedger(f.State.ClassLedgerId,1,changedPolicyHash);
        foreach(var r in f.State.Records)
            if(r.Origin==ClassOrigin.Selected)sameGuidClasses.CommitCreated(r.Identity,r.Class);else sameGuidClasses.ImportExisting(r.Identity,r.Class);
        byte[] sameGuidBytes;using(MemoryStream stream=new MemoryStream()){ResidentialClassLedgerCodec.Write(stream,sameGuidClasses);sameGuidBytes=stream.ToArray();}
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(UncheckedEnvelope(sameGuidBytes,f.Capacity),true);},"same GUID with different exact class hash rejected");
        byte[] badPolicy=(byte[])f.Capacity.Clone();int policyVersion=64+BitConverter.ToUInt16(badPolicy,62);badPolicy[policyVersion]^=1;RepairCrc(badPolicy);
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(UncheckedEnvelope(f.Classes,badPolicy),true);},"unknown accounting policy rejects despite repaired CRC/SHA");
        byte[] badProfile=(byte[])f.Capacity.Clone();int profileName=policyVersion+4+32;int profileVersion=profileName+2+BitConverter.ToUInt16(badProfile,profileName);badProfile[profileVersion]^=1;RepairCrc(badProfile);
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(UncheckedEnvelope(f.Classes,badProfile),false);},"unknown nominal profile rejects despite repaired CRC/SHA");
        byte[] rawExpected=EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(f.Envelope,true);
        Fixture foreign=Populated(false,Guid.NewGuid());
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,f.Envelope,foreign.Envelope,foreign.Envelope,null,null);},"expected class cannot be replaced");
        byte[] shadowExpected=EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(f.Envelope,false);
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,f.Envelope,rawExpected,shadowExpected,null,null);},"declared RAW expectation cannot match Shadow live");
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,f.Envelope,shadowExpected,rawExpected,null,null);},"declared Shadow expectation cannot match RAW live");
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,f.Envelope,EconomicSaveEnvelope.Encode(f.Classes,null),EconomicSaveEnvelope.Encode(f.Classes,null),null,null);},"expected live cannot drop required populated capacity");
        byte[] rawClasses;var rawState=Decode(rawExpected,out rawClasses);var reopenedWindows=rawState.Windows;var opening=reopenedWindows[0];
        reopenedWindows[0]=new ResidentialCapacityWindow(opening.Class,opening.Day,ResidentialCapacityWindowPhase.Open,opening.OpeningRaw,opening.Overbuild,opening.DesiredQ,opening.DesiredMaximumQ,0,rawState.StockRevision,rawState.ExternalRevision);
        byte[] reopened=Envelope(f,State(f,rawState.Records,reopenedWindows,5,9,5));
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,f.Envelope,rawExpected,reopened,null,null);},"same-day reopened/refunded window forbidden");
        // One valid but unexpected record change must not be tolerated as live drift.
        var changed=f.State.Records;var first=changed[0];changed[0]=new ResidentialCapacityRecord(first.ClassLedgerId,first.Identity,first.Generation,first.Class,ResidentialNominalCategory.Other,first.NativeLevel,2,first.Phase,first.Origin);
        byte[] altered=Envelope(f,State(f,changed,f.State.Windows,4,8,3));
        Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(f.Envelope,f.Envelope,rawExpected,altered,null,null);},"record HOME/Q drift forbidden");
        for(int i=0;i<f.Envelope.Length;i++){byte[] broken=(byte[])f.Envelope.Clone();broken[i]^=1;Reject<InvalidDataException>(delegate{EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(broken,true);},"populated saved corruption byte "+i);}
        return _checks;
    }
}
