using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SC4Evolution.Simulation;

internal static class Program
{
    private static int _checks;
    private static readonly Guid Id = new Guid("842e298e-f531-47a2-a0da-2171f07b17aa");
    private sealed class Fixture
    {
        internal ResidentialClassLedger Classes;
        internal byte[] ClassBytes;
        internal ResidentialCapacityLedgerState State;
        internal byte[] Bytes;
    }
    private sealed class Layout
    {
        internal int AccountingName, AccountingVersion, AccountingHash, ProfileName, ProfileVersion, ProfileHash;
        internal int FirstDay, StockRevision, ExternalRevision, PublishedStock, PublishedExternal, Count, WindowCount;
        internal readonly List<Dictionary<string, int>> Records = new List<Dictionary<string, int>>();
        internal readonly List<Dictionary<string, int>> Windows = new List<Dictionary<string, int>>();
    }
    private static int Main()
    {
        try
        {
            RoundTripAndRestoration(); BoundClassContent(); MalformedWire(); SizeAndNameLimits(); StreamOwnership();
            Console.WriteLine("PASS: " + _checks + " capacity codec assertions across roundtrip/determinism, exact class binding/content, malformed wire/CRC/UTF8, limits and stream ownership. Staging pure source only; no native authority/game execution.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }

    private static Fixture Populated()
    {
        ResidentialClassLedger classes = new ResidentialClassLedger(Id, 1, new byte[32]);
        ResidentialCapacityRecord[] records = new ResidentialCapacityRecord[4];
        for (int i = 0; i < records.Length; i++)
        {
            ResidentialLotIdentity identity = Identity((ushort)(i + 1), "House á " + i);
            ResidentialClass lotClass = (ResidentialClass)(i % 3 + 1);
            ResidentialLotRecord lot = i % 2 == 0 ? classes.CommitCreated(identity, lotClass) : classes.ImportExisting(identity, lotClass);
            records[i] = new ResidentialCapacityRecord(Id, identity, lot.Generation, lotClass,
                i == 2 ? ResidentialNominalCategory.Other : ResidentialNominalCategory.LowGeneric, 0,
                i == 2 ? 2 : 1, (ResidentialCapacityPhase)(i + 1), lot.Origin);
        }
        classes.CommitCreated(Identity(5, "Class without capacity"), ResidentialClass.Medium);
        ResidentialCapacityWindow[] windows = {
            Window(ResidentialClass.Low, ResidentialCapacityWindowPhase.Open, 20.125f, 1.5f, 13, 4, 2),
            Window(ResidentialClass.Medium, ResidentialCapacityWindowPhase.Exhausted, 6f, 2f, 26, 3, 2),
            Window(ResidentialClass.High, ResidentialCapacityWindowPhase.Invalidated, 10f, 1.5f, 100, 1, 1) };
        return Complete(classes, State(records, windows));
    }
    private static ResidentialCapacityWindow Window(ResidentialClass value, ResidentialCapacityWindowPhase phase,
        float raw, float overbuild, long committed, long stock, long external)
    {
        long desired, maximum; ResidentialCapacityLedger.CalculateQuota(raw, overbuild, out desired, out maximum);
        return new ResidentialCapacityWindow(value, 10, phase, raw, overbuild, desired, maximum, committed, stock, external);
    }
    private static ResidentialCapacityLedgerState State(ResidentialCapacityRecord[] records, ResidentialCapacityWindow[] windows)
    {
        return new ResidentialCapacityLedgerState(Id, ResidentialCapacityLedger.PolicyName, ResidentialCapacityLedger.PolicyVersion, ResidentialCapacityLedger.PolicyHash,
            ResidentialNominalCapacity.PolicyName, ResidentialNominalCapacity.PolicyVersion, ResidentialNominalCapacity.PolicyHash, 10, 4, 2, 3, 1, records, windows);
    }
    private static ResidentialLotIdentity Identity(ushort building, string name)
    { return new ResidentialLotIdentity(building, (uint)(100 + building), Bits(building), Bits(-building), name, 1, 2, 2, 2); }
    private static Fixture Complete(ResidentialClassLedger classes, ResidentialCapacityLedgerState state)
    {
        byte[] classBytes = ClassBytes(classes);
        return new Fixture { Classes = classes, ClassBytes = classBytes, State = state, Bytes = Write(state, classBytes) };
    }
    private static byte[] ClassBytes(ResidentialClassLedger classes)
    { using (MemoryStream stream = new MemoryStream()) { ResidentialClassLedgerCodec.Write(stream, classes); return stream.ToArray(); } }
    private static byte[] Write(ResidentialCapacityLedgerState state, byte[] classes)
    { using (MemoryStream stream = new MemoryStream()) { ResidentialCapacityLedgerCodec.Write(stream, state, classes); return stream.ToArray(); } }
    private static ResidentialCapacityLedgerState Read(byte[] bytes, byte[] classes)
    { using (MemoryStream stream = new MemoryStream(bytes, false)) return ResidentialCapacityLedgerCodec.ReadBound(stream, classes); }

    private static void RoundTripAndRestoration()
    {
        Fixture f = Populated(); ResidentialCapacityLedgerState read = Read(f.Bytes, f.ClassBytes);
        Bytes(f.Bytes, Write(read, f.ClassBytes), "populated deterministic roundtrip");
        Equal(4L, read.Records.Length, "capacity subset of five classes accepted");
        Equal(3L, read.PublishedStockRevision, "persisted stock acknowledgment retained in DTO");
        Equal(1L, read.PublishedExternalRevision, "persisted external acknowledgment retained in DTO");
        Equal(100L, read.Windows[2].CommittedQ, "invalidated reconciliation overrun retained");
        Equal(Bits(20.125f), Bits(read.Windows[0].OpeningRaw), "raw exact float bits");
        Equal(Bits(1.5f), Bits(read.Windows[0].Overbuild), "overbuild exact float bits");
        ResidentialCapacityLedger restored = ResidentialCapacityLedger.Restore(Id, read, 55);
        Equal(0L, restored.Snapshot().PendingReservations, "no reservation restored");
        Check(!restored.Snapshot().PublicationCurrent, "restore requires a new graph acknowledgment");
        Equal(13L, restored.Snapshot().StockQ[0], "unavailable Low contributes no stock");
        Equal(13L, restored.Snapshot().StockQ[1], "completed Medium contributes stock");
        Equal(10L, restored.Snapshot().StockQ[2], "upgrading High contributes stock");
        ResidentialCapacityRecord[] reordered = f.State.Records; Array.Reverse(reordered);
        Bytes(f.Bytes, Write(State(reordered, f.State.Windows), f.ClassBytes), "record insertion order does not change wire");
        reordered[0] = null; Check(read.Records[0] != null, "state arrays remain defensive copies");

        ResidentialClassLedger emptyClasses = new ResidentialClassLedger(Id, 1, new byte[32]);
        Fixture empty = Complete(emptyClasses, State(new ResidentialCapacityRecord[0], new ResidentialCapacityWindow[3]));
        Bytes(empty.Bytes, Write(Read(empty.Bytes, empty.ClassBytes), empty.ClassBytes), "empty deterministic roundtrip");
        ResidentialCapacityWindow[] sparse = new ResidentialCapacityWindow[3];
        sparse[1] = Window(ResidentialClass.Medium, ResidentialCapacityWindowPhase.NotQualified,
            BitConverter.ToSingle(BitConverter.GetBytes(int.MinValue), 0), 1f, 0, 0, 0);
        Fixture negativeZero = Complete(emptyClasses, State(new ResidentialCapacityRecord[0], sparse));
        ResidentialCapacityLedgerState zeroRead = Read(negativeZero.Bytes, negativeZero.ClassBytes);
        Equal(int.MinValue, Bits(zeroRead.Windows[1].OpeningRaw), "negative zero bits survive");
        Check(zeroRead.Windows[0] == null && zeroRead.Windows[2] == null, "three null/nonnull slots preserved");
        ResidentialCapacityWindow[] floor = new ResidentialCapacityWindow[3];
        floor[0] = Window(ResidentialClass.Low, ResidentialCapacityWindowPhase.Exhausted, 6f, 1f, 31, 4, 2);
        Fixture floorFixture = Complete(emptyClasses, State(new ResidentialCapacityRecord[0], floor));
        Equal(31L, Read(floorFixture.Bytes, floorFixture.ClassBytes).Windows[0].CommittedQ, "legitimate exhausted replacement floor overrun preserved");
        int committedOffset = Describe(floorFixture.Bytes).Windows[0]["committed"];
        Reject(Edit(floorFixture.Bytes, delegate(byte[] b) { Put64(b, committedOffset, 32); }), floorFixture.ClassBytes,
            "model rejects exhausted excess above its declared floor allowance");
    }

    private static void BoundClassContent()
    {
        Fixture f = Populated();
        // Even an otherwise equivalent class ledger with an advanced generation
        // watermark has different exact bytes and must not pair with old capacity.
        f.Classes.CommitCreated(Identity(100, "Transient"), ResidentialClass.Low); f.Classes.Remove(100);
        byte[] stale = ClassBytes(f.Classes);
        Reject(f.Bytes, stale, "same GUID but different exact class payload");
        foreach (string change in new[] { "identity", "generation", "class", "origin", "missing" })
        {
            Fixture current = Populated(); ResidentialLotRecord[] lots = current.Classes.GetRecords();
            if (change == "missing")
            { ResidentialLotRecord[] shortened = new ResidentialLotRecord[lots.Length - 1]; Array.Copy(lots, 1, shortened, 0, shortened.Length); lots = shortened; }
            else
            {
                ResidentialLotRecord original = lots[0]; ResidentialLotIdentity identity = original.Identity;
                if (change == "identity") identity = new ResidentialLotIdentity(identity.BuildingId, identity.NativeBuildIndex + 1,
                    identity.OriginXBits, identity.OriginZBits, identity.PrefabName, identity.Service, identity.SubService, identity.Width, identity.Length);
                ResidentialClass cls = change == "class" ? ResidentialClass.High : original.DesignClass;
                lots[0] = new ResidentialLotRecord(identity, change == "generation" ? 100UL : original.Generation, cls, cls,
                    change == "origin" ? ClassOrigin.ImportedNative : original.Origin);
            }
            byte[] foreign = ClassBytes(new ResidentialClassLedger(Id, 1, new byte[32], 101, lots));
            byte[] rebound = (byte[])current.Bytes.Clone();
            using (SHA256 hash = SHA256.Create()) Array.Copy(hash.ComputeHash(foreign), 0, rebound, 30, 32);
            Recrc(rebound);
            // Blindly trusting the hash would accept this: it has been updated
            // to the supplied, well-formed class payload. Content validation must reject.
            Reject(rebound, foreign, "recomputed class hash cannot hide " + change + " mismatch");
            Throws<InvalidDataException>(delegate { Write(current.State, foreign); }, "writer validates paired class records");
        }
        Fixture valid = Populated();
        ResidentialCapacityRecord first = valid.State.Records[0];
        ResidentialCapacityRecord duplicateGeneration = new ResidentialCapacityRecord(Id, valid.State.Records[1].Identity, first.Generation,
            ResidentialClass.Medium, ResidentialNominalCategory.LowEco, 0, 1, ResidentialCapacityPhase.Completed, ClassOrigin.ImportedNative);
        Throws<InvalidDataException>(delegate { Write(State(new[] { first, first }, new ResidentialCapacityWindow[3]), valid.ClassBytes); }, "duplicate IDs on write");
        Throws<InvalidDataException>(delegate { Write(State(new[] { first, duplicateGeneration }, new ResidentialCapacityWindow[3]), valid.ClassBytes); }, "duplicate generations on write");
        byte[] brokenClasses = (byte[])valid.ClassBytes.Clone(); brokenClasses[20] ^= 1;
        Reject(valid.Bytes, brokenClasses, "malformed class payload");
        byte[] reboundBroken = (byte[])valid.Bytes.Clone();
        using (SHA256 hash = SHA256.Create()) Array.Copy(hash.ComputeHash(brokenClasses), 0, reboundBroken, 30, 32);
        Recrc(reboundBroken);
        Reject(reboundBroken, brokenClasses, "matching exact hash still requires a valid decoded class payload");
        byte[] duplicateClassGeneration = (byte[])valid.ClassBytes.Clone();
        int firstNameBytes = duplicateClassGeneration[109] | duplicateClassGeneration[110] << 8;
        int secondClassRecord = 74 + 37 + firstNameBytes;
        Array.Copy(duplicateClassGeneration, 80, duplicateClassGeneration, secondClassRecord + 6, 8); Recrc(duplicateClassGeneration);
        byte[] reboundDuplicate = (byte[])valid.Bytes.Clone();
        using (SHA256 hash = SHA256.Create()) Array.Copy(hash.ComputeHash(duplicateClassGeneration), 0, reboundDuplicate, 30, 32);
        Recrc(reboundDuplicate);
        Reject(reboundDuplicate, duplicateClassGeneration, "valid hashes/CRCs do not bypass class codec duplicate-generation validation");
    }

    private static void MalformedWire()
    {
        Fixture f = Populated(); Layout layout = Describe(f.Bytes);
        byte[] crc = (byte[])f.Bytes.Clone(); crc[20] ^= 1; Reject(crc, f.ClassBytes, "CRC corruption");
        for (int length = 0; length < f.Bytes.Length; length++)
        { byte[] shortBytes = new byte[length]; Array.Copy(f.Bytes, shortBytes, length); Reject(shortBytes, f.ClassBytes, "truncated wire"); }
        byte[] appended = new byte[f.Bytes.Length + 1]; Array.Copy(f.Bytes, appended, f.Bytes.Length);
        Reject(appended, f.ClassBytes, "trailing bytes without new CRC");
        Reject(Edit(f.Bytes, delegate(byte[] b) { b[0] ^= 1; }), f.ClassBytes, "magic with recomputed CRC");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put16(b, 8, 2); }), f.ClassBytes, "wire version with recomputed CRC");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put32(b, 10, b.Length - 1); }), f.ClassBytes, "declared length mismatch");
        Reject(Edit(f.Bytes, delegate(byte[] b) { b[14] ^= 1; }), f.ClassBytes, "class GUID mismatch");
        foreach (int offset in new[] { layout.AccountingName, layout.ProfileName })
            Reject(Edit(f.Bytes, delegate(byte[] b) { b[offset] = (byte)'X'; }), f.ClassBytes, "unknown identity name");
        foreach (int offset in new[] { layout.AccountingVersion, layout.ProfileVersion })
            Reject(Edit(f.Bytes, delegate(byte[] b) { Put32(b, offset, 999); }), f.ClassBytes, "unknown identity version");
        foreach (int offset in new[] { layout.AccountingHash, layout.ProfileHash })
            Reject(Edit(f.Bytes, delegate(byte[] b) { b[offset] ^= 1; }), f.ClassBytes, "unknown identity hash");
        foreach (long badCount in new[] { 65536L, uint.MaxValue })
            Reject(Edit(f.Bytes, delegate(byte[] b) { Put32(b, layout.Count, badCount); }), f.ClassBytes, "excessive record count");
        Reject(Edit(f.Bytes, delegate(byte[] b) { b[layout.WindowCount] = 4; }), f.ClassBytes, "window count");
        foreach (int offset in new[] { layout.FirstDay, layout.StockRevision, layout.ExternalRevision })
            Reject(Edit(f.Bytes, delegate(byte[] b) { Put64(b, offset, -1); }), f.ClassBytes, "negative revision/day");
        foreach (int offset in new[] { layout.PublishedStock, layout.PublishedExternal })
            foreach (long bad in new[] { -2L, 100L }) Reject(Edit(f.Bytes, delegate(byte[] b) { Put64(b, offset, bad); }), f.ClassBytes, "acknowledgment outside interval");
        Dictionary<string, int> record = layout.Records[0], second = layout.Records[1], window = layout.Windows[0];
        foreach (string field in new[] { "class", "category", "phase", "origin", "width", "length" })
            Reject(Edit(f.Bytes, delegate(byte[] b) { b[record[field]] = 0; }), f.ClassBytes, "zero record enum/geometry " + field);
        Reject(Edit(f.Bytes, delegate(byte[] b) { b[record["level"]] = 5; }), f.ClassBytes, "native level bounds");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put16(b, record["id"], 0); }), f.ClassBytes, "zero building ID");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put16(b, second["id"], 1); }), f.ClassBytes, "duplicate building ID");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put64(b, record["generation"], 0); }), f.ClassBytes, "zero generation");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Array.Copy(b, record["generation"], b, second["generation"], 8); }), f.ClassBytes, "duplicate generation");
        foreach (string field in new[] { "homes", "coefficient", "q" })
            Reject(Edit(f.Bytes, delegate(byte[] b) { Put64(b, record[field], 100); }), f.ClassBytes, "inconsistent native input/Q " + field);
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put32(b, record["x"], 0x7f800000); }), f.ClassBytes, "infinite origin");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put32(b, record["service"], -1); }), f.ClassBytes, "negative service");
        Reject(Edit(f.Bytes, delegate(byte[] b) { b[record["name"]] = 0xff; }), f.ClassBytes, "strict UTF8");
        foreach (int nameLength in new[] { 0, 1025, ushort.MaxValue })
            Reject(Edit(f.Bytes, delegate(byte[] b) { Put16(b, record["nameLength"], nameLength); }), f.ClassBytes, "invalid name length");
        Reject(Edit(f.Bytes, delegate(byte[] b) { b[window["present"]] = 2; }), f.ClassBytes, "presence boolean exactly 0/1");
        Reject(Edit(f.Bytes, delegate(byte[] b) { b[window["class"]] = 0; }), f.ClassBytes, "unknown window class");
        Reject(Edit(f.Bytes, delegate(byte[] b) { b[layout.Windows[1]["class"]] = 1; }), f.ClassBytes, "duplicate window class");
        Reject(Edit(f.Bytes, delegate(byte[] b) { b[window["phase"]] = 0; }), f.ClassBytes, "unknown window phase");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put64(b, window["day"], 9); }), f.ClassBytes, "window before first day");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put32(b, window["raw"], 0x7fc00000); }), f.ClassBytes, "NaN RAW");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put32(b, window["overbuild"], Bits(0.5f)); }), f.ClassBytes, "invalid overbuild");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put64(b, window["desired"], 81); }), f.ClassBytes, "quota recomputation");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put64(b, window["committed"], -1); }), f.ClassBytes, "negative committed");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put64(b, window["external"], 1); }), f.ClassBytes, "stale open external revision");
        Reject(Edit(f.Bytes, delegate(byte[] b) { Put64(b, window["stock"], 5); }), f.ClassBytes, "future opening revision");
        byte[] trailing = new byte[f.Bytes.Length + 1]; Array.Copy(f.Bytes, 0, trailing, 0, f.Bytes.Length - 4);
        Put32(trailing, 10, trailing.Length); Recrc(trailing);
        Reject(trailing, f.ClassBytes, "trailing byte even with valid length/CRC");
    }

    private static void SizeAndNameLimits()
    {
        ResidentialClassLedger classes = new ResidentialClassLedger(Id, 1, new byte[32]);
        ResidentialLotIdentity identity = Identity(1, new string('x', 1024)); ResidentialLotRecord lot = classes.CommitCreated(identity, ResidentialClass.Low);
        ResidentialCapacityRecord record = new ResidentialCapacityRecord(Id, identity, lot.Generation, lot.DesignClass,
            ResidentialNominalCategory.LowGeneric, 0, 1, ResidentialCapacityPhase.Completed, lot.Origin);
        Fixture maxName = Complete(classes, State(new[] { record }, new ResidentialCapacityWindow[3]));
        Equal(1024L, Read(maxName.Bytes, maxName.ClassBytes).Records[0].Identity.PrefabName.Length, "1024-byte name boundary");
        byte[] oversized = new byte[ResidentialCapacityLedgerCodec.MaximumPayloadBytes + 1];
        Array.Copy(maxName.Bytes, oversized, maxName.Bytes.Length - 4); Put32(oversized, 10, oversized.Length); Recrc(oversized);
        using (MemoryStream tooLarge = new MemoryStream(oversized, false))
        { Throws<InvalidDataException>(delegate { ResidentialCapacityLedgerCodec.ReadBound(tooLarge, maxName.ClassBytes); }, "16MiB reader bound"); Check(tooLarge.CanRead, "oversized reader left open"); }
        Throws<InvalidDataException>(delegate { Read(maxName.Bytes, new byte[ResidentialCapacityLedgerCodec.MaximumPayloadBytes + 1]); }, "class payload size bound");

        // Maximum count is representable and accepted, not merely rejected when
        // a small corrupt payload claims it. One-character names keep size bounded.
        const int count = 65535;
        ResidentialLotRecord[] lots = new ResidentialLotRecord[count]; ResidentialCapacityRecord[] records = new ResidentialCapacityRecord[count];
        for (int i = 0; i < count; i++)
        {
            ResidentialLotIdentity id = Identity((ushort)(i + 1), "x");
            lots[i] = new ResidentialLotRecord(id, (ulong)(i + 1), ResidentialClass.Low, ResidentialClass.Low, ClassOrigin.Selected);
            records[i] = new ResidentialCapacityRecord(Id, id, (ulong)(i + 1), ResidentialClass.Low,
                ResidentialNominalCategory.LowGeneric, 0, 1, ResidentialCapacityPhase.Completed, ClassOrigin.Selected);
        }
        ResidentialClassLedger maximum = new ResidentialClassLedger(Id, 1, new byte[32], 65536, lots);
        Fixture maxCount = Complete(maximum, State(records, new ResidentialCapacityWindow[3]));
        Equal(count, Read(maxCount.Bytes, maxCount.ClassBytes).Records.Length, "65535 records accepted");
        Check(maxCount.Bytes.Length <= ResidentialCapacityLedgerCodec.MaximumPayloadBytes, "maximum count payload bounded");

        // Capacity records have more fixed fields than class records: this pair
        // has a valid <16MiB class payload but would exceed the capacity limit.
        const int wideCount = 16300; string wideName = new string('z', 970);
        lots = new ResidentialLotRecord[wideCount]; records = new ResidentialCapacityRecord[wideCount];
        for (int i = 0; i < wideCount; i++)
        {
            ResidentialLotIdentity id = Identity((ushort)(i + 1), wideName);
            lots[i] = new ResidentialLotRecord(id, (ulong)(i + 1), ResidentialClass.Low, ResidentialClass.Low, ClassOrigin.Selected);
            records[i] = new ResidentialCapacityRecord(Id, id, (ulong)(i + 1), ResidentialClass.Low,
                ResidentialNominalCategory.LowGeneric, 0, 1, ResidentialCapacityPhase.Completed, ClassOrigin.Selected);
        }
        byte[] wideClasses = ClassBytes(new ResidentialClassLedger(Id, 1, new byte[32], wideCount + 1, lots));
        ResidentialCapacityLedgerState wideState = State(records, new ResidentialCapacityWindow[3]);
        using (MemoryStream target = new MemoryStream())
        {
            target.WriteByte(23);
            Throws<InvalidDataException>(delegate { ResidentialCapacityLedgerCodec.Write(target, wideState, wideClasses); }, "writer preflight 16MiB limit");
            Equal(1L, target.Length, "oversize validation touches no caller bytes"); Check(target.CanWrite, "oversized write leaves caller open");
        }
    }

    private static void StreamOwnership()
    {
        Fixture f = Populated();
        using (MemoryStream target = new MemoryStream())
        {
            target.WriteByte(19); ResidentialCapacityLedgerCodec.Write(target, f.State, f.ClassBytes);
            Check(target.CanWrite, "write leaves caller open"); Equal(19L, target.ToArray()[0], "write uses current position");
        }
        using (MemoryStream target = new MemoryStream())
        {
            target.WriteByte(17); byte[] before = target.ToArray();
            Throws<InvalidDataException>(delegate { ResidentialCapacityLedgerCodec.Write(target, f.State, new byte[78]); }, "invalid pairing before write");
            Check(target.CanWrite, "failed write leaves caller open"); Bytes(before, target.ToArray(), "failed validation writes no caller bytes");
        }
        using (ChunkedStream source = new ChunkedStream(f.Bytes))
        { Equal(4L, ResidentialCapacityLedgerCodec.ReadBound(source, f.ClassBytes).Records.Length, "short nonseekable reads"); Check(!source.Closed, "read leaves nonseekable caller open"); }
        using (ChunkedStream source = new ChunkedStream(new byte[18]))
        { Throws<InvalidDataException>(delegate { ResidentialCapacityLedgerCodec.ReadBound(source, f.ClassBytes); }, "bad read"); Check(!source.Closed, "failed read leaves caller open"); }
    }

    private static Layout Describe(byte[] bytes)
    {
        Layout l = new Layout();
        using (MemoryStream stream = new MemoryStream(bytes, false))
        {
            BinaryReader reader = new BinaryReader(stream); stream.Position = 62;
            int n = reader.ReadUInt16(); l.AccountingName = (int)stream.Position; stream.Position += n;
            l.AccountingVersion = (int)stream.Position; stream.Position += 4; l.AccountingHash = (int)stream.Position; stream.Position += 32;
            n = reader.ReadUInt16(); l.ProfileName = (int)stream.Position; stream.Position += n;
            l.ProfileVersion = (int)stream.Position; stream.Position += 4; l.ProfileHash = (int)stream.Position; stream.Position += 32;
            l.FirstDay = (int)stream.Position; l.StockRevision = l.FirstDay + 8; l.ExternalRevision = l.FirstDay + 16;
            l.PublishedStock = l.FirstDay + 24; l.PublishedExternal = l.FirstDay + 32; stream.Position += 40;
            l.Count = (int)stream.Position; int count = reader.ReadInt32(); l.WindowCount = (int)stream.Position; reader.ReadByte();
            for (int i = 0; i < count; i++)
            {
                int s = (int)stream.Position; Dictionary<string, int> r = new Dictionary<string, int>();
                r["id"] = s; r["generation"] = s + 6; r["class"] = s + 14; r["category"] = s + 15; r["level"] = s + 16;
                r["phase"] = s + 17; r["origin"] = s + 18; r["homes"] = s + 19; r["coefficient"] = s + 27; r["q"] = s + 35;
                r["x"] = s + 43; r["service"] = s + 51; r["width"] = s + 59; r["length"] = s + 60; r["nameLength"] = s + 61;
                stream.Position = s + 61; n = reader.ReadUInt16(); r["name"] = (int)stream.Position; stream.Position += n; l.Records.Add(r);
            }
            for (int i = 0; i < 3; i++)
            {
                int s = (int)stream.Position; Dictionary<string, int> w = new Dictionary<string, int>(); w["present"] = s;
                if (reader.ReadByte() != 0)
                {
                    w["class"] = s + 1; w["phase"] = s + 2; w["day"] = s + 3; w["raw"] = s + 11; w["overbuild"] = s + 15;
                    w["desired"] = s + 19; w["committed"] = s + 35; w["stock"] = s + 43; w["external"] = s + 51; stream.Position = s + 59;
                }
                l.Windows.Add(w);
            }
        }
        return l;
    }
    private static byte[] Edit(byte[] original, Action<byte[]> change)
    { byte[] result = (byte[])original.Clone(); change(result); Recrc(result); return result; }
    private static void Put16(byte[] bytes, int offset, int value) { bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); }
    private static void Put32(byte[] bytes, int offset, long value) { for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (8 * i)); }
    private static void Put64(byte[] bytes, int offset, long value) { for (int i = 0; i < 8; i++) bytes[offset + i] = (byte)(value >> (8 * i)); }
    private static void Recrc(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        for (int i = 0; i < bytes.Length - 4; i++) { crc ^= bytes[i]; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u); }
        Put32(bytes, bytes.Length - 4, ~crc);
    }
    private static int Bits(float value) { return BitConverter.ToInt32(BitConverter.GetBytes(value), 0); }
    private static void Reject(byte[] bytes, byte[] classes, string message) { Throws<InvalidDataException>(delegate { Read(bytes, classes); }, message); }
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Equal(long expected, long actual, string message) { Check(expected == actual, message + ": " + expected + " != " + actual); }
    private static void Bytes(byte[] expected, byte[] actual, string message)
    { Check(expected.Length == actual.Length, message + " length"); for (int i = 0; i < expected.Length; i++) if (expected[i] != actual[i]) throw new InvalidOperationException(message + " byte " + i); _checks++; }
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { _checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message); }

    private sealed class ChunkedStream : Stream
    {
        private readonly MemoryStream _inner;
        internal bool Closed;
        internal ChunkedStream(byte[] bytes) { _inner = new MemoryStream(bytes, false); }
        public override bool CanRead { get { return !Closed; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override int Read(byte[] buffer, int offset, int count) { return _inner.Read(buffer, offset, Math.Min(7, count)); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { Closed = true; if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
