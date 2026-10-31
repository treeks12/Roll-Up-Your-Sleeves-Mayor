using System;
using System.IO;
using System.Text;

namespace SC4Evolution.Simulation
{
    /// <summary>Bounded deterministic v1 override storage. Invalid data throws;
    /// deciding how to retain unavailable original bytes belongs to the caller.</summary>
    public static class CivicFundingLedgerCodec
    {
        public const int MaximumPayloadBytes = 16 * 1024 * 1024;
        private const ushort Version = 1;
        private const int HeaderAndCrcBytes = 34;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SC4CFND1");
        public static void Write(Stream stream, CivicFundingLedger ledger)
        {
            if (stream == null || !stream.CanWrite) throw new ArgumentException("Writable stream required.", "stream");
            if (ledger == null) throw new ArgumentNullException("ledger");
            CivicFundingRecord[] records = ledger.GetRecords();
            if (ledger.LedgerId == Guid.Empty || records.Length > CivicFundingLedger.MaximumRecords) throw new InvalidDataException("Invalid civic funding ledger header.");
            byte[][] prefabs = new byte[records.Length][], types = new byte[records.Length][];
            long length = HeaderAndCrcBytes; ushort previous = 0;
            for (int i = 0; i < records.Length; i++)
            {
                CivicFundingRecord record = records[i];
                if (record == null || record.Identity == null || record.Identity.BuildingId <= previous) throw new InvalidDataException("Invalid or unordered civic funding record.");
                previous = record.Identity.BuildingId;
                CivicFacilityIdentity.ValidateArchetype(record.Identity.Archetype);
                CivicFundingRecord.ValidatePercent(record.CapacityFundingPercent, "capacityFundingPercent");
                CivicFundingRecord.ValidatePercent(record.CoverageFundingPercent, "coverageFundingPercent");
                if (!record.CapacityFundingPercent.HasValue && !record.CoverageFundingPercent.HasValue) throw new InvalidDataException("An inherited-only record cannot be persisted.");
                prefabs[i] = EncodeName(record.Identity.PrefabName); types[i] = EncodeName(record.Identity.AiTypeName);
                length = checked(length + 15L + prefabs[i].Length + types[i].Length);
                if (length > MaximumPayloadBytes) throw new InvalidDataException("Civic funding payload exceeds 16 MiB.");
            }
            byte[] payload;
            using (MemoryStream buffer = new MemoryStream((int)length))
            {
                BinaryWriter writer = new BinaryWriter(buffer, CivicFacilityIdentity.StrictUtf8);
                writer.Write(Magic); writer.Write(Version); writer.Write(ledger.LedgerId.ToByteArray()); writer.Write((uint)records.Length);
                for (int i = 0; i < records.Length; i++)
                {
                    CivicFundingRecord r = records[i]; writer.Write(r.Identity.BuildingId); writer.Write(r.Identity.NativeBuildIndex); writer.Write((byte)r.Identity.Archetype);
                    writer.Write((short)(r.CapacityFundingPercent.HasValue ? r.CapacityFundingPercent.Value : -1));
                    writer.Write((short)(r.CoverageFundingPercent.HasValue ? r.CoverageFundingPercent.Value : -1));
                    writer.Write((ushort)prefabs[i].Length); writer.Write(prefabs[i]); writer.Write((ushort)types[i].Length); writer.Write(types[i]);
                }
                writer.Flush(); byte[] body = buffer.ToArray();
                if (body.Length + 4L != length) throw new InvalidDataException("Civic funding payload length calculation failed.");
                writer.Write(Crc32(body, body.Length)); writer.Flush(); payload = buffer.ToArray();
            }
            // Preflight and serialization complete before the first caller write.
            stream.Write(payload, 0, payload.Length);
        }
        public static CivicFundingLedger Read(Stream stream)
        {
            if (stream == null || !stream.CanRead) throw new ArgumentException("Readable stream required.", "stream");
            byte[] payload = ReadBounded(stream);
            if (payload.Length < HeaderAndCrcBytes) throw new InvalidDataException("Truncated civic funding payload.");
            int bodyLength = payload.Length - 4;
            uint stored = (uint)(payload[bodyLength] | payload[bodyLength + 1] << 8 | payload[bodyLength + 2] << 16 | payload[bodyLength + 3] << 24);
            if (stored != Crc32(payload, bodyLength)) throw new InvalidDataException("Civic funding checksum mismatch.");
            try
            {
                using (MemoryStream buffer = new MemoryStream(payload, 0, bodyLength, false))
                {
                    BinaryReader reader = new BinaryReader(buffer, CivicFacilityIdentity.StrictUtf8);
                    byte[] magic = ReadExact(reader, 8); for (int i = 0; i < 8; i++) if (magic[i] != Magic[i]) throw new InvalidDataException("Invalid civic funding signature.");
                    if (reader.ReadUInt16() != Version) throw new InvalidDataException("Unsupported civic funding version.");
                    CivicFundingLedger ledger = new CivicFundingLedger(new Guid(ReadExact(reader, 16)));
                    uint count = reader.ReadUInt32();
                    if (count > CivicFundingLedger.MaximumRecords || (long)count * 17L > buffer.Length - buffer.Position) throw new InvalidDataException("Civic funding record count is invalid or truncated.");
                    ushort previous = 0;
                    for (uint i = 0; i < count; i++)
                    {
                        ushort id = reader.ReadUInt16(); uint buildIndex = reader.ReadUInt32(); CivicFundingArchetype archetype = (CivicFundingArchetype)reader.ReadByte();
                        int? capacity = ReadPercent(reader), coverage = ReadPercent(reader);
                        if (id == 0 || id <= previous) throw new InvalidDataException("Civic facility IDs are zero, duplicated or unordered."); previous = id;
                        if (!capacity.HasValue && !coverage.HasValue) throw new InvalidDataException("Inherited-only civic funding record is invalid.");
                        string prefab = ReadName(reader), aiType = ReadName(reader);
                        ledger.Set(new CivicFacilityIdentity(id, buildIndex, prefab, aiType, archetype), capacity, coverage);
                    }
                    if (buffer.Position != buffer.Length) throw new InvalidDataException("Trailing civic funding bytes.");
                    return ledger;
                }
            }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated civic funding payload.", error); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid civic funding UTF-8.", error); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid civic funding values.", error); }
        }
        private static int? ReadPercent(BinaryReader reader)
        {
            short value = reader.ReadInt16(); if (value == -1) return null;
            if (value < 0 || value > 120) throw new InvalidDataException("Civic funding percent is outside 0..120 or inherit."); return value;
        }
        private static byte[] EncodeName(string value)
        {
            byte[] result = CivicFacilityIdentity.StrictUtf8.GetBytes(value);
            if (result.Length < 1 || result.Length > CivicFacilityIdentity.MaximumNameUtf8Bytes) throw new InvalidDataException("Civic funding name exceeds its UTF-8 bounds."); return result;
        }
        private static string ReadName(BinaryReader reader)
        {
            int count = reader.ReadUInt16(); if (count < 1 || count > CivicFacilityIdentity.MaximumNameUtf8Bytes) throw new InvalidDataException("Civic funding name length is invalid.");
            byte[] bytes = ReadExact(reader, count);
            ValidateUtf8(bytes);
            return CivicFacilityIdentity.StrictUtf8.GetString(bytes);
        }
        // Validate canonical UTF-8 independently of legacy Mono decoder fallback behavior.
        private static void ValidateUtf8(byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                int lead = bytes[i], following, minimum = 0x80, maximum = 0xbf;
                if (lead <= 0x7f) continue;
                if (lead >= 0xc2 && lead <= 0xdf) following = 1;
                else if (lead >= 0xe0 && lead <= 0xef)
                { following = 2; if (lead == 0xe0) minimum = 0xa0; if (lead == 0xed) maximum = 0x9f; }
                else if (lead >= 0xf0 && lead <= 0xf4)
                { following = 3; if (lead == 0xf0) minimum = 0x90; if (lead == 0xf4) maximum = 0x8f; }
                else throw new InvalidDataException("Invalid civic funding UTF-8 lead byte.");
                if (i + following >= bytes.Length || bytes[i + 1] < minimum || bytes[i + 1] > maximum)
                    throw new InvalidDataException("Invalid civic funding UTF-8 sequence.");
                for (int j = 2; j <= following; j++)
                    if (bytes[i + j] < 0x80 || bytes[i + j] > 0xbf) throw new InvalidDataException("Invalid civic funding UTF-8 continuation.");
                i += following;
            }
        }
        private static byte[] ReadExact(BinaryReader reader, int count)
        { byte[] result = reader.ReadBytes(count); if (result.Length != count) throw new EndOfStreamException(); return result; }
        private static byte[] ReadBounded(Stream stream)
        {
            if (stream.CanSeek && stream.Length - stream.Position > MaximumPayloadBytes) throw new InvalidDataException("Civic funding payload exceeds 16 MiB.");
            using (MemoryStream result = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                while (true)
                {
                    int count = stream.Read(chunk, 0, chunk.Length); if (count == 0) return result.ToArray();
                    if (result.Length + count > MaximumPayloadBytes) throw new InvalidDataException("Civic funding payload exceeds 16 MiB."); result.Write(chunk, 0, count);
                }
            }
        }
        private static uint Crc32(byte[] bytes, int count)
        {
            uint crc = uint.MaxValue;
            for (int i = 0; i < count; i++) { crc ^= bytes[i]; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u); }
            return ~crc;
        }
    }
}
