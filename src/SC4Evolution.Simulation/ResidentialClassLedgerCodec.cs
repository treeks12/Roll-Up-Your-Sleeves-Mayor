using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SC4Evolution.Simulation
{
    /// <summary>Versioned bounded persistence for residential lot-class ledgers.</summary>
    public static class ResidentialClassLedgerCodec
    {
        private const ushort Version = 1;
        private const int MaximumPayloadBytes = 16 * 1024 * 1024;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SC4RCLS1");

        public static void Write(Stream stream, ResidentialClassLedger ledger)
        {
            if (stream == null || !stream.CanWrite) throw new ArgumentException("Writable stream required.", "stream");
            if (ledger == null) throw new ArgumentNullException("ledger");

            ResidentialLotRecord[] records = ledger.GetRecords();
            if (records.Length > ResidentialClassLedger.MaximumRecords)
                throw new InvalidDataException("Residential class ledger exceeds the record limit.");
            byte[][] prefabBytes = new byte[records.Length][];
            long totalLength = 78; // Header plus checksum.
            ulong maximumGeneration = 0;
            ushort previousBuildingId = 0;
            HashSet<ulong> generations = new HashSet<ulong>();
            int index;
            for (index = 0; index < records.Length; index++)
            {
                ResidentialLotRecord record = records[index];
                if (record == null || record.Identity == null)
                    throw new InvalidDataException("Residential class ledger contains an invalid record.");
                if (record.Identity.BuildingId <= previousBuildingId)
                    throw new InvalidDataException("Residential records are not in canonical building-ID order.");
                previousBuildingId = record.Identity.BuildingId;
                if (record.Generation == 0 || !generations.Add(record.Generation))
                    throw new InvalidDataException("Residential record generation is zero or duplicated.");
                ResidentialClassLedger.ValidateClass(record.DesignClass, "designClass");
                ResidentialClassLedger.ValidateClass(record.OccupantClass, "occupantClass");
                ResidentialClassLedger.ValidateOrigin(record.Origin, "origin");
                if (record.DesignClass != record.OccupantClass)
                    throw new InvalidDataException("Version 1 requires design and occupant classes to match.");
                if (record.Generation > maximumGeneration) maximumGeneration = record.Generation;
                prefabBytes[index] = record.Identity.GetPrefabNameBytes();
                if (prefabBytes[index].Length < 1 || prefabBytes[index].Length > ResidentialLotIdentity.MaximumPrefabNameBytes)
                    throw new InvalidDataException("Residential prefab name exceeds the UTF-8 byte limit.");
                totalLength += 37L + prefabBytes[index].Length;
                if (totalLength > MaximumPayloadBytes)
                    throw new InvalidDataException("Residential class ledger exceeds the payload limit.");
            }
            if (ledger.LedgerId == Guid.Empty || ledger.PolicyHash == null || ledger.PolicyHash.Length != 32 ||
                ledger.NextGeneration == 0 || ledger.NextGeneration <= maximumGeneration)
                throw new InvalidDataException("Residential class ledger header is invalid.");

            byte[] payload;
            using (MemoryStream buffer = new MemoryStream((int)totalLength))
            {
                BinaryWriter writer = new BinaryWriter(buffer, Encoding.UTF8);
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(ledger.LedgerId.ToByteArray());
                writer.Write(ledger.PolicyVersion);
                writer.Write(ledger.PolicyHash);
                writer.Write(ledger.NextGeneration);
                writer.Write((uint)records.Length);
                for (index = 0; index < records.Length; index++)
                {
                    ResidentialLotRecord record = records[index];
                    ResidentialLotIdentity identity = record.Identity;
                    writer.Write(identity.BuildingId);
                    writer.Write(identity.NativeBuildIndex);
                    writer.Write(record.Generation);
                    writer.Write((byte)record.Origin);
                    writer.Write((byte)record.DesignClass);
                    writer.Write((byte)record.OccupantClass);
                    writer.Write(identity.OriginXBits);
                    writer.Write(identity.OriginZBits);
                    writer.Write(identity.Service);
                    writer.Write(identity.SubService);
                    writer.Write(identity.Width);
                    writer.Write(identity.Length);
                    writer.Write((ushort)prefabBytes[index].Length);
                    writer.Write(prefabBytes[index]);
                }
                writer.Flush();
                byte[] body = buffer.ToArray();
                if (body.Length + 4 != totalLength)
                    throw new InvalidDataException("Residential class ledger size calculation failed.");
                uint checksum = Crc32(body, 0, body.Length);
                using (MemoryStream complete = new MemoryStream((int)totalLength))
                {
                    complete.Write(body, 0, body.Length);
                    BinaryWriter checksumWriter = new BinaryWriter(complete, Encoding.UTF8);
                    checksumWriter.Write(checksum);
                    checksumWriter.Flush();
                    payload = complete.ToArray();
                }
            }

            // Do all validation and serialization before touching the caller's stream.
            stream.Write(payload, 0, payload.Length);
        }

        public static ResidentialClassLedger Read(Stream stream)
        {
            if (stream == null || !stream.CanRead) throw new ArgumentException("Readable stream required.", "stream");
            byte[] payload = ReadBoundedPayload(stream);
            if (payload.Length < 78) throw new InvalidDataException("Truncated residential class ledger.");

            int bodyLength = payload.Length - 4;
            uint storedChecksum = ReadUInt32LittleEndian(payload, bodyLength);
            if (Crc32(payload, 0, bodyLength) != storedChecksum)
                throw new InvalidDataException("Residential class ledger checksum mismatch.");

            try
            {
                using (MemoryStream buffer = new MemoryStream(payload, 0, bodyLength, false))
                {
                    BinaryReader reader = new BinaryReader(buffer, ResidentialLotIdentity.StrictUtf8);
                    byte[] magic = reader.ReadBytes(Magic.Length);
                    if (magic.Length != Magic.Length || !BytesEqual(Magic, magic))
                        throw new InvalidDataException("Invalid residential class ledger signature.");
                    ushort version = reader.ReadUInt16();
                    if (version != Version) throw new InvalidDataException("Unsupported residential class ledger version.");
                    byte[] guidBytes = reader.ReadBytes(16);
                    if (guidBytes.Length != 16) throw new InvalidDataException("Truncated residential ledger ID.");
                    Guid ledgerId = new Guid(guidBytes);
                    if (ledgerId == Guid.Empty) throw new InvalidDataException("Residential ledger ID is empty.");
                    uint policyVersion = reader.ReadUInt32();
                    byte[] policyHash = reader.ReadBytes(32);
                    if (policyHash.Length != 32) throw new InvalidDataException("Truncated residential policy hash.");
                    ulong nextGeneration = reader.ReadUInt64();
                    uint rawCount = reader.ReadUInt32();
                    if (rawCount > ResidentialClassLedger.MaximumRecords)
                        throw new InvalidDataException("Residential class ledger record count exceeds the limit.");
                    int count = (int)rawCount;
                    long minimumBytes = (long)count * 38L;
                    if (minimumBytes > buffer.Length - buffer.Position)
                        throw new InvalidDataException("Truncated residential class records.");

                    ResidentialLotRecord[] records = new ResidentialLotRecord[count];
                    HashSet<ushort> buildingIds = new HashSet<ushort>();
                    HashSet<ulong> generations = new HashSet<ulong>();
                    ushort previousBuildingId = 0;
                    ulong maximumGeneration = 0;
                    int index;
                    for (index = 0; index < count; index++)
                    {
                        ushort buildingId = reader.ReadUInt16();
                        uint nativeBuildIndex = reader.ReadUInt32();
                        ulong generation = reader.ReadUInt64();
                        byte rawOrigin = reader.ReadByte();
                        byte rawDesignClass = reader.ReadByte();
                        byte rawOccupantClass = reader.ReadByte();
                        int originXBits = reader.ReadInt32();
                        int originZBits = reader.ReadInt32();
                        int service = reader.ReadInt32();
                        int subService = reader.ReadInt32();
                        byte width = reader.ReadByte();
                        byte length = reader.ReadByte();
                        ushort nameLength = reader.ReadUInt16();
                        if (buildingId == 0 || buildingId <= previousBuildingId || !buildingIds.Add(buildingId))
                            throw new InvalidDataException("Residential building IDs are zero, duplicated, or unsorted.");
                        previousBuildingId = buildingId;
                        if (generation == 0 || !generations.Add(generation))
                            throw new InvalidDataException("Residential record generation is zero or duplicated.");
                        if (!IsValidOrigin(rawOrigin)) throw new InvalidDataException("Unknown residential class origin.");
                        if (!IsValidClass(rawDesignClass) || !IsValidClass(rawOccupantClass))
                            throw new InvalidDataException("Unknown residential class value.");
                        if (rawDesignClass != rawOccupantClass)
                            throw new InvalidDataException("Version 1 requires design and occupant classes to match.");
                        if (nameLength < 1 || nameLength > ResidentialLotIdentity.MaximumPrefabNameBytes)
                            throw new InvalidDataException("Invalid residential prefab-name length.");
                        byte[] nameBytes = reader.ReadBytes(nameLength);
                        if (nameBytes.Length != nameLength) throw new InvalidDataException("Truncated residential prefab name.");
                        string prefabName = ResidentialLotIdentity.StrictUtf8.GetString(nameBytes);
                        ResidentialLotIdentity identity = new ResidentialLotIdentity(buildingId, nativeBuildIndex,
                            originXBits, originZBits, prefabName, service, subService, width, length);
                        records[index] = new ResidentialLotRecord(identity, generation,
                            (ResidentialClass)rawDesignClass, (ResidentialClass)rawOccupantClass,
                            (ClassOrigin)rawOrigin);
                        if (generation > maximumGeneration) maximumGeneration = generation;
                    }

                    if (buffer.Position != buffer.Length)
                        throw new InvalidDataException("Trailing bytes after residential class records.");
                    if (nextGeneration == 0 || nextGeneration <= maximumGeneration)
                        throw new InvalidDataException("Next generation does not exceed stored generations.");
                    return new ResidentialClassLedger(ledgerId, policyVersion, policyHash, nextGeneration, records);
                }
            }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated residential class ledger.", error); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid UTF-8 in residential class ledger.", error); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid residential class ledger values.", error); }
        }

        private static byte[] ReadBoundedPayload(Stream stream)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                while (true)
                {
                    int read = stream.Read(chunk, 0, chunk.Length);
                    if (read == 0) break;
                    if (buffer.Length + read > MaximumPayloadBytes)
                        throw new InvalidDataException("Residential class ledger exceeds the payload limit.");
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
        }

        private static bool IsValidClass(byte value)
        {
            return value >= (byte)ResidentialClass.Low && value <= (byte)ResidentialClass.High;
        }

        private static bool IsValidOrigin(byte value)
        {
            return value == (byte)ClassOrigin.ImportedNative || value == (byte)ClassOrigin.Selected;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            int index;
            for (index = 0; index < left.Length; index++)
                if (left[index] != right[index]) return false;
            return true;
        }

        private static uint ReadUInt32LittleEndian(byte[] bytes, int offset)
        {
            return (uint)bytes[offset] | ((uint)bytes[offset + 1] << 8) |
                ((uint)bytes[offset + 2] << 16) | ((uint)bytes[offset + 3] << 24);
        }

        private static uint Crc32(byte[] bytes, int offset, int count)
        {
            uint crc = uint.MaxValue;
            int index;
            for (index = offset; index < offset + count; index++)
            {
                crc ^= bytes[index];
                int bit;
                for (bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1u) == 0u ? 0u : 0xEDB88320u);
            }
            return ~crc;
        }
    }
}
