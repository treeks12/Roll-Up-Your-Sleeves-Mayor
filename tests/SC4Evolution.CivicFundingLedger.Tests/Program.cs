using System;
using System.IO;
using SC4Evolution.Simulation;
internal static class Program
{
    static int checks;
    static readonly Guid GuidValue = new Guid("1d07b194-8e55-462d-b92a-3c56427120ad");
    static CivicFacilityIdentity I(ushort id) { return I(id, 123, "Prefab", "AI", 3); }
    static CivicFacilityIdentity I(ushort id, uint index, string prefab, string ai, int kind) { return new CivicFacilityIdentity(id, index, prefab, ai, (CivicFundingArchetype)kind); }
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Throws<T>(Action action) where T : Exception { checks++; try { action(); } catch(T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    static byte[] Write(CivicFundingLedger value) { using(var s = new MemoryStream()) { CivicFundingLedgerCodec.Write(s,value); return s.ToArray(); } }
    static CivicFundingLedger Read(byte[] bytes) { using(var s = new MemoryStream(bytes)) return CivicFundingLedgerCodec.Read(s); }
    static bool Equal(byte[] a, byte[] b) { if(a.Length!=b.Length)return false; for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true; }
    static void Fix(byte[] b) { uint crc=uint.MaxValue; for(int i=0;i<b.Length-4;i++){crc^=b[i];for(int j=0;j<8;j++)crc=(crc>>1)^((crc&1)==0?0u:0xedb88320u);}crc=~crc;for(int i=0;i<4;i++)b[b.Length-4+i]=(byte)(crc>>(i*8)); }
    static void Bad(byte[] source,int offset,byte value) { byte[] b=(byte[])source.Clone();b[offset]=value;Fix(b);Throws<InvalidDataException>(delegate{Read(b);}); }
    static void Main() { try { Models();Codec();Limits();Console.WriteLine("PASS "+checks+" checks"); }catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;} }
    static void Models()
    {
        Check(typeof(CivicFacilityIdentity).IsSealed && typeof(CivicFundingRecord).IsSealed,"sealed values");
        foreach(var p in typeof(CivicFacilityIdentity).GetProperties())Check(p.GetSetMethod()==null,"immutable identity");
        foreach(var p in typeof(CivicFundingRecord).GetProperties())Check(p.GetSetMethod()==null,"immutable record");
        Throws<ArgumentException>(delegate{new CivicFundingLedger(Guid.Empty);});
        Throws<ArgumentOutOfRangeException>(delegate{I(0);});
        Throws<ArgumentOutOfRangeException>(delegate{I(1,0,"p","a",4);});
        Throws<ArgumentException>(delegate{I(1,0,"","a",0);});
        Throws<ArgumentException>(delegate{I(1,0,"p",null,0);});
        foreach(string invalid in new string[]{"\ud800","\udfff","x\ud800x"})Throws<ArgumentException>(delegate{I(1,0,invalid,"a",0);});
        Throws<ArgumentOutOfRangeException>(delegate{I(1,0,new string('x',1025),"a",0);});
        Throws<ArgumentOutOfRangeException>(delegate{I(1,0,new string('\u00e9',513),"a",0);});
        Check(I(1,uint.MaxValue,new string('\u00e9',512),"\ud83c\udfe5",0).NativeBuildIndex==uint.MaxValue,"byte bound and full native index");
        var l=new CivicFundingLedger(GuidValue);CivicFundingRecord r;
        Check(l.Set(I(2),null,120),"health local coverage120");Check(!l.Set(I(2),null,120),"unchanged");
        Check(l.TryGet(I(2),out r)&&r.CapacityFundingPercent==null&&r.CoverageFundingPercent==120,"relocation retains identity");
        var mismatches=new CivicFacilityIdentity[]{I(2,124,"Prefab","AI",3),I(2,123,"prefab","AI",3),I(2,123,"Prefab","Ai",3),I(2,123,"Prefab","AI",2)};
        foreach(var other in mismatches){Check(!l.TryGet(other,out r)&&r==null,"reused/mismatched absent");Check(l.Count==1&&l.TryGet(I(2),out r),"lookup preserves old");}
        Check(!l.TryGet(null,out r),"null absent");
        foreach(int p in new int[]{-1,121,int.MinValue,int.MaxValue}){Throws<ArgumentOutOfRangeException>(delegate{l.Set(I(2),p,null);});Throws<ArgumentOutOfRangeException>(delegate{l.Set(I(2),null,p);});}
        Check(l.TryGet(I(2),out r)&&r.CoverageFundingPercent==120,"bad mutation preserves");
        Throws<ArgumentException>(delegate{new CivicFundingRecord(I(1),null,null);});
        Throws<ArgumentNullException>(delegate{new CivicFundingRecord(null,0,null);});
        Check(l.Set(mismatches[0],0,null)&&!l.TryGet(I(2),out r)&&l.TryGet(mismatches[0],out r),"Set explicitly replaces reused ID");
        Check(l.Set(I(2),null,null)&&l.Count==0,"Set null/null explicit removal by ID");Check(!l.Remove(0)&&!l.Remove(2),"absent remove");
        l.Set(I(5),100,0);l.Set(I(1),120,null);var snapshot=l.GetRecords();Check(snapshot[0].Identity.BuildingId==1&&snapshot[1].Identity.BuildingId==5,"sorted");snapshot[0]=null;Check(l.GetRecords()[0]!=null,"defensive array");l.Remove(1);Check(snapshot.Length==2,"snapshot stable");
    }
    static void Codec()
    {
        var l=new CivicFundingLedger(GuidValue);var reverse=new CivicFundingLedger(GuidValue);
        for(ushort i=1;i<=4;i++)l.Set(I(i,i,"P","A",i-1),i%2==0?(int?)null:0,i%2==0?(int?)120:null);
        for(ushort i=4;i>=1;i--)reverse.Set(I(i,i,"P","A",i-1),i%2==0?(int?)null:0,i%2==0?(int?)120:null);
        byte[] b=Write(l);var loaded=Read(b);Check(loaded.LedgerId==GuidValue&&loaded.Count==4&&Equal(b,Write(loaded))&&Equal(b,Write(reverse)),"GUID/order exact");Check(Read(Write(new CivicFundingLedger(GuidValue))).Count==0,"empty");
        for(int i=0;i<b.Length;i++){byte[] c=(byte[])b.Clone();c[i]^=1;Throws<InvalidDataException>(delegate{Read(c);});}
        for(int i=0;i<b.Length;i++){byte[] c=new byte[i];Array.Copy(b,c,i);Throws<InvalidDataException>(delegate{Read(c);});}
        Bad(b,0,0);Bad(b,8,2);byte[] emptyGuid=(byte[])b.Clone();Array.Clear(emptyGuid,10,16);Fix(emptyGuid);Throws<InvalidDataException>(delegate{Read(emptyGuid);});
        Bad(b,28,1);Bad(b,26,5);Bad(b,30,0);Bad(b,47,1);Bad(b,64,1);Bad(b,36,4);Bad(b,37,121);Bad(b,39,254);
        byte[] inherited=(byte[])b.Clone();inherited[37]=inherited[38]=255;Fix(inherited);Throws<InvalidDataException>(delegate{Read(inherited);});
        Bad(b,41,0);Bad(b,42,5);Bad(b,43,255);Bad(b,44,0);Bad(b,45,5);Bad(b,46,255);
        foreach(string name in new string[]{"\u00e9","\u533b\u9662","\ud83c\udfe5",new string('\u00e9',512)}){var u=new CivicFundingLedger(GuidValue);u.Set(I(1,0,name,name,2),null,120);byte[] encoded=Write(u);Check(Equal(encoded,Write(Read(encoded))),"Unicode exact");}
        byte[][] malformed={new byte[]{0xc0,0xaf},new byte[]{0xc1,0xbf},new byte[]{0xc2,0x7f},new byte[]{0xe0,0x9f,0x80},new byte[]{0xed,0xa0,0x80},new byte[]{0xe1,0x80,0x7f},new byte[]{0xf0,0x8f,0x80,0x80},new byte[]{0xf4,0x90,0x80,0x80},new byte[]{0xf5,0x80,0x80,0x80},new byte[]{0xf1,0x80,0x80,0x7f},new byte[]{0xc2},new byte[]{0xe1,0x80},new byte[]{0xf1,0x80,0x80},new byte[]{0x80}};
        foreach(var sequence in malformed){var u=new CivicFundingLedger(GuidValue);u.Set(I(1,0,new string('p',sequence.Length),"a",0),1,null);byte[] encoded=Write(u);Array.Copy(sequence,0,encoded,43,sequence.Length);Fix(encoded);Throws<InvalidDataException>(delegate{Read(encoded);});}
        byte[] trailing=new byte[b.Length+1];Array.Copy(b,trailing,b.Length-4);Fix(trailing);Throws<InvalidDataException>(delegate{Read(trailing);});
        using(var stream=new MemoryStream()){stream.WriteByte(99);CivicFundingLedgerCodec.Write(stream,l);Check(stream.CanWrite,"writer open");stream.Position=1;Check(CivicFundingLedgerCodec.Read(stream).Count==4&&stream.CanRead,"offset reader open");}
        using(var stream=new ShortRead(b,3))Check(CivicFundingLedgerCodec.Read(stream).Count==4&&stream.CanRead,"short nonseek stream");
    }
    static void Limits()
    {
        var maximum=new CivicFundingLedger(GuidValue);for(int i=1;i<=65535;i++)maximum.Set(I((ushort)i,0,"p","a",0),0,null);
        Check(maximum.Count==65535&&Read(Write(maximum)).Count==65535,"maximum records");Check(maximum.Set(I(65535),null,120)&&maximum.Count==65535,"replace full");
        int remaining=CivicFundingLedgerCodec.MaximumPayloadBytes-34,count=(remaining+2062)/2063;var boundary=new CivicFundingLedger(GuidValue);
        for(int i=1;i<=count;i++){int left=count-i,size=Math.Min(2063,remaining-left*17),names=size-15,p=Math.Min(1024,names-1),a=names-p;boundary.Set(I((ushort)i,0,new string('p',p),new string('a',a),0),120,120);remaining-=size;}
        byte[] exact=Write(boundary);Check(remaining==0&&exact.Length==CivicFundingLedgerCodec.MaximumPayloadBytes&&Read(exact).Count==count,"exact16MiB");
        var last=boundary.GetRecords()[count-1];boundary.Set(I((ushort)count,0,last.Identity.PrefabName,last.Identity.AiTypeName+"a",0),120,120);
        using(var output=new MemoryStream()){output.WriteByte(42);Throws<InvalidDataException>(delegate{CivicFundingLedgerCodec.Write(output,boundary);});Check(output.Length==1&&output.ToArray()[0]==42&&output.CanWrite,"oversize prewrite no changes");}
        byte[] oversize=new byte[CivicFundingLedgerCodec.MaximumPayloadBytes+1];Throws<InvalidDataException>(delegate{Read(oversize);});using(var stream=new ShortRead(oversize,8192))Throws<InvalidDataException>(delegate{CivicFundingLedgerCodec.Read(stream);});
    }
    sealed class ShortRead:Stream
    {
        readonly MemoryStream inner;readonly int maximum;internal ShortRead(byte[] b,int max){inner=new MemoryStream(b);maximum=max;}
        public override bool CanRead{get{return true;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return false;}}
        public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
        public override int Read(byte[] b,int o,int c){return inner.Read(b,o,Math.Min(c,maximum));}public override void Flush(){}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}public override void SetLength(long v){throw new NotSupportedException();}public override void Write(byte[] b,int o,int c){throw new NotSupportedException();}protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
}
