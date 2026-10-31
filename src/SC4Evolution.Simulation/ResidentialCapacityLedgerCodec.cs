using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SC4Evolution.Simulation
{
    /// <summary>Data-only capacity state, bound to the exact class payload captured with it.
    /// CRC/hash detect corruption and stale pairings; they do not authenticate an untrusted writer.
    /// No reservation/session token or native creation authority is serialized or restored.</summary>
    public static class ResidentialCapacityLedgerCodec
    {
        public const int MaximumPayloadBytes = 16 * 1024 * 1024;
        private const ushort Version = 1;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SC4RCAP1");
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static void Write(Stream stream, ResidentialCapacityLedgerState state, byte[] classLedgerPayload)
        {
            if (stream == null || !stream.CanWrite) throw new ArgumentException("Writable stream required.", "stream");
            if (state == null) throw new ArgumentNullException("state");
            byte[] classBytes = CopyClassPayload(classLedgerPayload);
            ResidentialCapacityRecord[] records = state.Records;
            ResidentialCapacityWindow[] windows = state.Windows;
            ValidateBoundState(state, classBytes);
            Array.Sort(records, delegate(ResidentialCapacityRecord a, ResidentialCapacityRecord b) { return a.Identity.BuildingId.CompareTo(b.Identity.BuildingId); });
            byte[] accountingName = NameBytes(state.AccountingPolicyName), profileName = NameBytes(state.ProfileName);
            byte[][] names = new byte[records.Length][];
            // Includes magic/version/length, GUID, class hash, policy/profile names,
            // versions/hashes, five watermarks, record count, window count and CRC.
            long length = checked(8L + 2L + 4L + 16L + 32L + 2L + accountingName.Length + 4L + 32L +
                2L + profileName.Length + 4L + 32L + 40L + 4L + 1L + 4L);
            for (int i = 0; i < records.Length; i++)
            {
                names[i] = NameBytes(records[i].Identity.PrefabName);
                length = checked(length + 63L + names[i].Length);
                RequireSize(length);
            }
            for (int i = 0; i < 3; i++) length = checked(length + (windows[i] == null ? 1L : 59L));
            RequireSize(length);
            byte[] payload;
            using (MemoryStream buffer = new MemoryStream((int)length))
            {
                // Only the internal buffer is owned; never dispose caller streams.
                BinaryWriter writer = new BinaryWriter(buffer, Utf8);
                writer.Write(Magic); writer.Write(Version); writer.Write((uint)length);
                writer.Write(state.ClassLedgerId.ToByteArray()); writer.Write(Sha256(classBytes));
                WriteName(writer, accountingName); writer.Write(state.AccountingPolicyVersion); writer.Write(state.AccountingPolicyHash);
                WriteName(writer, profileName); writer.Write(state.ProfileVersion); writer.Write(state.ProfileHash);
                writer.Write(state.FirstPlanningDay); writer.Write(state.StockRevision); writer.Write(state.ExternalRevision);
                writer.Write(state.PublishedStockRevision); writer.Write(state.PublishedExternalRevision);
                writer.Write((uint)records.Length); writer.Write((byte)3);
                for (int i = 0; i < records.Length; i++)
                {
                    ResidentialCapacityRecord r = records[i]; ResidentialLotIdentity id = r.Identity;
                    writer.Write(id.BuildingId); writer.Write(id.NativeBuildIndex); writer.Write(r.Generation);
                    writer.Write((byte)r.Class); writer.Write((byte)r.Category); writer.Write((byte)r.NativeLevel);
                    writer.Write((byte)r.Phase); writer.Write((byte)r.Origin);
                    writer.Write(r.HomeCount); writer.Write(ResidentialNominalCapacity.GetQuartersPerHome(r.Category)); writer.Write(r.CapacityQ);
                    writer.Write(id.OriginXBits); writer.Write(id.OriginZBits); writer.Write(id.Service); writer.Write(id.SubService);
                    writer.Write(id.Width); writer.Write(id.Length); WriteName(writer, names[i]);
                }
                for (int i = 0; i < 3; i++)
                {
                    ResidentialCapacityWindow w = windows[i]; writer.Write((byte)(w == null ? 0 : 1));
                    if (w == null) continue;
                    writer.Write((byte)w.Class); writer.Write((byte)w.Phase); writer.Write(w.Day);
                    writer.Write(FloatBits(w.OpeningRaw)); writer.Write(FloatBits(w.Overbuild));
                    writer.Write(w.DesiredQ); writer.Write(w.DesiredMaximumQ); writer.Write(w.CommittedQ);
                    writer.Write(w.OpeningStockRevision); writer.Write(w.OpeningExternalRevision);
                }
                writer.Flush();
                byte[] body = buffer.ToArray();
                if (checked(body.Length + 4L) != length) throw new InvalidDataException("Capacity payload size calculation failed.");
                writer.Write(Crc32(body, body.Length)); writer.Flush(); payload = buffer.ToArray();
            }
            // Entire validation/serialization completes before the first caller write.
            stream.Write(payload, 0, payload.Length);
        }

        public static ResidentialCapacityLedgerState ReadBound(Stream stream, byte[] classLedgerPayload)
        {
            if (stream == null || !stream.CanRead) throw new ArgumentException("Readable stream required.", "stream");
            byte[] classBytes = CopyClassPayload(classLedgerPayload);
            byte[] payload = ReadBounded(stream);
            if (payload.Length < 14 + 4) throw new InvalidDataException("Truncated capacity payload.");
            int bodyLength = payload.Length - 4;
            uint storedCrc = (uint)payload[bodyLength] | ((uint)payload[bodyLength + 1] << 8) |
                ((uint)payload[bodyLength + 2] << 16) | ((uint)payload[bodyLength + 3] << 24);
            if (Crc32(payload, bodyLength) != storedCrc) throw new InvalidDataException("Capacity checksum mismatch.");
            try
            {
                using (MemoryStream buffer = new MemoryStream(payload, 0, bodyLength, false))
                {
                    BinaryReader reader = new BinaryReader(buffer, Utf8);
                    if (!Same(ReadExact(reader, 8), Magic)) throw new InvalidDataException("Invalid capacity signature.");
                    if (reader.ReadUInt16() != Version) throw new InvalidDataException("Unsupported capacity wire version.");
                    if (reader.ReadUInt32() != payload.Length) throw new InvalidDataException("Capacity payload length differs from its header.");
                    Guid classId = new Guid(ReadExact(reader, 16));
                    if (!Same(ReadExact(reader, 32), Sha256(classBytes))) throw new InvalidDataException("Capacity payload is bound to another exact class payload.");
                    string accountingName = ReadName(reader); uint accountingVersion = reader.ReadUInt32(); byte[] accountingHash = ReadExact(reader, 32);
                    string profileName = ReadName(reader); uint profileVersion = reader.ReadUInt32(); byte[] profileHash = ReadExact(reader, 32);
                    long firstDay = reader.ReadInt64(), stockRevision = reader.ReadInt64(), externalRevision = reader.ReadInt64();
                    long publishedStock = reader.ReadInt64(), publishedExternal = reader.ReadInt64();
                    uint rawCount = reader.ReadUInt32();
                    if (rawCount > ResidentialClassLedger.MaximumRecords || checked((long)rawCount * 64L + 3L) > buffer.Length - buffer.Position - 1L)
                        throw new InvalidDataException("Capacity record count is excessive or truncated.");
                    if (reader.ReadByte() != 3) throw new InvalidDataException("Exactly three ordered capacity window slots are required.");
                    ResidentialCapacityRecord[] records = new ResidentialCapacityRecord[(int)rawCount];
                    ushort previous = 0; HashSet<ulong> generations = new HashSet<ulong>();
                    for (int i = 0; i < records.Length; i++)
                    {
                        ushort building = reader.ReadUInt16(); uint buildIndex = reader.ReadUInt32(); ulong generation = reader.ReadUInt64();
                        ResidentialClass lotClass = (ResidentialClass)reader.ReadByte(); ResidentialNominalCategory category = (ResidentialNominalCategory)reader.ReadByte();
                        int nativeLevel = reader.ReadByte(); ResidentialCapacityPhase phase = (ResidentialCapacityPhase)reader.ReadByte(); ClassOrigin origin = (ClassOrigin)reader.ReadByte();
                        long homes = reader.ReadInt64(), coefficientQ = reader.ReadInt64(), capacityQ = reader.ReadInt64();
                        int x = reader.ReadInt32(), z = reader.ReadInt32(), service = reader.ReadInt32(), subService = reader.ReadInt32();
                        byte width = reader.ReadByte(), length = reader.ReadByte(); string name = ReadName(reader);
                        if (building <= previous || generation == 0 || !generations.Add(generation)) throw new InvalidDataException("Capacity records are duplicated, unsorted or have zero identity/generation.");
                        previous = building;
                        ResidentialLotIdentity identity = new ResidentialLotIdentity(building, buildIndex, x, z, name, service, subService, width, length);
                        ResidentialCapacityRecord record = new ResidentialCapacityRecord(classId, identity, generation, lotClass, category, nativeLevel, homes, phase, origin);
                        if (coefficientQ != ResidentialNominalCapacity.GetQuartersPerHome(category) || capacityQ != record.CapacityQ)
                            throw new InvalidDataException("Capacity coefficient/Q does not equal the known nominal profile computation.");
                        records[i] = record;
                    }
                    ResidentialCapacityWindow[] windows = new ResidentialCapacityWindow[3];
                    for (int i = 0; i < 3; i++)
                    {
                        byte present = reader.ReadByte();
                        if (present == 0) continue;
                        if (present != 1) throw new InvalidDataException("Invalid capacity window presence byte.");
                        ResidentialClass lotClass = (ResidentialClass)reader.ReadByte(); ResidentialCapacityWindowPhase phase = (ResidentialCapacityWindowPhase)reader.ReadByte();
                        if ((int)lotClass != i + 1) throw new InvalidDataException("Capacity window classes are duplicated, unordered or unknown.");
                        long day = reader.ReadInt64(); float raw = FromBits(reader.ReadInt32()), overbuild = FromBits(reader.ReadInt32());
                        long desired = reader.ReadInt64(), maximum = reader.ReadInt64(), committed = reader.ReadInt64();
                        long openingStock = reader.ReadInt64(), openingExternal = reader.ReadInt64();
                        windows[i] = new ResidentialCapacityWindow(lotClass, day, phase, raw, overbuild, desired, maximum, committed, openingStock, openingExternal);
                    }
                    if (buffer.Position != buffer.Length) throw new InvalidDataException("Trailing bytes after capacity state.");
                    ResidentialCapacityLedgerState state = new ResidentialCapacityLedgerState(classId, accountingName, accountingVersion, accountingHash,
                        profileName, profileVersion, profileHash, firstDay, stockRevision, externalRevision, publishedStock, publishedExternal, records, windows);
                    ValidateBoundState(state, classBytes);
                    return state;
                }
            }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated capacity payload.", error); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid capacity values or UTF-8.", error); }
            catch (OverflowException error) { throw new InvalidDataException("Capacity arithmetic overflow.", error); }
        }

        private static void ValidateBoundState(ResidentialCapacityLedgerState state, byte[] classBytes)
        {
            try
            {
                // Restore checks duplicate IDs/generations, window slots/revisions and
                // data invariants, with no token/publication authorization restored.
                ResidentialCapacityLedger.Restore(state.ClassLedgerId, state, 0);
                ResidentialClassLedger classes;
                using (MemoryStream stream = new MemoryStream(classBytes, false)) classes = ResidentialClassLedgerCodec.Read(stream);
                if (classes.LedgerId != state.ClassLedgerId) throw new InvalidDataException("Capacity/class GUID mismatch.");
                foreach (ResidentialCapacityRecord record in state.Records)
                {
                    ResidentialLotRecord lot;
                    // DTO V1 Origin mirrors ClassOrigin. Importing capacity for
                    // an already Selected class must retain that Selected origin.
                    if (!classes.TryGet(record.Identity, out lot) || lot.Generation != record.Generation || lot.DesignClass != record.Class ||
                        lot.OccupantClass != record.Class || lot.Origin != record.Origin)
                        throw new InvalidDataException("Capacity record lacks an exact matching class identity/generation/class/origin.");
                }
                // Class records without capacity are allowed: native reconciliation owns gaps.
            }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid capacity state.", error); }
            catch (OverflowException error) { throw new InvalidDataException("Capacity state overflow.", error); }
        }

        private static byte[] CopyClassPayload(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException("classLedgerPayload");
            if (payload.Length < 78 || payload.Length > MaximumPayloadBytes) throw new InvalidDataException("Class payload is absent, truncated or excessive.");
            return (byte[])payload.Clone();
        }
        private static byte[] ReadBounded(Stream stream)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[8192]; int count;
                while ((count = stream.Read(chunk, 0, chunk.Length)) != 0)
                { RequireSize(checked(buffer.Length + count)); buffer.Write(chunk, 0, count); }
                return buffer.ToArray();
            }
        }
        private static void RequireSize(long size)
        { if (size > MaximumPayloadBytes) throw new InvalidDataException("Capacity payload exceeds 16 MiB."); }
        private static byte[] NameBytes(string value)
        {
            byte[] bytes = Utf8.GetBytes(value);
            if (bytes.Length < 1 || bytes.Length > 1024) throw new InvalidDataException("Capacity name must contain 1..1024 strict UTF-8 bytes.");
            return bytes;
        }
        private static void WriteName(BinaryWriter writer, byte[] bytes) { writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        private static string ReadName(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            if (length < 1 || length > 1024) throw new InvalidDataException("Invalid capacity name byte length.");
            return Utf8.GetString(ReadExact(reader, length));
        }
        private static byte[] ReadExact(BinaryReader reader, int length)
        { byte[] bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return bytes; }
        private static byte[] Sha256(byte[] bytes) { using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(bytes); }
        private static int FloatBits(float value) { return BitConverter.ToInt32(BitConverter.GetBytes(value), 0); }
        private static float FromBits(int value) { return BitConverter.ToSingle(BitConverter.GetBytes(value), 0); }
        private static bool Same(byte[] a, byte[] b)
        { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
        private static uint Crc32(byte[] bytes, int count)
        {
            uint crc = uint.MaxValue;
            for (int i = 0; i < count; i++) { crc ^= bytes[i]; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u); }
            return ~crc;
        }
    }
}
